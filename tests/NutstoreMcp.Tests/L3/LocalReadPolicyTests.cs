using NutstoreMcp.Plugin.L3;

namespace NutstoreMcp.Tests;

/// <summary>
/// 读取白名单的守卫测试。核心要证明的是**默认关闭**与**越界拒绝**：
/// 让调用方传本地路径去读，等于给了读本机任意文件的能力，
/// 所以白名单缺失时必须是「不可用」而不是「随便读」。
/// </summary>
public sealed class LocalReadPolicyTests : IDisposable
{
    // 不用 %TEMP%：本机的 Temp 目录在当前环境下建目录会被拒绝（沙箱 ACL），
    // 而测试输出目录一定可写。
    private readonly string _temp = Path.Combine(
        AppContext.BaseDirectory, "policy-temp-" + Guid.NewGuid().ToString("N").Substring(0, 8));

    public LocalReadPolicyTests() => Directory.CreateDirectory(_temp);

    public void Dispose()
    {
        try { Directory.Delete(_temp, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void 白名单为空时本地文件模式不可用()
    {
        var file = Write("a.txt", "hello");

        foreach (var roots in new[] { Array.Empty<string>(), LocalReadPolicy.ParseRoots(null), LocalReadPolicy.ParseRoots("") })
        {
            var error = LocalReadPolicy.Validate(file, roots, 1000, out var canonical, out var length);
            Assert.NotNull(error);
            Assert.Contains("未启用", error);
            Assert.Null(canonical);
            Assert.Equal(0, length);
        }
    }

    [Fact]
    public void 解析白名单时跳过注释空行与相对路径并按大小写去重()
    {
        var root = Path.Combine(_temp, "root");
        var text = string.Join("\n", new[]
        {
            "# 这是注释",
            "",
            "   ",
            root,
            root.ToUpperInvariant(),      // 大小写不同视为同一个根
            root + "\\",                  // 尾部反斜杠要归一
            "relative\\path",             // 相对路径不接收
        });

        var roots = LocalReadPolicy.ParseRoots(text);

        Assert.Single(roots);
        Assert.Equal(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar), roots[0]);
    }

    [Theory]
    [InlineData(@"C:\data\sub\a.txt", @"C:\data", true)]
    [InlineData(@"C:\data\a.txt", @"C:\data", true)]
    [InlineData(@"C:\DATA\a.txt", @"C:\data", true)]                 // 大小写不敏感
    [InlineData(@"C:\data-evil\a.txt", @"C:\data", false)]           // 共同前缀不算在根下
    [InlineData(@"C:\database\a.txt", @"C:\data", false)]
    [InlineData(@"D:\data\a.txt", @"C:\data", false)]
    [InlineData(@"C:\data", @"C:\data", true)]                       // 根本身
    [InlineData(@"C:\data\", @"C:\", true)]                          // 根是盘符
    [InlineData(@"D:\x\y", @"C:\", false)]
    public void 归属判断必须按目录边界而不是字符串前缀(string path, string root, bool expected)
    {
        Assert.Equal(expected, LocalReadPolicy.IsUnderAnyRoot(path, new[] { root }));
    }

    [Fact]
    public void 白名单外的文件被拒绝且提示写明路径()
    {
        var file = Write("outside.txt", "hello");
        var roots = LocalReadPolicy.ParseRoots(Path.Combine(_temp, "another-root"));

        var error = LocalReadPolicy.Validate(file, roots, 1000, out _, out _);

        Assert.NotNull(error);
        Assert.Contains("不在读取白名单内", error);
        Assert.Contains("outside.txt", error);
    }

    [Fact]
    public void 白名单内的正常文件通过并给出规范路径与长度()
    {
        var file = Write("ok.txt", "hello world");
        var roots = LocalReadPolicy.ParseRoots(_temp);

        var error = LocalReadPolicy.Validate(file, roots, 1000, out var canonical, out var length);

        Assert.Null(error);
        Assert.Equal(Path.GetFullPath(file), canonical);
        Assert.Equal(11, length);
    }

    [Fact]
    public void 相对路径目录空文件与超限文件都被拒绝()
    {
        var roots = LocalReadPolicy.ParseRoots(_temp);

        Assert.Contains("绝对路径",
            LocalReadPolicy.Validate("relative.txt", roots, 1000, out _, out _));

        var dir = Path.Combine(_temp, "sub");
        Directory.CreateDirectory(dir);
        Assert.Contains("是目录",
            LocalReadPolicy.Validate(dir, roots, 1000, out _, out _));

        var empty = Write("empty.txt", "");
        Assert.Contains("空的",
            LocalReadPolicy.Validate(empty, roots, 1000, out _, out _));

        var big = Write("big.txt", new string('x', 20));
        var tooLarge = LocalReadPolicy.Validate(big, roots, 10, out _, out _);
        Assert.NotNull(tooLarge);
        Assert.Contains("超过", tooLarge);

        Assert.Contains("不存在",
            LocalReadPolicy.Validate(Path.Combine(_temp, "missing.txt"), roots, 1000, out _, out _));
    }

    [Fact]
    public void 读取时必须核对长度并在长度变化时中止()
    {
        var file = Write("exact.txt", "1234567890");
        var bytes = LocalReadPolicy.ReadExactly(file, 10);
        Assert.Equal("1234567890", System.Text.Encoding.UTF8.GetString(bytes));

        // 期望长度与实际不符（模拟校验之后文件被改动）
        Assert.Throws<InvalidOperationException>(() => LocalReadPolicy.ReadExactly(file, 5));
        Assert.Throws<InvalidOperationException>(() => LocalReadPolicy.ReadExactly(file, 20));
    }

    private string Write(string name, string content)
    {
        var path = Path.Combine(_temp, name);
        File.WriteAllText(path, content);
        return path;
    }
}
