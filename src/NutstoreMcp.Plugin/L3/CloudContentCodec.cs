// 云端内容读取与上传用的纯函数：UTF-8 判定、SHA-256、客户端云端 hash 与分块记录。
//
// 放在这里而不是 UiHook 里，是因为默认域程序集不方便做离线测试；
// 这个文件同时被 UiHook（net48）与测试套件（net10）链接编译，
// 由 dotnet test 直接覆盖 —— 与 RemoteEventCodec 的做法一致。
using System.Security.Cryptography;
using System.Text;

namespace NutstoreMcp.Plugin.L3;

/// <summary>读取云端内容时的编码与摘要工具。</summary>
internal static class CloudContentCodec
{
    /// <summary>客户端云端 hash 的分块大小：4 MiB。</summary>
    public const int CloudHashBlockBytes = 4 * 1024 * 1024;

    /// <summary>
    /// 客户端在内容不超过该值时走**全量上传**，超过后走**分块上传**。
    /// 两侧的云端 hash 公式相同，但上传协议不同；这个阈值决定走哪条路。
    /// </summary>
    public const int FullUploadThresholdBytes = 393216;

    /// <summary>
    /// 大文件创建的原始字节上限（16 MiB）。超过这个范围不做，避免长耗时和
    /// 一次调用消耗过多上传流量；此上限同时由插件与默认域钩子校验。
    /// </summary>
    public const int MaxFileUploadBytes = 16 * 1024 * 1024;

    /// <summary>
    /// 单次内联读取的原始字节上限（256 KiB）。
    /// </summary>
    /// <remarks>
    /// 与受限创建的内联内容上限一致：它低于客户端 4 MiB 分块阈值，所以整文件读取时
    /// 单块 cloud hash 公式直接成立。插件与默认域钩子共用这一个常量，避免两边漂移。
    /// </remarks>
    public const int MaxInlineReadBytes = 256 * 1024;

    /// <summary>
    /// 严格 UTF-8 解码。
    /// </summary>
    /// <remarks>
    /// 只有整段都是合法 UTF-8 才返回 true；任何非法序列（包括被区间切开的
    /// 多字节字符）都返回 false，绝不使用替换字符 —— 内容要么是原文，要么交给 base64。
    /// </remarks>
    public static bool TryDecodeUtf8(byte[] bytes, out string text)
    {
        try
        {
            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            text = string.Empty;
            return false;
        }
    }

    /// <summary>内容是否以 UTF-8 BOM 开头。</summary>
    public static bool HasUtf8Bom(byte[] bytes)
        => bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;

    /// <summary>小写十六进制 SHA-256。</summary>
    public static string Sha256Hex(byte[] bytes)
    {
        using var sha = SHA256.Create();
        var digest = sha.ComputeHash(bytes);
        var text = new StringBuilder(digest.Length * 2);
        foreach (var b in digest)
        {
            text.Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        }

        return text.ToString();
    }

    /// <summary>
    /// 客户端的分块云端 hash：按 4 MiB 分块，每块
    /// <c>SHA256(block) || BE64(block.Length)</c> 拼成 40 字节记录，
    /// 再对全部记录做一次 SHA-256，结果用普通 Base64。
    /// </summary>
    /// <remarks>
    /// 不支持空文件：客户端对空内容走另一条分支（32 个零字节），
    /// 本函数只用于已确认非空的内容。大文件上传时由 <see cref="ComputeFileRecord"/>
    /// 同时给出每一块的记录与文件级记录。
    /// </remarks>
    public static string CloudHash(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        return ComputeFileRecord(stream).HashBase64;
    }

    /// <summary>URL-safe Base64：把 <c>+</c>/<c>/</c> 换成 <c>-</c>/<c>_</c>，保留 padding。</summary>
    public static string ToUrlSafeBase64(byte[] bytes)
        => Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_');

    /// <summary>
    /// 按客户端算法生成一个文件的全部块记录与文件级记录。
    /// </summary>
    /// <remarks>
    /// 记录布局（来自客户端反编译并在真实上传上核对过）：
    /// <list type="bullet">
    /// <item>块记录 = SHA256(该块原始字节) || BE64(该块长度)，共 40 字节；</item>
    /// <item>文件摘要 = SHA256(全部块记录依次拼接)；</item>
    /// <item>文件记录 = 文件摘要 || BE64(文件总长度)。</item>
    /// </list>
    /// 上传时的块名用**URL-safe Base64(块记录)**；块列表体是各块
    /// URL-safe Base64(块记录) 以 <c>\r\n</c> 分隔（末尾也带一个分隔符）。
    /// 元数据里的 <c>hashBase64</c> 用普通 Base64(文件摘要)，
    /// <c>blockListNameBase64</c> 用普通 Base64(文件记录)。
    /// 空流按客户端另一条分支处理，这里直接拒绝。
    /// </remarks>
    public static CloudFileRecord ComputeFileRecord(Stream stream)
    {
        if (stream is null)
        {
            throw new ArgumentNullException(nameof(stream));
        }

        if (!stream.CanRead)
        {
            throw new ArgumentException("流不可读", nameof(stream));
        }

        var blocks = new List<CloudBlockRecord>();
        var allRecords = new MemoryStream();
        var buffer = new byte[CloudHashBlockBytes];
        long total = 0;

        using (var sha = SHA256.Create())
        {
            while (true)
            {
                var read = ReadBlock(stream, buffer);
                if (read == 0)
                {
                    break;
                }

                var digest = sha.ComputeHash(buffer, 0, read);
                var record = BuildRecord(digest, read);
                blocks.Add(new CloudBlockRecord(total, read, record));
                allRecords.Write(record, 0, record.Length);
                total += read;

                if (read < buffer.Length)
                {
                    break;
                }
            }

            if (total == 0)
            {
                throw new ArgumentException("空内容不走分块公式", nameof(stream));
            }

            var fileDigest = sha.ComputeHash(allRecords.ToArray());
            var fileRecord = BuildRecord(fileDigest, total);
            return new CloudFileRecord(total, fileDigest, fileRecord, blocks);
        }
    }

    /// <summary>把一条 40 字节记录拼成 <c>URL-safe Base64</c> 文本行。</summary>
    public static string ToBlockListLine(byte[] record) => ToUrlSafeBase64(record) + "\r\n";

    private static int ReadBlock(Stream stream, byte[] buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var got = stream.Read(buffer, read, buffer.Length - read);
            if (got <= 0)
            {
                break;
            }

            read += got;
        }

        return read;
    }

    private static byte[] BuildRecord(byte[] digest, long length)
    {
        var record = new byte[40];
        Buffer.BlockCopy(digest, 0, record, 0, digest.Length);
        var bigEndianLength = BitConverter.GetBytes(length);
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(bigEndianLength);
        }

        Buffer.BlockCopy(bigEndianLength, 0, record, digest.Length, bigEndianLength.Length);
        return record;
    }
}

/// <summary>一个 4 MiB 块的记录与原始位置，供默认域逐块上传。</summary>
internal sealed class CloudBlockRecord
{
    public CloudBlockRecord(long offset, int length, byte[] record)
    {
        Offset = offset;
        Length = length;
        Record = record;
    }

    /// <summary>该块在文件中的起始偏移。</summary>
    public long Offset { get; }

    /// <summary>该块原始字节数。</summary>
    public int Length { get; }

    /// <summary>40 字节块记录。</summary>
    public byte[] Record { get; }
}

/// <summary>整个文件的云端记录与块清单。</summary>
internal sealed class CloudFileRecord
{
    public CloudFileRecord(long size, byte[] digest, byte[] record, IReadOnlyList<CloudBlockRecord> blocks)
    {
        Size = size;
        Digest = digest;
        Record = record;
        Blocks = blocks;
    }

    /// <summary>文件总字节数。</summary>
    public long Size { get; }

    /// <summary>文件摘要（32 字节），对全部块记录做 SHA-256。</summary>
    public byte[] Digest { get; }

    /// <summary>文件记录（40 字节）：摘要 || BE64(总长度)。</summary>
    public byte[] Record { get; }

    /// <summary>各块记录，按文件顺序。</summary>
    public IReadOnlyList<CloudBlockRecord> Blocks { get; }

    /// <summary>元数据里的 hashBase64：普通 Base64(文件摘要)。</summary>
    public string HashBase64 => Convert.ToBase64String(Digest);

    /// <summary>元数据里的 blockListNameBase64：普通 Base64(文件记录)。</summary>
    public string BlockListNameBase64 => Convert.ToBase64String(Record);

    /// <summary>块列表请求的 name 参数：URL-safe Base64(文件记录)。</summary>
    public string UrlSafeBlockListName => CloudContentCodec.ToUrlSafeBase64(Record);

    /// <summary>块列表请求体：各块 URL-safe Base64(块记录) + CRLF，末尾也带一个 CRLF。</summary>
    public string BlockListBody
    {
        get
        {
            var text = new StringBuilder(Blocks.Count * 56);
            foreach (var block in Blocks)
            {
                text.Append(CloudContentCodec.ToBlockListLine(block.Record));
            }

            return text.ToString();
        }
    }
}
