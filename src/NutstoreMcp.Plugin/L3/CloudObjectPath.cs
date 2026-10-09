// 校验云端对象路径。
using System.Text;

namespace NutstoreMcp.Plugin.L3;

/// <summary>云端写入路径校验，拒绝 Windows 会折叠成同一路径的别名。</summary>
public static class CloudObjectPath
{
    public const int MaxUtf8Bytes = NutstoreMcp.ClientBindings.ClientSymbols.CloudPathMaxUtf8Bytes;

    private static readonly char[] Invalid = ['\\', ':', '*', '?', '"', '<', '>', '|'];
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static string? Validate(string? path, bool allowRoot = false)
    {
        if (string.IsNullOrWhiteSpace(path)) return "云端路径为空。";
        var actual = path!;
        if (!actual.StartsWith("/", StringComparison.Ordinal)) return "云端路径必须以 / 开头。";
        if (actual == "/") return allowRoot ? null : "云端路径不能是根目录。";
        if (actual.EndsWith("/", StringComparison.Ordinal)) return "云端路径末尾不能是 /。";
        if (actual.IndexOf("//", StringComparison.Ordinal) >= 0) return "云端路径不能含双斜线。";
        if (actual.IndexOfAny(Invalid) >= 0) return "云端路径含非法字符。";

        try
        {
            if (StrictUtf8.GetByteCount(actual) > MaxUtf8Bytes)
                return $"云端路径 UTF-8 长度超过 {MaxUtf8Bytes} 字节。";
        }
        catch (EncoderFallbackException)
        {
            return "云端路径含无效 Unicode 字符。";
        }

        foreach (var segment in actual.Substring(1).Split('/'))
        {
            if (segment.Length > 255) return "云端路径单个片段超过 255 字符。";
            if (segment == "." || segment == "..") return "云端路径不能含 . 或 .. 穿越片段。";
            if (!string.Equals(segment, segment.Trim(), StringComparison.Ordinal))
                return "云端路径片段首尾不能有空白。";
            if (segment.EndsWith(".", StringComparison.Ordinal)) return "云端路径片段末尾不能是点。";
            if (segment.Any(char.IsControl)) return "云端路径含控制字符。";

            // Windows 对带扩展名的设备名也保留，例如 CON.txt / COM1.log。
            var stem = segment.Split('.')[0].TrimEnd();
            if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase)
                || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
                || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase)
                || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
                || stem.Equals("CONIN$", StringComparison.OrdinalIgnoreCase)
                || stem.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase)
                || IsNumberedDevice(stem, "COM")
                || IsNumberedDevice(stem, "LPT"))
            {
                return "云端路径不能使用 Windows 设备名。";
            }
        }

        return null;
    }

    public static string Parent(string path)
    {
        var error = Validate(path);
        if (error is not null) throw new ArgumentException(error, nameof(path));
        var slash = path.LastIndexOf('/');
        return slash == 0 ? "/" : path.Substring(0, slash);
    }

    private static bool IsNumberedDevice(string stem, string prefix) =>
        stem.Length == 4 && stem.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
        && ((stem[3] >= '1' && stem[3] <= '9') || stem[3] is '¹' or '²' or '³');
}
