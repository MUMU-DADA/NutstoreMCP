// 云端内容读取用的纯函数：UTF-8 判定、SHA-256 与客户端云端 hash。
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
    /// 单次内联读取的原始字节上限（256 KiB）。
    /// </summary>
    /// <remarks>
    /// 与受限创建的上限一致：它低于客户端 4 MiB 分块阈值，所以整文件读取时
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
    /// 本函数只用于已确认非空、且不超过单次读取上限的内容。
    /// </remarks>
    public static string CloudHash(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            throw new ArgumentException("空内容不走分块公式", nameof(bytes));
        }

        using var sha = SHA256.Create();
        using var records = new MemoryStream();
        for (var offset = 0; offset < bytes.Length; offset += CloudHashBlockBytes)
        {
            var length = Math.Min(CloudHashBlockBytes, bytes.Length - offset);
            var digest = sha.ComputeHash(bytes, offset, length);
            records.Write(digest, 0, digest.Length);

            var bigEndianLength = BitConverter.GetBytes((long)length);
            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(bigEndianLength);
            }

            records.Write(bigEndianLength, 0, bigEndianLength.Length);
        }

        return Convert.ToBase64String(sha.ComputeHash(records.ToArray()));
    }
}
