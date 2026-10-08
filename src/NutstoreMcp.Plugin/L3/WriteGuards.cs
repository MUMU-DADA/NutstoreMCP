namespace NutstoreMcp.Plugin.L3;

/// <summary>
/// L3 写入的放行判断。
/// </summary>
public static class WriteGuards
{
    /// <summary>放行所需的环境变量名。默认不存在，即默认完全不可写。</summary>
    public const string UnlockVariable = "NUTSTORE_L3_WRITE";

    /// <summary>判定结果。<see cref="WouldWrite"/> 为 false 表示这次只是预演。</summary>
    public sealed record Verdict(
        bool WouldWrite,
        IReadOnlyList<string> Refusals,
        IReadOnlyList<string> Notes)
    {
        public bool Allowed => Refusals.Count == 0;
    }

    /// <summary>判定输入。刻意全是值类型/字符串，方便构造边界用例。</summary>
    public sealed record Request(
        bool UnlockPresent,
        bool Confirmed,
        bool DryRun,
        string? LocalPath,
        bool LocalPathExists,
        bool LocalPathIsDirectory,
        bool LocalPathIsEmpty,
        bool PathAlreadyBound,
        IReadOnlyList<string> ExistingBindingPaths,
        long SandboxId = 0,
        string? SandboxSyncState = null);

    public static Verdict Evaluate(Request r)
    {
        var refusals = new List<string>();
        var notes = new List<string>();

        // ── 1. 三道闸，缺一不可 ──

        if (!r.UnlockPresent)
        {
            refusals.Add(
                $"未设置环境变量 {UnlockVariable}=1 —— L3 写入默认关闭。"
                + "这是刻意的：写入会改动同步配置，不该在没人盯着的时候可被触发。");
        }

        if (!r.Confirmed)
        {
            refusals.Add("调用方没有传 confirm=true。");
        }

        // ── 2. 本地路径本身 ──

        if (string.IsNullOrWhiteSpace(r.LocalPath))
        {
            refusals.Add("本地路径为空。");
        }
        else
        {
            if (!r.LocalPathExists)
            {
                refusals.Add($"本地路径不存在: {r.LocalPath}");
            }
            else if (!r.LocalPathIsDirectory)
            {
                refusals.Add($"本地路径不是目录: {r.LocalPath}");
            }
            else if (!r.LocalPathIsEmpty)
            {
                // 这是本类存在的主要理由
                refusals.Add(
                    $"本地目录非空: {r.LocalPath}。"
                    + "非空目录与云端沙箱绑定后会双向合并，冲突时可能覆盖文件 —— "
                    + "判断这些文件安不安全需要比对两边全量元数据，判断错了代价是数据。"
                    + "请先建一个空目录，或手动在客户端里操作。");
            }
        }

        // ── 3. 不能和现有绑定重叠 ──

        if (r.PathAlreadyBound)
        {
            refusals.Add("该路径已经被某个同步文件夹占用。");
        }

        if (!string.IsNullOrWhiteSpace(r.LocalPath))
        {
            foreach (var existing in r.ExistingBindingPaths)
            {
                if (string.IsNullOrWhiteSpace(existing))
                {
                    continue;
                }

                if (IsSameOrNested(r.LocalPath!, existing))
                {
                    refusals.Add(
                        $"本地路径与现有同步根重叠: {existing}。"
                        + "嵌套同步的行为未定义，客户端自己也不允许这么建。");
                    break;
                }
            }
        }

        // ── 4. 必须绑定一个**已存在于云端**的沙箱 ──
        //
        // 绑定已存在的沙箱是纯本地操作（云端内容会被下载下来）。
        // 「新建云端文件夹」是远程写操作，属于另一件事，不在本工具范围内。
        if (r.SandboxId <= 0)
        {
            refusals.Add(
                "没有指定要绑定的云端沙箱（sandboxId 必须为正数）。"
                + "本操作只把**已存在**的云端文件夹绑到本地，不在云端新建任何东西 —— "
                + "先用 nutstore_sync_folders 拿到未绑定的沙箱及其 ID。");
        }

        // fail-closed：状态必须被**明确证明**是 NotSynced。
        // 留空不等于「无所谓」—— 没提供就等于没证明，拒绝。
        if (string.IsNullOrWhiteSpace(r.SandboxSyncState))
        {
            refusals.Add(
                "没有提供沙箱的同步状态，无法确认它尚未绑定。"
                + "必须显式传 sandboxSyncState=NotSynced（值来自 nutstore_sync_folders）。");
        }
        else if (!string.Equals(r.SandboxSyncState, "NotSynced", StringComparison.OrdinalIgnoreCase))
        {
            refusals.Add(
                $"沙箱状态是 {r.SandboxSyncState}，不是 NotSynced。"
                + "只允许绑定尚未同步的沙箱 —— 已绑定的再绑一次没有意义，"
                + "而且可能触发意料之外的重定位。");
        }

        notes.Add("本操作只创建绑定，不删除、不移动任何文件或目录。");
        notes.Add("绑定后客户端会把云端内容下载到该目录 —— 所以它必须是空的。");

        // 只有全部放行、且不是预演时才真的写
        return new Verdict(
            WouldWrite: refusals.Count == 0 && !r.DryRun,
            Refusals: refusals,
            Notes: notes);
    }

    /// <summary>
    /// 两条路径是否相同或互为祖孙（大小写不敏感 —— Windows 文件系统如此）。
    /// <c>Ordinal</c> 而不是文化敏感比较：否则某些区域设置下会误判。
    /// </summary>
    public static bool IsSameOrNested(string a, string b)
    {
        var x = Normalize(a);
        var y = Normalize(b);

        if (x.Length == 0 || y.Length == 0)
        {
            return false;
        }

        if (string.Equals(x, y, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return x.StartsWith(y + "\\", StringComparison.OrdinalIgnoreCase)
            || y.StartsWith(x + "\\", StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string path)
    {
        try
        {
            var full = Path.GetFullPath(path.Trim());
            return full.TrimEnd('\\', '/');
        }
        catch (Exception)
        {
            // 路径非法时用原串，让比较尽量保守（宁可判重叠也不要漏判）
            return path.Trim().TrimEnd('\\', '/');
        }
    }
}
