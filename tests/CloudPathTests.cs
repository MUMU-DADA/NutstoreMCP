using NutstoreMcp;

namespace NutstoreMcp.Tests;

public class CloudPathTests
{
    [Fact]
    public void Normalize_Basics()
    {
        Assert.Equal("/a/b", CloudPath.Normalize("/a/b"));
        Assert.Equal("/a/b", CloudPath.Normalize("a/b"));
        Assert.Equal("/a/b", CloudPath.Normalize("/a//b/"));
        Assert.Equal("/", CloudPath.Normalize("/"));
        Assert.Equal("/", CloudPath.Normalize(""));
        Assert.Equal("/我的坚果云/文档", CloudPath.Normalize("/我的坚果云/文档"));
    }

    [Theory]
    [InlineData("/a/../b")]
    [InlineData("/..")]
    [InlineData("/a/./b")]
    [InlineData("C:\\Users\\x")]
    [InlineData("/a\u0000b")]
    public void Normalize_RejectsTraversalAndIllegalChars(string input)
        => Assert.Throws<PathNotAllowedException>(() => CloudPath.Normalize(input));

    [Fact]
    public void Normalize_RejectsNull()
        => Assert.Throws<PathNotAllowedException>(() => CloudPath.Normalize(null));

    [Fact]
    public void Encode_PerSegment()
    {
        Assert.Equal("/a/b", CloudPath.Encode("/a/b"));
        Assert.Equal("/%E6%88%91%E7%9A%84%E5%9D%9A%E6%9E%9C%E4%BA%91", CloudPath.Encode("/我的坚果云"));
        Assert.Equal("/a%20b/c", CloudPath.Encode("/a b/c"));
    }

    [Fact]
    public void PercentEncodedTraversal_CannotEscapeAllowlist()
    {
        // 字面量语义：%2e%2e 是普通目录名，编码后 % 变成 %25，
        // 服务端收到的是字面字符串而不是 ".."
        var p = CloudPath.Normalize("/root/%2e%2e/secret");
        Assert.Equal("/root/%2e%2e/secret", p);

        var url = CloudPath.Encode(p);
        Assert.Contains("%252e%252e", url, StringComparison.Ordinal);
        Assert.DoesNotContain("%2e%2e/", url, StringComparison.Ordinal);

        // 且它仍然落在 /root 白名单内（因为它本来就是 /root 下的字面路径）
        Assert.True(CloudPath.IsAllowed(p, ["/root"]));
    }

    [Fact]
    public void ToUrl_Joins()
    {
        Assert.Equal("https://dav.jianguoyun.com/dav/a", CloudPath.ToUrl("https://dav.jianguoyun.com/dav", "/a"));
        Assert.Equal("https://dav.jianguoyun.com/dav/a", CloudPath.ToUrl("https://dav.jianguoyun.com/dav/", "/a"));
        Assert.Equal("https://dav.jianguoyun.com/dav/", CloudPath.ToUrl("https://dav.jianguoyun.com/dav", "/"));
    }

    [Fact]
    public void FromHref_HandlesAbsoluteAbsolutePathAndRelative()
    {
        const string baseUrl = "https://dav.jianguoyun.com/dav";
        Assert.Equal("/a/b", CloudPath.FromHref("https://dav.jianguoyun.com/dav/a/b", baseUrl));
        Assert.Equal("/a/b", CloudPath.FromHref("/dav/a/b", baseUrl));
        Assert.Equal("/", CloudPath.FromHref("/dav/", baseUrl));
        Assert.Equal("/", CloudPath.FromHref("/dav", baseUrl));
        Assert.Equal("/我的/note.md", CloudPath.FromHref("/dav/%E6%88%91%E7%9A%84/note.md", baseUrl));
        Assert.Equal("/a/b", CloudPath.FromHref("/dav/a/b?x=1#frag", baseUrl));
        Assert.Equal("/docs", CloudPath.FromHref("/dav/docs/", baseUrl));
    }

    [Fact]
    public void IsAllowed_EmptyAllowlistDeniesEverything()
    {
        Assert.False(CloudPath.IsAllowed("/anything", []));
        Assert.False(CloudPath.IsAllowed("/anything", null));
    }

    [Fact]
    public void IsAllowed_MatchesOnSegmentBoundaryNotStringPrefix()
    {
        Assert.True(CloudPath.IsAllowed("/code", ["/code"]));
        Assert.True(CloudPath.IsAllowed("/code/a.md", ["/code"]));
        Assert.False(CloudPath.IsAllowed("/code2", ["/code"]));
        Assert.False(CloudPath.IsAllowed("/code2/a", ["/code"]));
        Assert.False(CloudPath.IsAllowed("/other", ["/code"]));
    }

    [Fact]
    public void IsAllowed_ExplicitRootAllowsEverything()
        => Assert.True(CloudPath.IsAllowed("/any/thing", ["/"]));

    [Fact]
    public void AssertAllowed_ErrorCarriesAllowlist()
    {
        var ex = Assert.Throws<PathNotAllowedException>(() => CloudPath.AssertAllowed("/secret", ["/code"]));
        Assert.Contains("/code", ex.Hint!, StringComparison.Ordinal);

        var empty = Assert.Throws<PathNotAllowedException>(() => CloudPath.AssertAllowed("/secret", []));
        Assert.Contains("拒绝一切", empty.Hint!, StringComparison.Ordinal);
    }

    [Fact]
    public void NormalizeRoots_DropsInvalidEntriesInsteadOfThrowing()
    {
        Assert.Equal(["/code"], CloudPath.NormalizeRoots(new string?[] { "code", "/code", "  ", "/a/../b", null }));
        Assert.Equal(["/"], CloudPath.NormalizeRoots(["/"]));
        Assert.Empty(CloudPath.NormalizeRoots(null));
    }

    [Fact]
    public void AssertLocalPathAllowed_UsesPathRelativeSoSiblingPrefixIsNotAllowed()
    {
        var root = Path.Combine(Path.GetTempPath(), "ns-mcp-test-root");
        var evil = Path.Combine(Path.GetTempPath(), "ns-mcp-test-root-evil");

        Assert.Equal(
            Path.GetFullPath(Path.Combine(root, "a.txt")),
            CloudPath.AssertLocalPathAllowed(Path.Combine(root, "a.txt"), [root]));
        Assert.Equal(Path.GetFullPath(root), CloudPath.AssertLocalPathAllowed(root, [root]));
        Assert.Equal(
            Path.GetFullPath(Path.Combine(root, "sub", "b")),
            CloudPath.AssertLocalPathAllowed(Path.Combine(root, "sub", "b"), [root]));

        // 关键：相邻前缀目录不得被放行
        Assert.Throws<PathNotAllowedException>(
            () => CloudPath.AssertLocalPathAllowed(Path.Combine(evil, "x"), [root]));
        Assert.Throws<PathNotAllowedException>(
            () => CloudPath.AssertLocalPathAllowed(Path.Combine(Path.GetTempPath(), "elsewhere"), [root]));

        // 用 .. 也不能逃出去
        Assert.Throws<PathNotAllowedException>(
            () => CloudPath.AssertLocalPathAllowed(Path.Combine(root, "..", "outside"), [root]));

        Assert.Throws<PathNotAllowedException>(() => CloudPath.AssertLocalPathAllowed("/x", []));
    }

    [Fact]
    public void PathComparisonIsOrdinalNotCultureSensitive()
    {
        // 土耳其语环境下 ToLower("I") 会变成 "ı"，文化敏感比较会让 /CODE 与 /code 意外相等。
        // 我们要求严格 Ordinal：大小写不同就是不同路径。
        Assert.False(CloudPath.IsAllowed("/CODE", ["/code"]));
        Assert.False(CloudPath.IsAllowed("/code", ["/CODE"]));
    }
}
