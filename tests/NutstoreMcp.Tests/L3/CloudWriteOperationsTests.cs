using System.Text.Json;
using NutstoreMcp.Plugin.L3;

namespace NutstoreMcp.Tests;

/// <summary>用可变云端模型验证拒绝、调用失败和写后真实状态，不接触真实客户端。</summary>
public class CloudWriteOperationsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("relative.txt")]
    [InlineData("/a/../b")]
    [InlineData("/a/./b")]
    [InlineData("/a//b")]
    [InlineData("/a/b/")]
    [InlineData("/a\\b")]
    [InlineData("/a:b")]
    [InlineData("/a?b")]
    [InlineData("/a\u0001b")]
    [InlineData("/a/b ")]
    [InlineData("/a/ b")]
    [InlineData("/a/b.")]
    [InlineData("/CON.txt")]
    [InlineData("/a/nul")]
    [InlineData("/COM1.log")]
    [InlineData("/lpt9")]
    [InlineData("/COM¹")]
    [InlineData("/CONOUT$")]
    public void 非规范路径在任何云端读写之前被拒绝(string? path)
    {
        var cloud = new Cloud();
        using var result = cloud.Move(target: path, explicitTarget: true);

        Assert.False(Flag(result, "allowed"));
        Assert.False(Flag(result, "attempted"));
        Assert.Empty(cloud.ListCalls);
        Assert.Equal(0, cloud.BindingCalls);
        Assert.Equal(0, cloud.WriteCalls);
    }

    [Fact]
    public void 路径限制按严格UTF8字节计数且根可用于只读ACL()
    {
        var asciiBoundary = "/" + new string('a', 255) + "/" + new string('b', 255) + "/" + new string('c', 254);
        Assert.Null(CloudObjectPath.Validate(asciiBoundary));
        Assert.NotNull(CloudObjectPath.Validate(asciiBoundary + "c"));
        Assert.NotNull(CloudObjectPath.Validate("/" + new string('a', 256)));
        Assert.Null(CloudObjectPath.Validate("/" + new string('中', 255)));
        Assert.NotNull(CloudObjectPath.Validate("/" + new string('中', 256)));
        Assert.NotNull(CloudObjectPath.Validate("/a\ud800"));
        Assert.Null(CloudObjectPath.Validate("/", allowRoot: true));
        Assert.Equal("/", CloudObjectPath.Parent("/file.txt"));
        Assert.Equal("/from", CloudObjectPath.Parent("/from/a.txt"));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void 未解锁或未确认直接拒绝且不调用任何委托(bool unlocked, bool confirm)
    {
        var cloud = new Cloud();
        using var result = cloud.Move(unlocked: unlocked, confirm: confirm);

        Assert.False(Flag(result, "allowed"));
        Assert.Equal(0, cloud.BindingCalls);
        Assert.Empty(cloud.ListCalls);
        Assert.Equal(0, cloud.WriteCalls);
    }

    [Fact]
    public void 预演提供已验证probe且不调用写入()
    {
        var cloud = new Cloud();
        using var result = cloud.Move(dryRun: true);

        Assert.True(Flag(result, "allowed"));
        Assert.False(Flag(result, "attempted"));
        Assert.False(Flag(result, "executed"));
        Assert.True(result.RootElement.GetProperty("probe").GetProperty("preflightOk").GetBoolean());
        Assert.Equal(0, cloud.WriteCalls);
        Assert.Contains("/from", cloud.ListCalls);
        Assert.Contains("/to", cloud.ListCalls);
    }

    [Theory]
    [InlineData(7, 999)]
    [InlineData(999, 11)]
    [InlineData(0, 11)]
    [InlineData(7, 0)]
    public void 沙箱身份必须同时匹配ID和magic(long id, long magic)
    {
        var cloud = new Cloud();
        using var result = cloud.Move(id: id, magic: magic);
        Assert.False(Flag(result, "allowed"));
        Assert.Equal(0, cloud.WriteCalls);
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("relative", true)]
    [InlineData("C:relative", true)]
    [InlineData("\\current-drive", true)]
    [InlineData("C:\\new-sync", false)]
    public void 缺失本地真实绑定时拒绝(string root, bool exists)
    {
        var cloud = new Cloud { LocalRoot = root, LocalExists = exists };
        using var result = cloud.Move();
        Assert.False(Flag(result, "allowed"));
        Assert.Equal(0, cloud.WriteCalls);
    }

    [Theory]
    [InlineData("{\"ok\":false,\"entries\":[]}")]
    [InlineData("{\"ok\":\"true\",\"entries\":[]}")]
    [InlineData("{\"ok\":true}")]
    [InlineData("{\"ok\":true,\"entries\":[null]}")]
    [InlineData("{\"ok\":true,\"entries\":[42]}")]
    [InlineData("{\"ok\":true,\"entries\":[],\"truncated\":true}")]
    [InlineData("{\"ok\":true,\"entries\":[],\"truncated\":\"true\"}")]
    [InlineData("{\"ok\":true,\"entries\":[],\"truncated\":null}")]
    [InlineData("{\"ok\":true,\"entries\":[],\"hasMore\":\"false\"}")]
    [InlineData("{\"ok\":true,\"entries\":[],\"hasMore\":null}")]
    [InlineData("{\"ok\":true,\"entries\":[],\"complete\":false}")]
    [InlineData("not JSON")]
    public void 父目录读取失败或不完整不能当作目标不存在(string json)
    {
        var cloud = new Cloud { ListingOverride = path => path == "/to" ? json : null };
        using var result = cloud.Move();
        Assert.False(Flag(result, "allowed"));
        Assert.False(Flag(result, "attempted"));
        Assert.Equal(0, cloud.WriteCalls);
    }

    [Fact]
    public void 嵌套移动分别查两父目录并以真实内容验证成功()
    {
        var cloud = new Cloud();
        using var result = cloud.Move();

        Assert.True(Flag(result, "attempted"));
        Assert.True(Flag(result, "executed"));
        Assert.True(Flag(result, "wouldWrite"));
        var verification = result.RootElement.GetProperty("verification");
        Assert.True(verification.GetProperty("ok").GetBoolean());
        Assert.False(verification.GetProperty("sourcePresent").GetBoolean());
        Assert.True(verification.GetProperty("targetPresent").GetBoolean());
        Assert.True(verification.GetProperty("contentMatches").GetBoolean());
        Assert.Equal(new[] { "/from", "/to", "/from", "/to" }, cloud.ListCalls);
        Assert.Equal(1, cloud.WriteCalls);
    }

    [Fact]
    public void 同一父目录只查一次且允许递归根列表()
    {
        var cloud = new Cloud();
        cloud.Objects.Clear();
        cloud.Objects["/a.txt"] = Cloud.File();
        cloud.Source = "/a.txt";
        cloud.Target = "/b.txt";
        cloud.Objects["/elsewhere"] = Cloud.Directory();
        cloud.Objects["/elsewhere/c.txt"] = Cloud.File();
        using var result = cloud.Move();
        Assert.True(Flag(result, "executed"));
        Assert.Equal(new[] { "/", "/" }, cloud.ListCalls);
    }

    [Fact]
    public void 大小写冲突和目标后代存在均拒绝覆盖()
    {
        foreach (var conflicting in new[] { "/to/B.TXT", "/to/b.txt/child.txt" })
        {
            var cloud = new Cloud();
            cloud.Objects[conflicting] = Cloud.File();
            using var result = cloud.Move();
            Assert.False(Flag(result, "allowed"));
            Assert.False(Flag(result, "attempted"));
            Assert.Equal(0, cloud.WriteCalls);
        }
    }

    [Fact]
    public void 对象详情必须精确匹配路径且源目标大小写别名拒绝()
    {
        var cloud = new Cloud();
        using var dry = cloud.Move(source: "/FROM/A.TXT", dryRun: true);
        Assert.False(Flag(dry, "allowed"));
        using var same = cloud.Move(target: "/FROM/A.TXT");
        Assert.False(Flag(same, "allowed"));
        Assert.Equal(0, cloud.WriteCalls);
    }

    [Fact]
    public void 源目录拒绝即使没有后代()
    {
        var cloud = new Cloud();
        cloud.Objects[cloud.Source] = Cloud.Directory();
        using var result = cloud.Move();
        Assert.False(Flag(result, "attempted"));
        Assert.Contains("不是普通文件", result.RootElement.GetProperty("refusals").ToString(), StringComparison.Ordinal);
        Assert.Equal(0, cloud.WriteCalls);
    }

    [Theory]
    [InlineData(true, 1, 3, "abcd")]
    [InlineData(false, 0, 3, "abcd")]
    [InlineData(false, 1, -1, "abcd")]
    [InlineData(false, 1, 3, "")]
    public void 已删除或缺有效元数据的源不能移动(bool deleted, long version, long size, string hash)
    {
        var cloud = new Cloud();
        cloud.Objects[cloud.Source] = new Item("file", deleted, version, size, hash);
        using var result = cloud.Move();
        Assert.False(Flag(result, "attempted"));
        Assert.Equal(0, cloud.WriteCalls);
    }

    [Theory]
    [InlineData("{\"ok\":false,\"reason\":\"rejected\",\"verification\":{\"ok\":false}}")]
    [InlineData("{\"ok\":\"true\",\"verification\":{\"ok\":true}}")]
    [InlineData("{\"ok\":true}")]
    [InlineData("{\"ok\":true,\"verification\":{\"ok\":\"true\"}}")]
    [InlineData("malformed JSON")]
    public void 写入失败JSON或缺默认域真值不会误报成功且会复查(string hookJson)
    {
        var cloud = new Cloud { ApplyWrite = false, HookJson = hookJson };
        using var result = cloud.Move();
        Assert.True(Flag(result, "attempted"));
        Assert.False(Flag(result, "executed"));
        Assert.False(Flag(result, "allowed"));
        Assert.True(result.RootElement.GetProperty("verification").GetProperty("sourcePresent").GetBoolean());
        Assert.False(result.RootElement.GetProperty("verification").GetProperty("targetPresent").GetBoolean());
        Assert.Equal(4, cloud.ListCalls.Count);
        Assert.Equal(1, cloud.WriteCalls);
    }

    [Fact]
    public void 调用抛异常后仍复查且不自动重试()
    {
        var cloud = new Cloud { ApplyWrite = false, ThrowOnWrite = true };
        using var result = cloud.Move();
        Assert.True(Flag(result, "attempted"));
        Assert.False(Flag(result, "executed"));
        Assert.True(result.RootElement.GetProperty("verification").GetProperty("sourcePresent").GetBoolean());
        Assert.Equal(4, cloud.ListCalls.Count);
        Assert.Equal(1, cloud.WriteCalls);
    }

    [Fact]
    public void 客户端失败但状态已改变必须报告观测而非自动重试()
    {
        var cloud = new Cloud { HookJson = "{\"ok\":false,\"reason\":\"response lost\",\"verification\":{\"ok\":true}}" };
        using var result = cloud.Move();
        Assert.False(Flag(result, "executed"));
        var verification = result.RootElement.GetProperty("verification");
        Assert.False(verification.GetProperty("sourcePresent").GetBoolean());
        Assert.True(verification.GetProperty("targetPresent").GetBoolean());
        Assert.True(verification.GetProperty("contentMatches").GetBoolean());
        Assert.Equal(1, cloud.WriteCalls);
    }

    [Fact]
    public void 默认域乐观成功无法绕过插件写后不一致()
    {
        var cloud = new Cloud { CorruptTargetAfterWrite = true };
        using var result = cloud.Move();
        Assert.True(Flag(result, "attempted"));
        Assert.False(Flag(result, "executed"));
        Assert.False(result.RootElement.GetProperty("verification").GetProperty("contentMatches").GetBoolean());
    }

    [Fact]
    public void 写后hash仅大小写变化仍是不同内容()
    {
        var cloud = new Cloud { CaseChangedHashAfterWrite = true };
        using var result = cloud.Move();
        Assert.True(Flag(result, "attempted"));
        Assert.False(Flag(result, "executed"));
        Assert.False(result.RootElement.GetProperty("verification").GetProperty("contentMatches").GetBoolean());
    }

    [Fact]
    public void 写后列表失败报告未知状态而非不存在()
    {
        var cloud = new Cloud();
        cloud.ListingOverride = _ => cloud.WriteCalls > 0 ? "{\"ok\":false}" : null;
        using var result = cloud.Move();
        Assert.False(Flag(result, "executed"));
        Assert.Equal(JsonValueKind.Null, result.RootElement.GetProperty("verification").GetProperty("sourcePresent").ValueKind);
        Assert.Equal(JsonValueKind.Null, result.RootElement.GetProperty("verification").GetProperty("targetPresent").ValueKind);
    }

    [Fact]
    public void 建目录预演和写后目录类型验证()
    {
        var cloud = new Cloud();
        using var dry = cloud.Mkdir(dryRun: true);
        Assert.True(Flag(dry, "allowed"));
        Assert.Equal(0, cloud.WriteCalls);
        using var wet = cloud.Mkdir();
        Assert.True(Flag(wet, "executed"));
        Assert.True(wet.RootElement.GetProperty("verification").GetProperty("targetPresent").GetBoolean());
        Assert.Equal("directory", wet.RootElement.GetProperty("verification").GetProperty("target").GetProperty("objectType").GetString());
    }

    [Fact]
    public void 建目录拒绝现有对象包括大小写别名和目录标记()
    {
        var cloud = new Cloud();
        cloud.ListingOverride = path => path == "/to" ? "{\"ok\":true,\"entries\":[\"/to/B.TXT/\"]}" : null;
        using var result = cloud.Mkdir();
        Assert.False(Flag(result, "attempted"));
        Assert.Equal(0, cloud.WriteCalls);
    }

    [Fact]
    public void 父路径是文件时不能在其中建目录()
    {
        var cloud = new Cloud();
        cloud.Objects["/to"] = Cloud.File();
        using var result = cloud.Mkdir();
        Assert.False(Flag(result, "attempted"));
        Assert.Equal(0, cloud.WriteCalls);
    }

    [Fact]
    public void 建目录写后出现文件不能报告目录创建成功()
    {
        var cloud = new Cloud { CreateFileInsteadOfDirectory = true };
        using var result = cloud.Mkdir();
        Assert.True(Flag(result, "attempted"));
        Assert.False(Flag(result, "executed"));
        Assert.True(result.RootElement.GetProperty("verification").GetProperty("targetPresent").GetBoolean());
        Assert.Equal("file", result.RootElement.GetProperty("verification").GetProperty("target").GetProperty("objectType").GetString());
    }

    private static bool Flag(JsonDocument result, string name) => result.RootElement.GetProperty(name).GetBoolean();

    private sealed record Item(string Type, bool Deleted, long Version, long Size, string Hash);

    private sealed class Cloud
    {
        public string Source { get; set; } = "/from/a.txt";
        public string Target { get; set; } = "/to/b.txt";
        public string LocalRoot { get; set; } = @"C:\new-sync";
        public bool LocalExists { get; set; } = true;
        public int BindingCalls { get; private set; }
        public int WriteCalls { get; private set; }
        public List<string> ListCalls { get; } = new();
        public Dictionary<string, Item> Objects { get; } = new(StringComparer.Ordinal);
        public bool ApplyWrite { get; set; } = true;
        public bool ThrowOnWrite { get; set; }
        public bool CorruptTargetAfterWrite { get; set; }
        public bool CaseChangedHashAfterWrite { get; set; }
        public bool CreateFileInsteadOfDirectory { get; set; }
        public string HookJson { get; set; } = "{\"ok\":true,\"verification\":{\"ok\":true}}";
        public Func<string, string?>? ListingOverride { get; set; }

        public Cloud()
        {
            Objects["/from"] = Directory();
            Objects["/to"] = Directory();
            Objects[Source] = File();
        }

        public static Item File() => new("file", false, 1, 3, "abcdef123456");
        public static Item Directory() => new("directory", false, 1, 0, "");

        public JsonDocument Move(
            bool unlocked = true, bool confirm = true, bool dryRun = false,
            long id = 7, long magic = 11, string? source = null, string? target = null,
            bool explicitTarget = false)
        {
            var chosenTarget = explicitTarget ? target! : target ?? Target;
            return JsonDocument.Parse(CloudWriteOperations.Move(unlocked, confirm, dryRun, id, magic,
                source ?? Source, chosenTarget, Bindings, List, Detail, () => Execute(mkdir: false)));
        }

        public JsonDocument Mkdir(bool dryRun = false) => JsonDocument.Parse(CloudWriteOperations.Mkdir(
            true, true, dryRun, 7, 11, Target, Bindings, List, Detail, () => Execute(mkdir: true)));

        private string Bindings()
        {
            BindingCalls++;
            return JsonSerializer.Serialize(new
            {
                ok = true,
                sandboxes = new[] { new { id = "7", magic = "11", localPath = LocalRoot, localExists = LocalExists } },
            });
        }

        private string List(string path)
        {
            ListCalls.Add(path);
            var replacement = ListingOverride?.Invoke(path);
            return replacement ?? JsonSerializer.Serialize(new
            {
                ok = true,
                entries = Objects.Keys.Where(key => path == "/"
                    || key.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase)).ToArray(),
            });
        }

        private string Detail(string path)
        {
            var pair = Objects.FirstOrDefault(pair => string.Equals(pair.Key, path, StringComparison.OrdinalIgnoreCase));
            if (pair.Value is null) return "{\"ok\":false,\"reason\":\"not found\"}";
            return JsonSerializer.Serialize(new
            {
                ok = true, cloudPath = pair.Key, objectType = pair.Value.Type,
                deleted = pair.Value.Deleted.ToString().ToLowerInvariant(),
                version = pair.Value.Version.ToString(), sizeBytes = pair.Value.Size.ToString(), hash = pair.Value.Hash,
            });
        }

        private string Execute(bool mkdir)
        {
            WriteCalls++;
            if (ApplyWrite)
            {
                if (mkdir) Objects[Target] = CreateFileInsteadOfDirectory ? File() : Directory();
                else
                {
                    var source = Objects[Source];
                    Objects.Remove(Source);
                    Objects[Target] = source with { Version = source.Version + 1,
                        Hash = CorruptTargetAfterWrite ? "different" : CaseChangedHashAfterWrite ? source.Hash.ToUpperInvariant() : source.Hash };
                }
            }
            if (ThrowOnWrite) throw new InvalidOperationException("simulated response failure");
            return HookJson;
        }
    }
}
