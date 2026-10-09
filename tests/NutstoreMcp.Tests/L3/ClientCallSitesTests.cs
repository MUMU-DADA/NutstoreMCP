// 验证客户端调用登记。
using NutstoreMcp.ClientBindings;
using NutstoreMcp.Plugin.L3;

namespace NutstoreMcp.Tests;

/// <summary>
/// 混淆调用点登记的测试。
/// </summary>
/// <remarks>
/// 这些测试守的是一件具体的事：**禁止清单是按名字匹配的，挡不住哈希调用**。
/// 所以每一个哈希调用都必须在 ClientCallSites 里登记并说明理由。
/// </remarks>
public class ClientCallSitesTests
{
    [Fact]
    public void 每个登记项都要说清楚改不改状态()
    {
        foreach (var site in ClientCallSites.Registered)
        {
            // Mutates 是 bool，不可能「没说」—— 但理由必须写了
            Assert.False(
                string.IsNullOrWhiteSpace(site.Why),
                $"{site.MethodName} 没写安全理由");

            Assert.True(
                site.Why.Length > 20,
                $"{site.MethodName} 的理由太短，说不清为什么安全: {site.Why}");
        }
    }

    [Fact]
    public void 不许重复登记()
    {
        var names = ClientCallSites.Registered
            .Select(c => c.MethodName)
            .ToList();

        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void 改状态的调用必须点明它改什么()
    {
        foreach (var site in ClientCallSites.Mutating)
        {
            // 不能只说「会改状态」—— 得说清改的是什么
            Assert.True(
                site.Purpose.Length > 10,
                $"{site.MethodName} 的 Purpose 太短，没说清它做什么: {site.Purpose}");

            Assert.Contains(
                "不",
                site.Why,
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void 只读的调用要明确说是纯读()
    {
        foreach (var site in ClientCallSites.ReadOnly)
        {
            Assert.Contains(
                "纯读",
                site.Why,
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void 禁止清单与调用点清单不冲突()
    {
        // 一个方法既在禁止清单里又在调用点清单里，说明两处判断矛盾了
        foreach (var site in ClientCallSites.Registered)
        {
            Assert.Null(ForbiddenClientMethods.Check(site.TypeName, site.MethodName));
        }
    }

    [Fact]
    public void 建目录登记包含已验证的冲突边界()
    {
        var create = ClientCallSites.Find(ClientSymbols.CreateDirectory);

        Assert.NotNull(create);
        Assert.True(create!.Mutates);
        Assert.Contains("DuplicateName", create.Why, StringComparison.Ordinal);
    }

    [Fact]
    public void 云端操作层的调用点用同一个类型名()
    {
        var ops = ClientCallSites.Registered
            .Where(c => c.MethodName.Length >= 20 && c.MethodName.All(Uri.IsHexDigit))
            .ToList();

        // 不写死条数 —— 那个断言在合法新增调用点时就会断（已经发生过一次）。
        // 真正要守住的是「筛选没漏」，NotEmpty 就够了；
        // 下一条断言再守住「每个哈希名都指向那个已知的混淆类」。
        Assert.NotEmpty(ops);

        foreach (var site in ops)
        {
            // 哈希名是 33 个十六进制字符，不是 32（第一版按 32 写，一个都没匹配上）
            Assert.Equal(33, site.MethodName.Length);

            // 哈希形式的调用点必须指向那个已知的混淆类，
            // 不能出现「不知道从哪冒出来的第二个哈希类」
            Assert.Equal(ClientCallSites.CloudOpsType, site.TypeName);
        }
    }
}
