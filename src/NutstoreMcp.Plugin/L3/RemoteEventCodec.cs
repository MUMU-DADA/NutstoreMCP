using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace NutstoreMcp.Plugin.L3;

/// <summary>
/// 解析 <c>/cmd/getEventsV4</c> 的单页响应。
///
/// <para>
/// 这个类型刻意不引用坚果云客户端程序集，也不把事件反序列化成
/// <c>ServerAppCommand</c>。调用方可以在默认域里负责取响应头和会话，
/// 然后把正文交给这里；因此单元测试不需要启动客户端或访问网络。
/// </para>
/// </summary>
public static class RemoteEventCodec
{
    private const int DefaultMaxEvents = 1024;
    private const int DefaultMaxLineBytes = 256 * 1024;
    private const int DefaultMaxPayloadBytes = 512 * 1024;
    private const int DefaultMaxResponseBytes = 1024 * 1024;
    private const int DefaultMaxDecodedBytes = 1024 * 1024;
    private const int MaxJsonDepth = 64;

    /// <summary>单页解析的内存预算。</summary>
    public sealed class Limits
    {
        public Limits(
            int maxEvents = DefaultMaxEvents,
            int maxLineBytes = DefaultMaxLineBytes,
            int maxPayloadBytes = DefaultMaxPayloadBytes,
            int maxResponseBytes = DefaultMaxResponseBytes,
            int maxDecodedBytes = DefaultMaxDecodedBytes)
        {
            if (maxEvents <= 0) throw new ArgumentOutOfRangeException(nameof(maxEvents));
            if (maxLineBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxLineBytes));
            if (maxPayloadBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxPayloadBytes));
            if (maxResponseBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxResponseBytes));
            if (maxDecodedBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxDecodedBytes));

            MaxEvents = maxEvents;
            MaxLineBytes = maxLineBytes;
            MaxPayloadBytes = maxPayloadBytes;
            MaxResponseBytes = maxResponseBytes;
            MaxDecodedBytes = maxDecodedBytes;
        }

        public int MaxEvents { get; }
        public int MaxLineBytes { get; }
        public int MaxPayloadBytes { get; }
        public int MaxResponseBytes { get; }
        public int MaxDecodedBytes { get; }

        public static Limits Default { get; } = new();
    }

    /// <summary>单页的自有 DTO；不携带客户端响应对象或会话信息。</summary>
    public sealed class Page
    {
        internal Page(
            long cursorIn,
            long cursorOut,
            bool hasMore,
            IReadOnlyList<string> events,
            int payloadBytes)
        {
            CursorIn = cursorIn;
            CursorOut = cursorOut;
            HasMore = hasMore;
            Events = events;
            EventCount = events.Count;
            PayloadBytes = payloadBytes;
        }

        public long CursorIn { get; }
        public long CursorOut { get; }
        public bool HasMore { get; }
        public IReadOnlyList<string> Events { get; }
        public int EventCount { get; }
        public int PayloadBytes { get; }
    }

    /// <summary>响应不符合事件协议时抛出。</summary>
    public sealed class ProtocolException : FormatException
    {
        public ProtocolException(string message) : base(message) { }
    }

    /// <summary>正文超过单页预算时抛出。</summary>
    public sealed class LimitException : InvalidOperationException
    {
        public LimitException(string message) : base(message) { }
    }

    /// <summary>
    /// 解析一页 NDJSON。
    ///
    /// <paramref name="latestEventHeader"/> 是 <c>x-latest-event</c> 的原值；
    /// <c>null</c> 表示缺少头。<paramref name="moreDataHeader"/> 同样使用
    /// null 表示头不存在，因此空字符串仍表示有下一页。
    /// </summary>
    public static Page ParsePage(
        long cursorIn,
        string? latestEventHeader,
        string? moreDataHeader,
        Stream body,
        string? contentEncoding = null,
        Limits? limits = null)
    {
        if (cursorIn < 0) throw new ArgumentOutOfRangeException(nameof(cursorIn));
        if (body is null) throw new ArgumentNullException(nameof(body));

        var cursorOut = ParseCursor(latestEventHeader);
        var budget = limits ?? Limits.Default;
        var hasMore = moreDataHeader is not null;
        if (cursorOut < cursorIn || (hasMore && cursorOut == cursorIn))
            throw new ProtocolException("事件分页游标未前进或发生回退。");
        var events = new List<string>();
        var payloadBytes = 0;

        using var responseBudget = new BudgetReadStream(body, budget.MaxResponseBytes, "事件响应超过传输预算。");
        using var decoded = OpenDecoded(responseBudget, contentEncoding);
        using var decodedBudget = new BudgetReadStream(decoded, budget.MaxDecodedBytes, "解压后的事件响应超过预算。");
        // strict UTF-8 prevents silently returning replacement characters in an
        // event line. Leave the response stream owned by the caller.
        using var reader = new StreamReader(
            decodedBudget,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 4096,
            leaveOpen: true);

        while (true)
        {
            var line = ReadLineBounded(reader, budget.MaxLineBytes, out var reachedEnd);
            if (line is null && reachedEnd) break;
            if (line is null) continue;

            if (string.IsNullOrWhiteSpace(line))
            {
                if (reachedEnd) break;
                continue;
            }

            var lineBytes = Encoding.UTF8.GetByteCount(line);
            if (lineBytes > budget.MaxLineBytes)
                throw new LimitException("事件行超过单行预算。");
            if (events.Count >= budget.MaxEvents)
                throw new LimitException("事件数量超过单页预算。");
            if (payloadBytes > budget.MaxPayloadBytes - lineBytes)
                throw new LimitException("事件正文超过单页预算。");

            ValidateJsonRecord(line);

            // Keep each NDJSON record verbatim. The caller can parse it later;
            // no client command constructor or local EventDb path is involved.
            events.Add(line);
            payloadBytes += lineBytes;

            if (reachedEnd) break;
        }

        return new Page(cursorIn, cursorOut, hasMore, events, payloadBytes);
    }

    private static void ValidateJsonRecord(string text)
    {
        var parser = new JsonRecordParser(text);
        parser.ParseDocument();
    }

    internal static void DisposeQuietly(IDisposable? resource)
    {
        try { resource?.Dispose(); }
        catch (Exception) { }
    }

    private static long ParseCursor(string? header)
    {
        var value = header?.Trim();
        if (string.IsNullOrWhiteSpace(value))
            throw new ProtocolException("响应缺少 x-latest-event 游标。");

        if (!long.TryParse(
                value, NumberStyles.None, CultureInfo.InvariantCulture, out var cursor)
            || cursor < 0)
            throw new ProtocolException("x-latest-event 不是非负整数。");

        return cursor;
    }

    private static Stream OpenDecoded(Stream body, string? contentEncoding)
    {
        var encoding = contentEncoding?.Trim();
        if (encoding is null || encoding.Length == 0
            || encoding.Equals("identity", StringComparison.OrdinalIgnoreCase))
            return new NonDisposingStream(body);

        if (encoding.Equals("gzip", StringComparison.OrdinalIgnoreCase))
            return new GZipStream(body, CompressionMode.Decompress, leaveOpen: true);

        throw new ProtocolException("不支持的 Content-Encoding。");
    }

    private static string? ReadLineBounded(StreamReader reader, int maxLineBytes, out bool reachedEnd)
    {
        var builder = new StringBuilder();
        while (true)
        {
            var value = reader.Read();
            if (value < 0)
            {
                reachedEnd = true;
                if (builder.Length == 0) return null;
                break;
            }

            if (value == '\n')
            {
                reachedEnd = false;
                break;
            }

            // CRLF is normalized to one logical line. A bare CR is retained;
            // it will be counted and returned as part of the raw record.
            builder.Append((char)value);
            if (builder.Length > maxLineBytes)
                throw new LimitException("事件行超过单行预算。");
        }

        if (builder.Length > 0 && builder[builder.Length - 1] == '\r')
            builder.Length--;
        return builder.ToString();
    }

    /// <summary>
    /// 只做 JSON 语法完整性检查，不构造对象，也不依赖客户端的 JSON 库。
    /// 事件协议的顶层记录必须是对象或数组；这样普通文本、两个相邻值和
    /// 未闭合的字符串/括号都会在跨域返回前失败。
    /// </summary>
    private sealed class JsonRecordParser
    {
        private readonly string _text;
        private int _index;

        public JsonRecordParser(string text) => _text = text;

        public void ParseDocument()
        {
            SkipWhitespace();
            if (_index >= _text.Length || (_text[_index] != '{' && _text[_index] != '['))
                throw new ProtocolException("事件行必须是 JSON 对象或数组。");

            ParseValue(0);
            SkipWhitespace();
            if (_index != _text.Length)
                throw new ProtocolException("事件行含有尾随 JSON 内容。");
        }

        private void ParseValue(int depth)
        {
            if (depth > MaxJsonDepth)
                throw new LimitException("事件 JSON 嵌套深度超过预算。");
            if (_index >= _text.Length)
                throw InvalidJson();

            switch (_text[_index])
            {
                case '{': ParseObject(depth); return;
                case '[': ParseArray(depth); return;
                case '"': ParseString(); return;
                case 't': ParseLiteral("true"); return;
                case 'f': ParseLiteral("false"); return;
                case 'n': ParseLiteral("null"); return;
                default:
                    if (_text[_index] == '-' || IsDigit(_text[_index]))
                    {
                        ParseNumber();
                        return;
                    }

                    throw InvalidJson();
            }
        }

        private void ParseObject(int depth)
        {
            _index++; // {
            SkipWhitespace();
            if (Take('}')) return;

            while (true)
            {
                if (_index >= _text.Length || _text[_index] != '"')
                    throw InvalidJson();
                ParseString();
                SkipWhitespace();
                Require(':');
                ParseValue(depth + 1);
                SkipWhitespace();
                if (Take('}')) return;
                Require(',');
                SkipWhitespace();
            }
        }

        private void ParseArray(int depth)
        {
            _index++; // [
            SkipWhitespace();
            if (Take(']')) return;

            while (true)
            {
                ParseValue(depth + 1);
                SkipWhitespace();
                if (Take(']')) return;
                Require(',');
                SkipWhitespace();
            }
        }

        private void ParseString()
        {
            Require('"');
            while (_index < _text.Length)
            {
                var value = _text[_index++];
                if (value == '"') return;
                if (value < 0x20) throw InvalidJson();
                if (value != '\\') continue;

                if (_index >= _text.Length) throw InvalidJson();
                var escape = _text[_index++];
                if (escape is '"' or '\\' or '/' or 'b' or 'f' or 'n' or 'r' or 't')
                    continue;
                if (escape != 'u' || _index + 4 > _text.Length)
                    throw InvalidJson();
                for (var i = 0; i < 4; i++)
                {
                    if (!IsHex(_text[_index++])) throw InvalidJson();
                }
            }

            throw InvalidJson();
        }

        private void ParseLiteral(string literal)
        {
            if (_index + literal.Length > _text.Length
                || !string.Equals(_text.Substring(_index, literal.Length), literal, StringComparison.Ordinal))
                throw InvalidJson();
            _index += literal.Length;
        }

        private void ParseNumber()
        {
            if (Take('-') && _index >= _text.Length) throw InvalidJson();

            if (Take('0'))
            {
                if (_index < _text.Length && IsDigit(_text[_index])) throw InvalidJson();
            }
            else
            {
                if (_index >= _text.Length || _text[_index] < '1' || _text[_index] > '9')
                    throw InvalidJson();
                while (_index < _text.Length && IsDigit(_text[_index])) _index++;
            }

            if (Take('.'))
            {
                if (_index >= _text.Length || !IsDigit(_text[_index])) throw InvalidJson();
                while (_index < _text.Length && IsDigit(_text[_index])) _index++;
            }

            if (_index < _text.Length && (_text[_index] == 'e' || _text[_index] == 'E'))
            {
                _index++;
                if (_index < _text.Length && (_text[_index] == '+' || _text[_index] == '-')) _index++;
                if (_index >= _text.Length || !IsDigit(_text[_index])) throw InvalidJson();
                while (_index < _text.Length && IsDigit(_text[_index])) _index++;
            }
        }

        private void SkipWhitespace()
        {
            while (_index < _text.Length && _text[_index] is ' ' or '\t' or '\r' or '\n') _index++;
        }

        private void Require(char expected)
        {
            if (!Take(expected)) throw InvalidJson();
        }

        private bool Take(char value)
        {
            if (_index >= _text.Length || _text[_index] != value) return false;
            _index++;
            return true;
        }

        private ProtocolException InvalidJson() => new("事件行不是完整 JSON。");

        private static bool IsDigit(char value) => value is >= '0' and <= '9';

        private static bool IsHex(char value) => IsDigit(value)
            || value is >= 'a' and <= 'f' or >= 'A' and <= 'F';
    }

    /// <summary>用于 identity 响应的非拥有流，避免 codec 关闭 HTTP 响应流。</summary>
    private sealed class NonDisposingStream : Stream
    {
        private readonly Stream _inner;

        public NonDisposingStream(Stream inner) => _inner = inner;

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => _inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
        protected override void Dispose(bool disposing) { }
    }

    /// <summary>限制单页读取字节数，同时不接管底层 HTTP 响应流的所有权。</summary>
    private sealed class BudgetReadStream : Stream
    {
        private readonly Stream _inner;
        private readonly string _limitMessage;
        private long _remaining;

        public BudgetReadStream(Stream inner, int maxBytes, string limitMessage)
        {
            _inner = inner;
            _remaining = maxBytes;
            _limitMessage = limitMessage;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (count == 0) return 0;
            if (_remaining == 0)
            {
                if (_inner.ReadByte() >= 0) throw new LimitException(_limitMessage);
                return 0;
            }

            var allowed = (int)Math.Min(count, _remaining);
            var read = _inner.Read(buffer, offset, allowed);
            _remaining -= read;
            return read;
        }

        public override int ReadByte()
        {
            if (_remaining == 0)
            {
                if (_inner.ReadByte() >= 0) throw new LimitException(_limitMessage);
                return -1;
            }

            var value = _inner.ReadByte();
            if (value >= 0) _remaining--;
            return value;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { }
    }
}
