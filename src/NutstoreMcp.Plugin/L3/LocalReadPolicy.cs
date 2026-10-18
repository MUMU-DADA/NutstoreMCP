// 本地文件读取白名单 —— 决定「哪些本地文件可以被读出来传到云端」。
using System.Text;

namespace NutstoreMcp.Plugin.L3;

/// <summary>
/// 本地文件读取的**白名单**校验。
/// </summary>
/// <remarks>
/// 为什么需要白名单：让 MCP 调用方传一个本地路径去读，等于给了「读取本机任意文件」的
/// 能力 —— 一个被诱导的助手可以把 <c>.ssh</c> 或凭据文件读出来传到云端。写入还有令牌
/// 挡着，读取却只受调用方自觉约束，这不能接受。
///
/// 所以默认**关闭**：白名单文件不存在或没有有效条目时，按本地文件路径创建云端文件的
/// 功能直接不可用，只能走内联 base64。用户把自己信任的目录写进白名单才打开。
///
/// 只校验路径归属、大小与类型，**不读内容** —— 读取发生在真正执行的那一步。
/// </remarks>
internal static class LocalReadPolicy
{
    /// <summary>白名单文件名，放在 enabled.state / 写入令牌旁边。</summary>
    public const string FileName = "read-roots.txt";

    /// <summary>白名单每行一个绝对路径，<c>#</c> 开头为注释。</summary>
    public static IReadOnlyList<string> ParseRoots(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<string>();
        }

        var roots = new List<string>();

        foreach (var raw in (text ?? string.Empty).Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            // 白名单里的条目必须是绝对路径；写错的行直接忽略而不是当成相对路径
            // （相对路径会跟着当前目录漂，等于范围不可控）。
            if (!Path.IsPathRooted(line))
            {
                continue;
            }

            try
            {
                var full = Path.GetFullPath(line).TrimEnd(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                // "C:\" 去尾分隔符会变成 "C:"，补回来再比较
                if (full.Length == 0)
                {
                    continue;
                }

                if (full.Length == 2 && full[1] == ':')
                {
                    full += Path.DirectorySeparatorChar;
                }

                if (!roots.Contains(full, StringComparer.OrdinalIgnoreCase))
                {
                    roots.Add(full);
                }
            }
            catch (Exception)
            {
                // 非法路径（含非法字符）直接跳过
            }
        }

        return roots;
    }

    /// <summary>路径是否落在某个白名单根之下（含根本身）。</summary>
    public static bool IsUnderAnyRoot(string fullPath, IReadOnlyList<string> roots)
    {
        foreach (var root in roots)
        {
            if (fullPath.Length == root.Length)
            {
                if (string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                continue;
            }

            // 必须带上分隔符再比较，否则 "C:\data-evil" 会被当成在 "C:\data" 之下
            if (fullPath.Length > root.Length
                && fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                && (root[root.Length - 1] == Path.DirectorySeparatorChar || fullPath[root.Length] == Path.DirectorySeparatorChar))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 校验一个「要上传的本地文件」是否可用。返回 <c>null</c> 表示通过，
    /// 否则是要原样回给调用方的拒绝理由。
    /// </summary>
    /// <param name="sourcePath">调用方给的本地路径。</param>
    /// <param name="roots">白名单根，来自 <see cref="ParseRoots"/>。</param>
    /// <param name="maxBytes">允许的最大字节数。</param>
    /// <param name="canonicalPath">通过时返回规范化后的路径（真正读取时用这个）。</param>
    /// <param name="length">通过时返回文件长度。</param>
    public static string? Validate(
        string? sourcePath,
        IReadOnlyList<string> roots,
        long maxBytes,
        out string? canonicalPath,
        out long length)
    {
        canonicalPath = null;
        length = 0;

        if (roots.Count == 0)
        {
            return "本地文件模式未启用：需要先把可信目录写进白名单 " + FileName + "，"
                + "或用 contentBase64 直接给内容。";
        }

        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return "没有给本地文件路径。";
        }

        if (!Path.IsPathRooted(sourcePath))
        {
            return "本地文件路径必须是绝对路径。";
        }

        string full;
        try
        {
            full = Path.GetFullPath(sourcePath);
        }
        catch (Exception ex)
        {
            return "本地文件路径不合法：" + ex.GetType().Name + ": " + ex.Message;
        }

        if (!IsUnderAnyRoot(full, roots))
        {
            return "本地文件不在读取白名单内：" + full;
        }

        // 用 GetAttributes 而不是 FileInfo.Exists：FileInfo 对目录也返回 false，
        // 先判 Exists 会把「传了个目录」误报成「文件不存在」。
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(full);
        }
        catch (FileNotFoundException)
        {
            return "本地文件不存在：" + full;
        }
        catch (DirectoryNotFoundException)
        {
            return "本地文件不存在：" + full;
        }
        catch (Exception ex)
        {
            return "无法读取本地文件信息：" + ex.GetType().Name + ": " + ex.Message;
        }

        // 目录、设备、符号链接都要挡住：
        // 符号链接/目录联接指向白名单外的位置时，读出来的是白名单外的内容。
        if ((attributes & FileAttributes.Directory) != 0)
        {
            return "目标是目录，不是文件：" + full;
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            return "目标是符号链接或目录联接，可能指向白名单外，拒绝：" + full;
        }

        var info = new FileInfo(full);
        if (!info.Exists)
        {
            return "本地文件不存在：" + full;
        }

        if (info.Length <= 0)
        {
            return "本地文件是空的（0 字节）—— 空文件走另一条上传分支，当前不支持：" + full;
        }

        if (info.Length > maxBytes)
        {
            return "本地文件超过 " + maxBytes + " 字节上限（实际 " + info.Length + " 字节）：" + full;
        }

        canonicalPath = full;
        length = info.Length;
        return null;
    }

    /// <summary>
    /// 读取文件内容并核对长度。长度在「校验」与「读取」之间变化时直接失败，
    /// 绝不把未知大小的内容传上去。
    /// </summary>
    public static byte[] ReadExactly(string path, long expectedLength)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var buffer = new byte[expectedLength];

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

        if (read != expectedLength)
        {
            throw new InvalidOperationException(
                "本地文件在校验之后被改动过（期望 " + expectedLength + " 字节，读到 " + read + "），已中止。");
        }

        if (stream.ReadByte() >= 0)
        {
            throw new InvalidOperationException("本地文件在校验之后变长了，已中止。");
        }

        return buffer;
    }

    /// <summary>读出白名单文本；文件不存在时返回 <c>null</c>（等价于未启用）。</summary>
    public static string? TryReadRootsFile(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null;
        }
        catch (Exception)
        {
            // 读不到就当作未启用 —— 失败要朝「更严格」的方向倒
            return null;
        }
    }
}
