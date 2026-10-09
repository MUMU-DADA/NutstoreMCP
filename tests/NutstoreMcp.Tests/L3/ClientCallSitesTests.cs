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
    public void 未使用的调用点要标注出来()
    {
        // cfd1df84 已定位但没接出来。留着登记是为了防止「它已经在清单里了，
        // 直接调吧」这种推理 —— 它必须重新走一遍确认流程。
        var unused = ClientCallSites.Find("cfd1df8451110086567f3d56edf4697e8");

        Assert.NotNull(unused);
        Assert.Contains("未使用", unused!.Purpose, StringComparison.Ordinal);
    }

    [Fact]
    public void 云端操作层的调用点用同一个类型名()
    {
        // 哈希名是 33 个十六进制字符 —— 别按固定长度判断，
        // 那样写错一个数字就会静默漏掉全部（第一版就踩了这个）
        var ops = ClientCallSites.Registered
            .Where(c => c.MethodName.Length >= 20 && c.MethodName.All(Uri.IsHexDigit))
            .ToList();

        Assert.Equal(3, ops.Count);

        foreach (var site in ops)
        {
            // 哈希形式的调用点必须指向那个已知的混淆类，
            // 不能出现「不知道从哪冒出来的第二个哈希类」
            Assert.Equal(ClientCallSites.CloudOpsType, site.TypeName);
        }
    }
}
