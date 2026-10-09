using System.Text.Json;
using NutstoreMcp.Plugin.L3;

namespace NutstoreMcp.Tests;

/// <summary>
/// B2/C3 受限创建的守卫测试。核心要证明的是**「父目录非空就绝不创建文件」**：
/// 客户端 cf02 本体没有查重与 noReplace，防覆写全靠这条结构保证。
/// 用可变云端模型验证，不接触真实客户端、不产生任何流量。
/// </summary>
public class CloudCreateOperationsTests
{
    private const string Root = "C:\\new-sync";

    [Fact]
    public void 预演只读不写且probe显示预检通过()
    {
        var cloud = new Fake();
        using var result = cloud.Create(dryRun: true);

        Assert.True(Flag(result, "allowed"));
        Assert.False(Flag(result, "attempted"));
        Assert.False(Flag(result, "executed"));
        Assert.True(result.RootElement.GetProperty("probe").GetProperty("preflightOk").GetBoolean());
        Assert.Equal(0, cloud.DirectoryWrites);
        Assert.Equal(0, cloud.FileWrites);
        Assert.Contains("/", cloud.ListCalls);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void 未解锁或未确认直接拒绝且不调用任何委托(bool unlocked, bool confirm)
    {
        var cloud = new Fake();
        using var result = cloud.Create(unlocked: unlocked, confirm: confirm);

        Assert.False(Flag(result, "allowed"));
        Assert.Equal(0, cloud.BindingCalls);
        Assert.Empty(cloud.ListCalls);
        Assert.Equal(0, cloud.DirectoryWrites);
        Assert.Equal(0, cloud.FileWrites);
    }

    [Theory]
    [InlineData("a/b", "b.txt")]        // 目录名不能是多段
    [InlineData("..", "b.txt")]
    [InlineData(".", "b.txt")]
    [InlineData("a\\b", "b.txt")]
    [InlineData("a:b", "b.txt")]
    [InlineData("a?b", "b.txt")]
    [InlineData(" dir", "b.txt")]
    [InlineData("dir ", "b.txt")]
    [InlineData("dir.", "b.txt")]
    [InlineData("CON", "b.txt")]
    [InlineData("lpt9", "b.txt")]
    [InlineData("dir", "a/b")]
    [InlineData("dir", "..")]
    [InlineData("dir", "b:c")]
    [InlineData("dir", "nul")]
    [InlineData("dir", "b.")]
    public void 目录名与文件名必须是单个合法路径段(string directory, string file)
    {
        var cloud = new Fake();
        using var result = cloud.Create(directory: directory, file: file);

        Assert.False(Flag(result, "allowed"));
        Assert.Equal(0, cloud.BindingCalls);
        Assert.Equal(0, cloud.DirectoryWrites);
        Assert.Equal(0, cloud.FileWrites);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(CloudWriteOperations.MaxContentBytes + 1)]
    public void 空内容与超出上限的内容都被拒绝(int length)
    {
        var cloud = new Fake();
        using var result = cloud.Create(contentLength: length);

        Assert.False(Flag(result, "allowed"));
        Assert.Equal(0, cloud.DirectoryWrites);
        Assert.Equal(0, cloud.FileWrites);
    }

    [Fact]
    public void 根下已有同名或大小写别名时拒绝创建()
    {
        foreach (var existing in new[] { "/dir", "/DIR" })
        {
            var cloud = new Fake();
            cloud.Objects[existing] = Fake.Directory();
            using var result = cloud.Create();

            Assert.False(Flag(result, "allowed"));
            Assert.Equal(0, cloud.DirectoryWrites);
            Assert.Equal(0, cloud.FileWrites);
        }
    }

    [Fact]
    public void 父目录非空时绝不创建文件()
    {
        var cloud = new Fake();
        // 目录建出来后里面已经有别的东西（并发或客户端自带内容），此时必须停手。
        cloud.InjectIntoNewDirectory = "/dir/existing.txt";
        using var result = cloud.Create();

        Assert.Equal(1, cloud.DirectoryWrites);
        Assert.Equal(0, cloud.FileWrites);
        Assert.False(Flag(result, "executed"));
        Assert.False(result.RootElement.GetProperty("probe").GetProperty("directoryEmpty").GetBoolean());
        Assert.Contains(result.RootElement.GetProperty("verification").GetProperty("errors").EnumerateArray(),
            error => error.GetString()!.Contains("不是空的"));
    }

    [Fact]
    public void 新目录为空时创建成功且写后核对通过()
    {
        var cloud = new Fake();
        using var result = cloud.Create();

        Assert.True(Flag(result, "allowed"));
        Assert.True(Flag(result, "attempted"));
        Assert.True(Flag(result, "executed"));
        Assert.Equal(1, cloud.DirectoryWrites);
        Assert.Equal(1, cloud.FileWrites);
        Assert.Equal("/dir", cloud.CreatedDirectory);
        Assert.Equal("/dir/a.txt", cloud.CreatedFile);

        var verification = result.RootElement.GetProperty("verification");
        Assert.True(verification.GetProperty("ok").GetBoolean());
        Assert.True(verification.GetProperty("targetPresent").GetBoolean());
        Assert.True(verification.GetProperty("contentMatches").GetBoolean());
    }

    [Fact]
    public void 目录建不出来就不创建文件()
    {
        var cloud = new Fake { DirectoryHookJson = "{\"ok\":false,\"reason\":\"duplicate\"}" };
        using var result = cloud.Create();

        Assert.Equal(1, cloud.DirectoryWrites);
        Assert.Equal(0, cloud.FileWrites);
        Assert.False(Flag(result, "executed"));
    }

    [Fact]
    public void 目录建了但默认域没确认存在则停手()
    {
        var cloud = new Fake { DirectoryHookJson = "{\"ok\":true}" };   // 缺 verification
        using var result = cloud.Create();

        Assert.Equal(0, cloud.FileWrites);
        Assert.False(Flag(result, "executed"));
    }

    [Fact]
    public void 文件写完但详情不匹配不能报告成功()
    {
        var cloud = new Fake { CorruptFileAfterWrite = true };
        using var result = cloud.Create();

        Assert.Equal(1, cloud.FileWrites);
        Assert.False(Flag(result, "executed"));
        Assert.False(result.RootElement.GetProperty("verification").GetProperty("contentMatches").GetBoolean());
    }

    [Fact]
    public void 默认域乐观成功无法绕过插件写后复查()
    {
        var cloud = new Fake { WriteFileToCloud = false };   // 钩子说成功，云端其实没有
        using var result = cloud.Create();

        Assert.False(Flag(result, "executed"));
        Assert.False(result.RootElement.GetProperty("verification").GetProperty("targetPresent").GetBoolean());
        Assert.Equal(1, cloud.FileWrites);
    }

    [Fact]
    public void 写入抛异常后仍复查且不自动重试()
    {
        var cloud = new Fake { ThrowOnFileWrite = true };
        using var result = cloud.Create();

        Assert.Equal(1, cloud.FileWrites);      // 只尝试一次
        Assert.False(Flag(result, "executed"));
    }

    [Fact]
    public void 绑定缺失或不是真实本地路径时拒绝()
    {
        foreach (var exists in new[] { false, true })
        {
            var cloud = new Fake { LocalExists = exists, LocalRoot = exists ? "relative\\path" : Root };
            using var result = cloud.Create();

            Assert.False(Flag(result, "allowed"));
            Assert.Equal(0, cloud.DirectoryWrites);
            Assert.Equal(0, cloud.FileWrites);
        }
    }

    private static bool Flag(JsonDocument result, string name) => result.RootElement.GetProperty(name).GetBoolean();

    private sealed record Item(string Type, bool Deleted, long Version, long Size, string Hash);

    private sealed class Fake
    {
        public string LocalRoot { get; set; } = Root;
        public bool LocalExists { get; set; } = true;
        public int BindingCalls { get; private set; }
        public int DirectoryWrites { get; private set; }
        public int FileWrites { get; private set; }
        public string? CreatedDirectory { get; private set; }
        public string? CreatedFile { get; private set; }
        public string? InjectIntoNewDirectory { get; set; }
        public bool ApplyDirectoryWrite { get; set; } = true;
        public bool ApplyFileWrite { get; set; } = true;
        public bool WriteFileToCloud { get; set; } = true;
        public bool ThrowOnFileWrite { get; set; }
        public bool CorruptFileAfterWrite { get; set; }
        public string DirectoryHookJson { get; set; } = "{\"ok\":true,\"verification\":{\"ok\":true}}";
        public string CloudHash { get; set; } = "hash-abc";
        public List<string> ListCalls { get; } = new();
        public Dictionary<string, Item> Objects { get; } = new(StringComparer.Ordinal) { ["/"] = Directory() };

        public static Item File(long size, string hash) => new("file", false, 1, size, hash);
        public static Item Directory() => new("directory", false, 1, 0, "");

        public JsonDocument Create(
            bool unlocked = true, bool confirm = true, bool dryRun = false,
            string directory = "dir", string file = "a.txt", int contentLength = 12)
        {
            _directory = directory;
            _file = file;
            _contentLength = contentLength;
            return JsonDocument.Parse(CloudWriteOperations.CreateFile(
                unlocked, confirm, dryRun, 7, 11, directory, file, contentLength,
                Bindings, List, Detail, CreateDirectory, CreateFile));
        }

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
            return JsonSerializer.Serialize(new
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

        private string CreateDirectory()
        {
            DirectoryWrites++;
            var target = "/" + _directory;
            CreatedDirectory = target;
            if (ApplyDirectoryWrite)
            {
                Objects[target] = Directory();
                if (InjectIntoNewDirectory is not null)
                    Objects[InjectIntoNewDirectory] = File(3, "other-hash");
            }
            return DirectoryHookJson;
        }

        private string CreateFile()
        {
            FileWrites++;
            var target = "/" + _directory + "/" + _file;
            CreatedFile = target;
            if (ApplyFileWrite && WriteFileToCloud)
                Objects[target] = File(_contentLength, CorruptFileAfterWrite ? "different-hash" : CloudHash);
            if (ThrowOnFileWrite) throw new InvalidOperationException("simulated response failure");
            return JsonSerializer.Serialize(new { ok = true, cloudHash = CloudHash, verification = new { ok = true } });
        }

        // Create 的入参留一份给两个委托（真实调用点也是闭包捕获）。
        private string _directory = "dir";
        private string _file = "a.txt";
        private int _contentLength = 12;
    }
}
