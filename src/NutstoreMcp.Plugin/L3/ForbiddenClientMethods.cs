using System.Text.Json;

namespace NutstoreMcp.Plugin.L3;

/// <summary>
/// 永远不允许经 MCP 调用的客户端方法。
///
/// <para>
/// <b>为什么要在代码里拦，而不只是写在文档里</b>：文档拦不住一个热情过头的实现 ——
/// 只要有人加一个「通用调用客户端 API」的工具（这几乎是必然会发生的需求），
/// 这些方法就会自动变成可调用的。而它们的共同点是**后果不可逆**：
/// 注销同步根、解绑沙箱、清空本地数据。
/// </para>
///
/// <para>
/// 所以这里是一道**代码级闸门**：任何面向客户端的调用通道都应该先过
/// <see cref="Check"/>。denylist 比 allowlist 弱，但面对一个庞大的、
/// 还在持续测绘中的 API 面，allowlist 不现实 —— 至少把已知的悬崖围起来。
/// </para>
/// </summary>
public static class ForbiddenClientMethods
{
    /// <summary>方法名（不区分大小写）→ 禁止的理由。</summary>
    private static readonly Dictionary<string, string> Denylist = new(StringComparer.OrdinalIgnoreCase)
    {
        // ── 同步根 / 沙箱绑定 ──

        ["RemoveSandbox"] =
            "解绑沙箱。本地目录会怎样取决于客户端实现细节，而那是用户的文件。"
            + "客户端 GUI 里这个操作有二次确认，说明厂商自己认为它危险。",

        ["MigrateSandboxPath"] =
            "移动同步根。中途失败可能留下两处半成品状态。",

        ["MigrateSandboxObjectTableToUnicodeNoCaseCollation"] =
            "重建对象表。批量重写同步元数据，失败可能让本地与云端失去对应关系。",

        // ── OS 层（Windows 云文件 API）──

        ["UnregisterAllNutstoreSyncRoots"] =
            "把**所有**同步根从系统注销 —— Explorer 云图标、按需下载全部失效，"
            + "而且影响账号下的每一个文件夹，不只是操作者想动的那个。",

        ["UnregisterSyncRootsOfAccount"] =
            "注销整个账号的同步根。同上级别危险。",

        ["UnregisterSyncRoot"] =
            "注销单个同步根。会让该目录从系统的云文件体系里消失。",

        ["DisconnectSyncRoot"] =
            "断开同步根回调连接。断掉后该目录不再响应云文件操作。",

        ["DisableSyncRootOnDemandPopulation"] =
            "关闭按需填充 —— 占位符不再自动下载内容，表现为文件「打不开」。",

        // ── 数据清除 ──

        ["RemoveAUMIDToSyncRoots"] =
            "改动同步根与应用的关联注册，可能让同步根失去归属。",
    };

    /// <summary>
    /// 检查一个客户端方法是否被禁止调用。
    /// </summary>
    /// <param name="typeName">声明该方法的类型全名（用于报错信息，当前不参与判定）。</param>
    /// <param name="methodName">方法名。</param>
    /// <param name="reason">被禁止时给出理由；否则为空。</param>
    public static bool IsForbidden(string? typeName, string? methodName, out string reason)
    {
        reason = string.Empty;

        if (string.IsNullOrWhiteSpace(methodName))
        {
            return false;
        }

        if (Denylist.TryGetValue(methodName!.Trim(), out var why))
        {
            reason = $"{typeName ?? "(未知类型)"}.{methodName} 在禁止清单里：{why}";
            return true;
        }

        return false;
    }

    /// <summary>
    /// 面向「通用调用」通道的闸门。放行返回 null，禁止则返回可读的拒绝说明。
    /// </summary>
    public static string? Check(string? typeName, string? methodName)
        => IsForbidden(typeName, methodName, out var reason) ? reason : null;

    /// <summary>导出清单，供工具与文档共用（避免两处各写一份而悄悄漂移）。</summary>
    public static string Describe() => JsonSerializer.Serialize(new
    {
        count = Denylist.Count,
        note = "这些方法永不暴露给 MCP。它们后果不可逆：注销同步根、解绑沙箱、清空本地数据。",
        entries = Denylist
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => new { method = kv.Key, reason = kv.Value }),
    }, new JsonSerializerOptions
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });
}
