using System.Collections.Concurrent;

namespace NutstoreMcp.Plugin.Transport;

/// <summary>
/// 一个可阻塞读取的流：写入端 <see cref="Push"/> 投递字节，读取端 <see cref="Read"/>
/// 阻塞取字节。
///
/// 用途：官方 MCP SDK 的 <c>WithStreamServerTransport(input, output)</c> 要求一对
/// 「长连接」流，而 HTTP 是每请求独立的。中间用两条这种流做解耦 ——
/// 把 HTTP 请求体推进 input，SDK 的响应从 output 读出来。
/// </summary>
internal sealed class QueueStream : Stream
{
    private readonly BlockingCollection<byte[]> _chunks = new();
    private byte[]? _current;
    private int _offset;

    /// <summary>投递一段字节。流已关闭时静默丢弃。</summary>
    public void Push(byte[] data)
    {
        if (data.Length == 0 || _chunks.IsAddingCompleted)
        {
            return;
        }

        try
        {
            _chunks.Add(data);
        }
        catch (InvalidOperationException)
        {
            // 已完成添加 —— 忽略
        }
    }

    /// <summary>投递一行（自动补 \n）。SDK 的流传输按行分隔 JSON-RPC 消息。</summary>
    public void PushLine(string line) => Push(System.Text.Encoding.UTF8.GetBytes(line + "\n"));

    /// <summary>标记不会再写入；读取端在耗尽后返回 0（EOF）。</summary>
    public void CompleteAdding() => _chunks.CompleteAdding();

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (count == 0)
        {
            return 0;
        }

        while (_current is null || _offset >= _current.Length)
        {
            if (!_chunks.TryTake(out _current, Timeout.Infinite))
            {
                return 0; // 已完成添加且已耗尽
            }

            _offset = 0;
        }

        var n = Math.Min(count, _current.Length - _offset);
        Buffer.BlockCopy(_current, _offset, buffer, offset, n);
        _offset += n;
        return n;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count)
        => Push(buffer.AsSpan(offset, count).ToArray());

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_chunks.IsAddingCompleted)
        {
            _chunks.CompleteAdding();
        }

        base.Dispose(disposing);
    }
}
