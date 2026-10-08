using NutstoreMcp.Plugin.L3;

namespace NutstoreMcp.Tests;

/// <summary>
/// L3 写入守卫的测试。
///
/// <para>
/// 这一组是整个仓库里最该被测的东西：L3 唯一可能丢数据的地方就是
/// 「把本地目录绑到云端沙箱」，而放行判断是纯逻辑 —— 可以离线穷举，
/// 不必拿用户的真实数据当验证环境。
/// </para>
///
/// <para>
/// 放在主测试工程而不是 recon/ 的探针里：探针不在解决方案内、CI 不跑它，
/// 安全规则待在那种地方等于没测。
/// </para>
/// </summary>
public class WriteGuardsTests
{
    /// <summary>默认构造一个「全部满足」的请求，各用例只改自己关心的那项。</summary>
    private static WriteGuards.Request Ok(
        bool unlock = true,
        bool confirm = true,
        bool dryRun = false,
        string path = @"C:\tmp\newsync",
        bool exists = true,
        bool isDir = true,
        bool isEmpty = true,
        bool bound = false,
        string[]? existing = null,
        long sandboxId = 42,
        string? syncState = "NotSynced")
        => new(unlock, confirm, dryRun, path, exists, isDir, isEmpty, bound,
               existing ?? [], sandboxId, syncState);

    // ── 三道闸 ──

    [Fact]
    public void 默认全拒()
    {
        var v = WriteGuards.Evaluate(Ok(unlock: false, confirm: false, dryRun: true));

        Assert.False(v.Allowed);
        Assert.False(v.WouldWrite);
        Assert.True(v.Refusals.Count >= 2, "环境变量和 confirm 各应产生一条拒绝理由");
    }

    [Theory]
    [InlineData(false, true)]   // 有 confirm，没解锁
    [InlineData(true, false)]   // 有解锁，没 confirm
    public void 任一闸缺失即拒(bool unlock, bool confirm)
    {
        var v = WriteGuards.Evaluate(Ok(unlock: unlock, confirm: confirm));
        Assert.False(v.Allowed);
    }

    [Fact]
    public void 全部满足且非预演时才真的写()
    {
        var wet = WriteGuards.Evaluate(Ok(dryRun: false));
        Assert.True(wet.Allowed);
        Assert.True(wet.WouldWrite);
    }

    [Fact]
    public void 预演时允许但不写()
    {
        var dry = WriteGuards.Evaluate(Ok(dryRun: true));
        Assert.True(dry.Allowed);
        Assert.False(dry.WouldWrite);
    }

    // ── 核心数据安全 ──

    [Fact]
    public void 非空目录必拒_即使其它条件全满足()
    {
        // 这是整个守卫存在的理由。解锁、确认、非预演、合法沙箱 —— 都不够，
        // 因为非空目录与云端沙箱绑定后会双向合并，冲突时可能覆盖用户的文件。
        var v = WriteGuards.Evaluate(Ok(isEmpty: false));

        Assert.False(v.Allowed);
        Assert.False(v.WouldWrite);
        Assert.Contains(v.Refusals, r => r.Contains("非空", StringComparison.Ordinal));
    }

    [Fact]
    public void 目录不存在必拒()
    {
        var v = WriteGuards.Evaluate(Ok(exists: false));
        Assert.False(v.Allowed);
    }

    [Fact]
    public void 路径为空必拒()
    {
        var v = WriteGuards.Evaluate(Ok(path: "   "));
        Assert.False(v.Allowed);
    }

    [Fact]
    public void 路径不是目录必拒()
    {
        var v = WriteGuards.Evaluate(Ok(isDir: false));
        Assert.False(v.Allowed);
    }

    // ── 重叠检测 ──

    [Theory]
    [InlineData(@"C:\Users\me\sync", @"C:\Users\me\sync", true)]          // 完全相同
    [InlineData(@"C:\Users\me\sync\sub", @"C:\Users\me\sync", true)]      // 现有根的子目录
    [InlineData(@"C:\Users\me", @"C:\Users\me\sync", true)]                // 现有根的父目录
    [InlineData(@"C:\Users\me\SYNC\sub", @"c:\users\me\sync", true)]       // 大小写不同仍算重叠
    [InlineData(@"C:\Users\me\sync", @"C:\Users\me\sync2", false)]         // 前缀相似但无关
    [InlineData(@"C:\Users\me\other", @"C:\Users\me\sync", false)]         // 完全无关
    public void 嵌套判定(string candidate, string existing, bool shouldOverlap)
    {
        var v = WriteGuards.Evaluate(Ok(path: candidate, existing: [existing]));
        Assert.Equal(shouldOverlap, !v.Allowed);
    }

    [Fact]
    public void 已绑定的路径必拒()
    {
        var v = WriteGuards.Evaluate(Ok(bound: true));
        Assert.False(v.Allowed);
    }

    // ── 沙箱身份 ──

    [Fact]
    public void 没有沙箱ID必拒()
    {
        // 本操作只绑定**已存在**的云端沙箱，不在云端新建任何东西。
        // 没有 ID 就无从谈起。
        var v = WriteGuards.Evaluate(Ok(sandboxId: 0));
        Assert.False(v.Allowed);
    }

    [Fact]
    public void 缺沙箱状态时fail_closed()
    {
        // 留空不等于「无所谓」：没提供状态就等于没证明它尚未绑定
        var v = WriteGuards.Evaluate(Ok(syncState: null));
        Assert.False(v.Allowed);
        Assert.Contains(v.Refusals, r => r.Contains("状态", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("SyncedToDisk")]
    [InlineData("SyncPlaceholder")]
    public void 已绑定的沙箱拒绝重绑(string state)
    {
        var v = WriteGuards.Evaluate(Ok(syncState: state));
        Assert.False(v.Allowed);
    }

    [Fact]
    public void 状态判断不区分大小写()
    {
        var v = WriteGuards.Evaluate(Ok(syncState: "notsynced"));
        Assert.True(v.Allowed);
    }

    // ── 放行时的说明 ──

    [Fact]
    public void 放行时也带上注意事项()
    {
        var v = WriteGuards.Evaluate(Ok(dryRun: true));

        Assert.True(v.Allowed);
        Assert.NotEmpty(v.Notes);
        Assert.Contains(v.Notes, n => n.Contains("空", StringComparison.Ordinal));
    }
}
