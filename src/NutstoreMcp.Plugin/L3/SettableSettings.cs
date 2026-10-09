namespace NutstoreMcp.Plugin.L3;

/// <summary>
/// 哪些客户端设置允许改，以及各自的危险层级。
/// </summary>
/// <remarks>
/// 分层的理由：一把锁不够。`EnableFileProtectionLock` 会**安装文件系统驱动**，
/// `EnableSafeGuard` 影响文件夹同步与权限操作前的验证 —— 这两个做完不好撤。
/// 而 `AutoStart` 改错了再改回来就行。
///
/// 如果共用一把锁，那么「为了改开机自启而解锁」就等于同时允许装驱动。
/// 所以系统级需要**第二把锁**，它默认不存在，且只在你明确要动这类设置时才建。
/// </remarks>
internal static class SettableSettings
{
    /// <summary>
    /// 普通开关：改错了顶多是行为不符预期，改回来即可。
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Normal =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["AutoStart"] = "开机自动启动",
            ["IsShutdownOnIdle"] = "同步完成后关机",
            ["IsNotifyOnFilesUpdated"] = "文件更新后启动气泡提醒",
            ["IsDirectOpen"] = "直接打开文件夹",
            ["IsExperimental"] = "体验最新功能",
        };

    /// <summary>
    /// 系统级：涉及驱动安装、权限校验、同步暂停 —— 做完不好撤，需要第二把锁。
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> SystemLevel =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["EnableFileProtectionLock"] =
                "启用文件保护锁：会安装专用文件系统驱动来保护文件；关闭后本地同步文件夹在客户端未运行时将无法访问。",
            ["EnableSafeGuard"] =
                "高级安全验证：影响文件夹同步设置与权限设置前的验证行为。",
            ["IsSyncPaused"] =
                "自动暂停同步：暂停会让云端与本地脱节，期间任何一端的改动都不会同步过去。",
        };

    /// <summary>取设置项的层级：null 表示不允许改。</summary>
    public static string? TierOf(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        // IsNullOrWhiteSpace 没有 NotNullWhen 标注，显式收窄
        var key = name!;

        if (Normal.ContainsKey(key))
        {
            return "normal";
        }

        if (SystemLevel.ContainsKey(key))
        {
            return "system";
        }

        return null;
    }

    /// <summary>允许改的全部设置项名（用于拒绝时给出可选项）。</summary>
    public static IEnumerable<string> AllNames =>
        Normal.Keys.Concat(SystemLevel.Keys).OrderBy(n => n, StringComparer.Ordinal);
}
