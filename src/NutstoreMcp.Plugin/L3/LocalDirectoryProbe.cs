// 已有目录里创建文件前的**本地侧**预检：只看绑定文件夹，不读内容、不写任何东西。
//
// 为什么云端列举不够：客户端的冲突改名/删除分支不只由云端状态触发。
// 本机绑定文件夹里若有与目标仅大小写不同的文件，我们往云端写同名文件后，
// 客户端的冲突处理同样会作用到本地真实文件。所以两侧都要查。

namespace NutstoreMcp.Plugin.L3;

/// <summary>
/// 本地绑定文件夹的只读枚举，用来确认「目标文件名在本地没有大小写别名」，
/// 并核对父路径的每一段在磁盘上的大小写与云端一致。
/// </summary>
/// <remarks>
/// 只做结构性检查：不读文件内容、不跟随符号链接、不改动任何东西。
/// 目标目录尚未同步到本地时返回 <see cref="Result.PathExists"/> = false —— 本地没有对象，
/// 也就不存在本地别名；这不是错误。
/// </remarks>
public static class LocalDirectoryProbe
{
    /// <summary>一次预检的结果。</summary>
    /// <param name="PathExists">目标父目录在本地是否存在（false 表示尚未同步到本地）。</param>
    /// <param name="Entries">父目录里的条目名（不含路径）；父目录不存在时为空。</param>
    /// <param name="Error">本地读取失败或大小写不一致时的拒绝理由；<c>null</c> 表示没有发现问题。</param>
    public sealed record Result(bool PathExists, IReadOnlyList<string> Entries, string? Error)
    {
        public static Result Absent() => new(false, Array.Empty<string>(), null);
        public static Result Fail(string error) => new(false, Array.Empty<string>(), error);
    }

    /// <summary>
    /// 从绑定根开始逐段核对大小写，再枚举目标父目录。
    /// </summary>
    /// <param name="localRoot">客户端登记的真实本地绑定根（绝对路径）。</param>
    /// <param name="cloudDirectory">云端目录路径：<c>/</c> 表示沙箱根。</param>
    public static Result Inspect(string localRoot, string cloudDirectory)
    {
        if (string.IsNullOrWhiteSpace(localRoot) || !Path.IsPathRooted(localRoot))
        {
            return Result.Fail("本地绑定根不是绝对路径，拒绝执行。");
        }

        var segments = string.Equals(cloudDirectory, "/", StringComparison.Ordinal)
            ? Array.Empty<string>()
            : (cloudDirectory ?? string.Empty).TrimStart('/').Split('/');

        var current = localRoot;
        foreach (var segment in segments)
        {
            if (segment.Length == 0)
            {
                return Result.Fail("云端目录路径含空段，拒绝执行。");
            }

            if (File.Exists(current))
            {
                return Result.Fail("本地同路径是文件而不是目录：" + current + "，拒绝执行。");
            }

            if (!Directory.Exists(current))
            {
                // 父目录在本地还没有出现：往下也不可能有本地对象。
                return Result.Absent();
            }

            string? exact = null;
            string? caseOnly = null;
            try
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(current))
                {
                    var name = Path.GetFileName(entry);
                    if (string.Equals(name, segment, StringComparison.Ordinal))
                    {
                        exact = name;
                        break;
                    }

                    if (caseOnly is null && string.Equals(name, segment, StringComparison.OrdinalIgnoreCase))
                    {
                        caseOnly = name;
                    }
                }
            }
            catch (Exception ex)
            {
                return Result.Fail("本地目录枚举失败（" + current + "）：" + ex.GetType().Name + "。");
            }

            if (exact is null)
            {
                // 只有大小写不同的同名条目：客户端的冲突分支会作用到它们，必须拒绝。
                return caseOnly is not null
                    ? Result.Fail("本地绑定目录里的路径段大小写与云端不一致：" + Path.Combine(current, caseOnly)
                        + "（云端要求 " + segment + "），拒绝执行。")
                    : Result.Absent();
            }

            current = Path.Combine(current, exact);
        }

        if (!Directory.Exists(current))
        {
            return Result.Absent();
        }

        var names = new List<string>();
        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(current))
            {
                names.Add(Path.GetFileName(entry));
            }
        }
        catch (Exception ex)
        {
            return Result.Fail("本地目录枚举失败（" + current + "）：" + ex.GetType().Name + "。");
        }

        return new Result(true, names, null);
    }

    /// <summary>目录里是否已有与 <paramref name="fileName"/> 仅大小写不同的条目。</summary>
    public static string? FindCaseInsensitiveCollision(IReadOnlyList<string> entries, string fileName)
    {
        foreach (var entry in entries)
        {
            if (string.Equals(entry, fileName, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }

        return null;
    }
}
