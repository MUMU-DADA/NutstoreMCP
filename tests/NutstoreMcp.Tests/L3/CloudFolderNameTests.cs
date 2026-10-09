using NutstoreMcp.Plugin.L3;

namespace NutstoreMcp.Tests;

/// <summary>
/// 云端文件夹名校验的测试。
/// </summary>
/// <remarks>
/// 这个名字会被写进用户的云盘。校验错了的代价是要么拒绝合法名字（烦人），
/// 要么在云端造出一个诡异的名字（更糟）。所以正反两面都要测。
/// </remarks>
public class CloudFolderNameTests
{
    [Theory]
    [InlineData("mcp-test")]
    [InlineData("工作日志")]
    [InlineData("a")]
    [InlineData("My Folder 2026")]
    [InlineData("folder.with.dots")]
    [InlineData("folder-with_underscore")]
    [InlineData("emoji-😀")]
    public void 合法名字放行(string name)
    {
        Assert.Null(CloudFolderName.Validate(name));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void 空名字拒绝(string? name)
    {
        Assert.NotNull(CloudFolderName.Validate(name));
    }

    [Theory]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("a:b")]
    [InlineData("a*b")]
    [InlineData("a?b")]
    [InlineData("a\"b")]
    [InlineData("a<b")]
    [InlineData("a>b")]
    [InlineData("a|b")]
    public void 非法字符拒绝(string name)
    {
        var reason = CloudFolderName.Validate(name);
        Assert.NotNull(reason);
        Assert.Contains("非法字符", reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    public void 点与双点拒绝(string name)
    {
        Assert.NotNull(CloudFolderName.Validate(name));
    }

    [Fact]
    public void 过长拒绝()
    {
        Assert.Null(CloudFolderName.Validate(new string('a', CloudFolderName.MaxLength)));
        Assert.NotNull(CloudFolderName.Validate(new string('a', CloudFolderName.MaxLength + 1)));
    }

    [Theory]
    [InlineData(" lead")]
    [InlineData("trail ")]
    [InlineData("\ttab")]
    public void 首尾空白拒绝(string name)
    {
        // 「看起来一样其实不同」的两个云端文件夹是最难排查的那类问题
        Assert.NotNull(CloudFolderName.Validate(name));
    }

    [Fact]
    public void 控制字符拒绝()
    {
        Assert.NotNull(CloudFolderName.Validate("a\u0000b"));
        Assert.NotNull(CloudFolderName.Validate("a\nb"));
    }

    [Fact]
    public void 拒绝理由要能定位问题()
    {
        // 只说「非法」没用 —— 得让调用者知道是哪个字符/哪个规则
        Assert.Contains("空", CloudFolderName.Validate(null)!, StringComparison.Ordinal);
        Assert.Contains("/", CloudFolderName.Validate("a/b")!, StringComparison.Ordinal);
        Assert.Contains("长度", CloudFolderName.Validate(new string('x', 101))!, StringComparison.Ordinal);
        Assert.Contains("空白", CloudFolderName.Validate(" x")!, StringComparison.Ordinal);
    }
}
