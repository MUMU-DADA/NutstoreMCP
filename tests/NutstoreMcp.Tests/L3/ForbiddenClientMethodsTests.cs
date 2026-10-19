// 验证危险客户端方法拦截。
using NutstoreMcp.ClientBindings;
using NutstoreMcp.Plugin.L3;

namespace NutstoreMcp.Tests;

/// <summary>
/// L3 禁止方法的测试。
///
/// <para>
/// 这道闸门的意义在于：只要有人加一个「通用调用客户端 API」的工具
/// （面对这么大的 API 面，这几乎是必然会发生的需求），denylist 就是唯一的拦截点。
/// 所以它必须被测 —— 既不能漏掉已知的悬崖，也不能误伤正常方法。
/// </para>
/// </summary>
public class ForbiddenClientMethodsTests
{
    /// <summary>
    /// 这些方法一旦可调用，后果不可逆：注销同步根、解绑沙箱、重建同步元数据。
    /// </summary>
    public static TheoryData<string> DangerousMethods() =>
    [
        "RemoveSandbox",
        "MigrateSandboxPath",
        "MigrateSandboxObjectTableToUnicodeNoCaseCollation",
        "UnregisterAllNutstoreSyncRoots",
        "UnregisterSyncRootsOfAccount",
        "UnregisterSyncRoot",
        "DisconnectSyncRoot",
        "DisableSyncRootOnDemandPopulation",
        "RemoveAUMIDToSyncRoots",
        "DeleteObject",
        "RemoveObject",
        ClientSymbols.DeleteSandbox,
        "MakeDeleteSandboxRequest",
        ClientSymbols.DeleteObject,
        ClientSymbols.ReadEventsWithLocalWrites,
        ClientSymbols.SessionHeaderHelper,
        "GetFieldValue",
        "SetFieldValue",
        "GetPropertyValue",
        "SetPropertyValue",
    ];

    [Theory]
    [MemberData(nameof(DangerousMethods))]
    public void 危险方法全部被拦(string method)
    {
        var reason = ForbiddenClientMethods.Check("NutstoreClient.DAO.NSSandboxDAO", method);

        Assert.NotNull(reason);
        Assert.Contains(method, reason, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(DangerousMethods))]
    public void 大小写不能成为绕过手段(string method)
    {
        Assert.NotNull(ForbiddenClientMethods.Check(null, method.ToLowerInvariant()));
        Assert.NotNull(ForbiddenClientMethods.Check(null, method.ToUpperInvariant()));
    }

    [Fact]
    public void 两侧空白不能成为绕过手段()
    {
        Assert.NotNull(ForbiddenClientMethods.Check(null, "  RemoveSandbox  "));
    }

    /// <summary>
    /// 误伤会让闸门形同虚设 —— 实现者会干脆绕过它。
    /// 所以这些必须放行。
    /// </summary>
    public static TheoryData<string> SafeMethods() =>
    [
        "ReadAllSandboxesFromDb",
        "ReadAllSandboxes",
        "GetSandboxObjectsCount",
        "RegisterAsync",
        "ConnectSyncRootCallbacks",
        "GetSyncRootStandardInfo",
        "SetSyncLocation",
        "DiscardDuplicateAndEnqueue",
        "HasPendingBindCommand",
        ClientSymbols.ReadPathAcl, // 读子路径权限
        "MakeGetSyncFolderDetailRequest",
    ];

    [Theory]
    [MemberData(nameof(SafeMethods))]
    public void 正常方法不被误伤(string method)
    {
        Assert.Null(ForbiddenClientMethods.Check("Some.Type", method));
    }

    [Fact]
    public void 空方法名不报错也不拦()
    {
        Assert.Null(ForbiddenClientMethods.Check(null, null));
        Assert.Null(ForbiddenClientMethods.Check(null, ""));
        Assert.Null(ForbiddenClientMethods.Check(null, "   "));
    }

    [Fact]
    public void 拒绝理由要说清后果()
    {
        var reason = ForbiddenClientMethods.Check(null, "UnregisterAllNutstoreSyncRoots");

        Assert.NotNull(reason);
        // 只写「禁止」没用，必须让读的人知道为什么
        Assert.Contains("同步根", reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ClientSymbols.DeleteSandbox, "删除云端沙箱")]
    [InlineData("MakeDeleteSandboxRequest", "删除云端沙箱")]
    [InlineData(ClientSymbols.DeleteObject, "删除云端文件或目录")]
    public void 混淆删除入口的拒绝理由说明实际行为(string method, string consequence)
    {
        var reason = ForbiddenClientMethods.Check(ClientSymbols.CloudOpsType, "  " + method + "  ");

        Assert.NotNull(reason);
        Assert.Contains(consequence, reason, StringComparison.Ordinal);
    }

    [Fact]
    public void 清单导出的条数与实际一致()
    {
        var json = ForbiddenClientMethods.Describe();

        Assert.Contains("\"count\"", json, StringComparison.Ordinal);
        foreach (var method in DangerousMethods())
        {
            Assert.Contains(method, json, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void 专用放行条目仍在导出清单里并标注范围()
    {
        var json = ForbiddenClientMethods.Describe();

        Assert.Contains("\"dedicatedOnly\"", json, StringComparison.Ordinal);
        Assert.Contains("\"dedicated-only\"", json, StringComparison.Ordinal);
        // 专用放行不等于放松：通用通道仍然拒绝。
        Assert.NotNull(ForbiddenClientMethods.Check(null, ClientSymbols.DeleteObject));
        Assert.True(ForbiddenClientMethods.IsDedicatedOnly(ClientSymbols.DeleteObject));
        Assert.False(ForbiddenClientMethods.IsDedicatedOnly(null));
        Assert.False(ForbiddenClientMethods.IsDedicatedOnly("   "));
    }
}
