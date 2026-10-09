// 拦截禁止经 MCP 调用的客户端方法。
using NutstoreMcp.ClientBindings;
using System.Text.Json;

namespace NutstoreMcp.Plugin.L3;

/// <summary>
/// 永远不允许经 MCP 调用的客户端方法。
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

        // 不能只拦语义名字：客户端云端操作入口使用混淆哈希。
        [ClientSymbols.DeleteSandbox] =
            "删除云端沙箱。已反编译确认调用 MakeDeleteSandboxRequest 并发出请求，"
            + "不是沙箱状态查询；返回 bool 不代表只读。",

        ["MakeDeleteSandboxRequest"] =
            "构造删除云端沙箱的请求。禁止经通用桥拼装再提交删除请求。",

        [ClientSymbols.DeleteObject] =
            "删除云端文件或目录。两个重载最终都提交删除元数据，可能传播到本地同步文件。",

        [ClientSymbols.ReadEventsWithLocalWrites] =
            "事件入口虽只读取云端，但随后写 EventDb、改变未读数并发布最近页更新；"
            + "不能经通用桥调用，避免把远端读取变成本地客户端状态变更。",

        [ClientSymbols.SessionHeaderHelper] =
            "客户端私有会话头生成器，结果包含会话凭据；禁止经桥读取或导出。",

        ["DeleteObject"] = "删除对象；禁止通过语义名称绕过混淆入口禁令。",
        ["RemoveObject"] = "移除对象；禁止通过语义名称绕过混淆入口禁令。",

        ["GetFieldValue"] = "反射读取任意对象的私有字段，绕过桥的接收者和声明程序集限制。",
        ["SetFieldValue"] = "反射修改任意对象的私有字段，绕过桥的对象与成员写入边界。",
        ["GetPropertyValue"] = "反射读取任意对象的私有属性，绕过桥的公开成员限制。",
        ["SetPropertyValue"] = "反射修改任意对象的私有属性，绕过桥的对象与成员写入边界。",

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

    static ForbiddenClientMethods()
    {
        foreach (var name in ClientSymbols.ForbiddenMethodNames)
            if (!Denylist.ContainsKey(name))
                throw new InvalidOperationException("共享禁止清单缺少插件拒绝说明：" + name);
        if (Denylist.Count != ClientSymbols.ForbiddenMethodNames.Length)
            throw new InvalidOperationException("插件与共享禁止清单不一致。");
    }

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
        note = "这些方法永不暴露给 MCP：涉及删除、解绑、迁移同步根、会写客户端状态的事件入口，或绕过桥的对象与成员边界。",
        entries = Denylist
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => new { method = kv.Key, reason = kv.Value }),
    }, new JsonSerializerOptions
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });
}
