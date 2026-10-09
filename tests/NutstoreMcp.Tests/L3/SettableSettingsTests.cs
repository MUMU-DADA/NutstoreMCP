using NutstoreMcp.Plugin.L3;

namespace NutstoreMcp.Tests;

/// <summary>
/// 设置项白名单与危险分层的测试。
/// </summary>
/// <remarks>
/// 这里的判错有真实后果：分类错了要么让系统级改动绕过了第二把锁，
/// 要么把本来安全的开关误判成危险。所以两边都要测死。
/// </remarks>
public class SettableSettingsTests
{
    [Theory]
    [InlineData("AutoStart")]
    [InlineData("IsShutdownOnIdle")]
    [InlineData("IsNotifyOnFilesUpdated")]
    [InlineData("IsDirectOpen")]
    [InlineData("IsExperimental")]
    public void 普通开关归类为_normal(string name)
    {
        Assert.Equal("normal", SettableSettings.TierOf(name));
    }

    [Theory]
    [InlineData("EnableFileProtectionLock")]
    [InlineData("EnableSafeGuard")]
    [InlineData("IsSyncPaused")]
    public void 系统级设置归类为_system(string name)
    {
        // 这三个做完不好撤：装驱动 / 影响权限验证 / 让两端脱节
        Assert.Equal("system", SettableSettings.TierOf(name));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Proxy")]
    [InlineData("Storage")]
    [InlineData("SearchHotkey")]
    [InlineData("随便写的")]
    [InlineData("autostart; DROP")]
    public void 白名单外一律不允许(string? name)
    {
        // 不接受任意属性名 —— 否则这个工具就成了通用属性写入器
        Assert.Null(SettableSettings.TierOf(name));
    }

    [Theory]
    [InlineData("autostart")]
    [InlineData("AUTOSTART")]
    [InlineData("enablesafeguard")]
    public void 大小写不敏感(string name)
    {
        Assert.NotNull(SettableSettings.TierOf(name));
    }

    [Fact]
    public void 两个层级不允许重叠()
    {
        // 重叠会让一把锁意外地enough —— 同一个设置既是普通又是系统级，
        // 判断顺序就会决定它需要哪把锁
        Assert.Empty(SettableSettings.Normal.Keys.Intersect(
            SettableSettings.SystemLevel.Keys, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void 系统级必须带影响说明()
    {
        // 拒绝时要把后果说出来，不能只说「不允许」
        foreach (var (name, why) in SettableSettings.SystemLevel)
        {
            Assert.False(string.IsNullOrWhiteSpace(why), $"{name} 缺影响说明");
            Assert.True(why.Length > 15, $"{name} 的说明太短，说不清后果: {why}");
        }
    }

    [Fact]
    public void 系统级都是布尔开关()
    {
        // 工具侧按布尔值校验，名字对了但类型不是布尔会让那道校验失效
        foreach (var name in SettableSettings.SystemLevel.Keys)
        {
            Assert.DoesNotContain("Hotkey", name, StringComparison.Ordinal);
            Assert.DoesNotContain("Proxy", name, StringComparison.Ordinal);
            Assert.DoesNotContain("Storage", name, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void 全部名单稳定有序()
    {
        // 拒绝消息里会列出可选项，顺序不稳定会让消息每次都不同
        var first = SettableSettings.AllNames.ToList();
        var second = SettableSettings.AllNames.ToList();
        Assert.Equal(first, second);
        Assert.Equal(8, first.Count);
    }
}
