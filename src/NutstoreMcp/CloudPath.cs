using System.Globalization;

namespace NutstoreMcp;

/// <summary>
/// 云端路径的规范化、编码与白名单校验。
///
/// ## 语义约定
///
/// 调用方传入的路径一律按**字面量**解释，不做百分号解码。
/// 这一点是安全性的关键：如果输入 <c>/a/%2e%2e/b</c>，<c>%2e%2e</c> 被当作一个普通
/// 目录名，随后被逐段 URL 编码成 <c>%252e%252e</c> —— 服务端看到的是字面字符串，
/// 而不是 <c>..</c>。所以「先查后解码」的经典绕过在这里不成立。
///
/// 反过来，服务端返回的 href 是 URL 编码的，必须解码后才能当路径用 ——
/// 见 <see cref="FromHref"/>。
///
/// 所有比较一律 <see cref="StringComparison.Ordinal"/>：绝不能用文化敏感比较
/// 做路径或白名单判断。
/// </summary>
public static class CloudPath
{
    public const string Root = "/";

    public static bool IsRoot(string cloudPath) => cloudPath == Root;

    /// <summary>
    /// 把任意输入规范成 <c>/a/b</c> 形式。
    /// 拒绝：控制字符、反斜杠、<c>.</c> / <c>..</c> 片段。
    /// 折叠：重复斜杠、去掉尾部斜杠（根除外）。
    /// </summary>
    public static string Normalize(string? input)
    {
        if (input is null)
        {
            throw new PathNotAllowedException("路径不能为空");
        }

        foreach (var ch in input)
        {
            if (char.IsControl(ch))
            {
                throw new PathNotAllowedException("路径包含控制字符");
            }
        }

        if (input.Contains('\\', StringComparison.Ordinal))
        {
            throw new PathNotAllowedException(
                "路径不能包含反斜杠 —— 云端路径一律使用正斜杠",
                "把 C:\\foo 改写成 /foo");
        }

        var p = CollapseSlashes(input);
        if (!p.StartsWith('/'))
        {
            p = "/" + p;
        }

        if (p.Length > 1)
        {
            p = p.TrimEnd('/');
        }

        if (p.Length == 0)
        {
            p = Root;
        }

        var segments = p.Split('/');
        for (var i = 1; i < segments.Length; i++)
        {
            var seg = segments[i];
            if (seg is "." or "..")
            {
                throw new PathNotAllowedException(
                    $"路径不能包含 \"{seg}\" 片段",
                    "相对路径不受支持，请给出从云端根开始的完整路径");
            }
        }

        return p;
    }

    private static string CollapseSlashes(string s)
    {
        if (!s.Contains("//", StringComparison.Ordinal))
        {
            return s;
        }

        var sb = new System.Text.StringBuilder(s.Length);
        var prevSlash = false;
        foreach (var ch in s)
        {
            if (ch == '/')
            {
                if (prevSlash)
                {
                    continue;
                }

                prevSlash = true;
            }
            else
            {
                prevSlash = false;
            }

            sb.Append(ch);
        }

        return sb.ToString();
    }

    /// <summary>按段做百分号编码，保留 <c>/</c> 分隔符。</summary>
    public static string Encode(string cloudPath)
    {
        var segments = cloudPath.Split('/');
        for (var i = 0; i < segments.Length; i++)
        {
            segments[i] = Uri.EscapeDataString(segments[i]);
        }

        return string.Join('/', segments);
    }

    /// <summary>拼接完整 WebDAV URL。<paramref name="baseUrl"/> 形如 <c>https://dav.jianguoyun.com/dav</c>。</summary>
    public static string ToUrl(string baseUrl, string cloudPath)
    {
        var b = baseUrl.TrimEnd('/');
        var encoded = Encode(cloudPath);
        return encoded == Root ? b + "/" : b + encoded;
    }

    /// <summary>
    /// 把响应里的 href 还原成云端路径。
    /// href 可能是绝对 URL、绝对路径或相对路径；一律：
    /// 去 query/fragment → 百分号解码 → 去掉 baseUrl 的路径前缀 → 规范化。
    /// </summary>
    public static string FromHref(string? href, string baseUrl)
    {
        if (string.IsNullOrEmpty(href))
        {
            throw new PathNotAllowedException("响应中的 href 为空");
        }

        var raw = href;
        var hash = raw.IndexOf('#', StringComparison.Ordinal);
        if (hash >= 0)
        {
            raw = raw[..hash];
        }

        var q = raw.IndexOf('?', StringComparison.Ordinal);
        if (q >= 0)
        {
            raw = raw[..q];
        }

        if (Uri.TryCreate(raw, UriKind.Absolute, out var abs))
        {
            raw = abs.AbsolutePath;
        }

        string decoded;
        try
        {
            decoded = Uri.UnescapeDataString(raw);
        }
        catch (UriFormatException)
        {
            decoded = raw;
        }

        var basePath = new Uri(baseUrl).AbsolutePath.TrimEnd('/');
        if (basePath.Length > 0 && decoded.StartsWith(basePath, StringComparison.Ordinal))
        {
            decoded = decoded[basePath.Length..];
        }

        return Normalize(string.IsNullOrEmpty(decoded) ? Root : decoded);
    }

    /// <summary>
    /// 白名单校验。
    ///
    /// **空白名单 = 拒绝一切**（fail-closed）。这是刻意的：宁可让用户配一次，
    /// 也不能因为忘配而把整个云盘连同里面的凭据文件暴露给模型。
    /// </summary>
    public static bool IsAllowed(string cloudPath, IReadOnlyList<string>? roots)
    {
        if (roots is null || roots.Count == 0)
        {
            return false;
        }

        foreach (var r in roots)
        {
            string root;
            try
            {
                root = Normalize(r);
            }
            catch (PathNotAllowedException)
            {
                continue;
            }

            if (root == Root)
            {
                return true;
            }

            if (cloudPath.Equals(root, StringComparison.Ordinal))
            {
                return true;
            }

            if (cloudPath.StartsWith(root + "/", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>校验失败时抛出，消息里带上白名单，方便调用方自行修正。</summary>
    public static string AssertAllowed(string cloudPath, IReadOnlyList<string>? roots)
    {
        if (IsAllowed(cloudPath, roots))
        {
            return cloudPath;
        }

        var shown = roots is { Count: > 0 }
            ? string.Join(", ", roots)
            : "(空 —— 当前拒绝一切访问)";

        throw new PathNotAllowedException(
            $"路径 \"{cloudPath}\" 不在允许的白名单内",
            $"当前白名单：{shown}。如需访问请调整 NUTSTORE_ROOTS。");
    }

    /// <summary>规范化白名单条目，丢弃非法项（不阻断启动）。</summary>
    public static List<string> NormalizeRoots(IEnumerable<string?>? rawRoots)
    {
        var result = new List<string>();
        if (rawRoots is null)
        {
            return result;
        }

        foreach (var r in rawRoots)
        {
            if (string.IsNullOrWhiteSpace(r))
            {
                continue;
            }

            try
            {
                var norm = Normalize(r.Trim());
                if (!result.Contains(norm, StringComparer.Ordinal))
                {
                    result.Add(norm);
                }
            }
            catch (PathNotAllowedException)
            {
                // 非法白名单条目直接忽略
            }
        }

        return result;
    }

    /// <summary>
    /// 本机路径白名单校验 —— 用于上传来源与下载目标。
    ///
    /// 存在的理由：如果没有这层，「上传本地文件到云端」就变成了一条
    /// 任意文件读取 + 外传通道。模型只要被注入一句指令，就能把
    /// <c>C:\Users\...\邮箱密码.txt</c> 传到云端。
    ///
    /// 用 <see cref="Path.GetRelativePath"/> 判断包含关系，
    /// 避免 <c>C:\a-evil</c> 被 <c>C:\a</c> 前缀匹配。
    /// </summary>
    public static string AssertLocalPathAllowed(string localPath, IReadOnlyList<string> localRoots)
    {
        if (localRoots.Count == 0)
        {
            throw new PathNotAllowedException(
                "未配置本机可访问目录，拒绝一切本地文件访问",
                "请设置 NUTSTORE_LOCAL_ROOTS。");
        }

        var resolved = Path.GetFullPath(localPath);
        foreach (var root in localRoots)
        {
            var fullRoot = Path.GetFullPath(root);
            var rel = Path.GetRelativePath(fullRoot, resolved);
            if (rel == ".")
            {
                return resolved;
            }

            if (!rel.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(rel))
            {
                return resolved;
            }
        }

        throw new PathNotAllowedException(
            $"本机路径 \"{resolved}\" 不在允许的目录内",
            $"当前允许：{string.Join(", ", localRoots)}。如需其他位置请调整 NUTSTORE_LOCAL_ROOTS。");
    }
}
