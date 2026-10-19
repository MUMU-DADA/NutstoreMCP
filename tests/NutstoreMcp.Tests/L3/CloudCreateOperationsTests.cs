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
    public void 本地文件模式可以把内容上限提高到16MiB()
    {
        // 300000 字节超过内联 base64 的 256 KiB 上限，但低于全量上传阈值附近；
        // 本地文件模式应能把上限提到 CloudContentCodec.MaxFileUploadBytes。
        var cloud = new Fake();
        using var result = cloud.Create(contentLength: 300000,
            maxContentBytes: CloudContentCodec.MaxFileUploadBytes);

        Assert.True(Flag(result, "allowed"));
        Assert.True(Flag(result, "executed"));
        Assert.Equal(300000, cloud.Objects["/dir/a.txt"].Size);
        Assert.Equal(1, cloud.FileWrites);
    }

    [Fact]
    public void 超出16MiB的本地文件仍被拒绝()
    {
        var cloud = new Fake();
        using var result = cloud.Create(contentLength: CloudContentCodec.MaxFileUploadBytes + 1,
            maxContentBytes: CloudContentCodec.MaxFileUploadBytes);

        Assert.False(Flag(result, "allowed"));
        Assert.Equal(0, cloud.FileWrites);
        Assert.Contains(result.RootElement.GetProperty("refusals").EnumerateArray(),
            error => error.GetString()!.Contains("上限"));
    }

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
    [InlineData(-1)]
    [InlineData(CloudWriteOperations.MaxContentBytes + 1)]
    public void 负长度与超出上限的内容都被拒绝(int length)
    {
        var cloud = new Fake();
        using var result = cloud.Create(contentLength: length);

        Assert.False(Flag(result, "allowed"));
        Assert.Equal(0, cloud.DirectoryWrites);
        Assert.Equal(0, cloud.FileWrites);
    }

    [Fact]
    public void 空内容按空文件分支创建且写后核对大小为零()
    {
        // 客户端空文件是特殊分支：跳过内容上传、云端 hash 为 32 个零字节。
        // 插件这一侧只按「内容长度 0」把它如实传下去，写后照样核对大小与 hash。
        var cloud = new Fake { CloudHash = CloudContentCodec.EmptyFileHashBase64 };
        using var result = cloud.Create(contentLength: 0);

        Assert.True(Flag(result, "allowed"));
        Assert.True(Flag(result, "attempted"));
        Assert.True(Flag(result, "executed"));
        Assert.Equal(0, cloud.Objects["/dir/a.txt"].Size);
        Assert.True(result.RootElement.GetProperty("probe").GetProperty("emptyContent").GetBoolean());
        Assert.True(result.RootElement.GetProperty("verification").GetProperty("contentMatches").GetBoolean());
    }

    [Fact]
    public void 空文件写后哈希不符不能报告成功()
    {
        var cloud = new Fake
        {
            CloudHash = CloudContentCodec.EmptyFileHashBase64,
            CorruptFileAfterWrite = true,
        };
        using var result = cloud.Create(contentLength: 0);

        Assert.False(Flag(result, "executed"));
        Assert.False(result.RootElement.GetProperty("verification").GetProperty("contentMatches").GetBoolean());
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

    [Fact]
    public void 写后目录列表失败不能报告成功()
    {
        // 列表读不到就无法推断目标唯一存在；即使默认域说成功也不许报 executed。
        var cloud = new Fake();
        cloud.ListingOverride = path => path == "/dir" && cloud.FileWrites > 0
            ? "{\"ok\":false,\"reason\":\"listing failed\"}"
            : null;
        using var result = cloud.Create();

        Assert.Equal(1, cloud.FileWrites);
        Assert.False(Flag(result, "executed"));
        Assert.False(result.RootElement.GetProperty("verification").GetProperty("ok").GetBoolean());
    }

    [Fact]
    public void 写后出现大小写别名副本不能报告成功()
    {
        // 目标旁边多出一个仅大小写不同的副本时命中数不是 1 —— 这正是要拒绝的形态。
        var cloud = new Fake { AliasAfterWrite = "/dir/A.TXT" };
        using var result = cloud.Create();

        Assert.Equal(1, cloud.FileWrites);
        Assert.False(Flag(result, "executed"));
        Assert.False(result.RootElement.GetProperty("verification").GetProperty("targetPresent").GetBoolean());
        Assert.Contains(result.RootElement.GetProperty("verification").GetProperty("errors").EnumerateArray(),
            error => error.GetString()!.Contains("唯一出现"));
    }

    [Fact]
    public void 编码长度检查在解码之前挡住超大输入()
    {
        // 上限：262144 字节 → ceil(n/3)*4 = 349528 个 base64 字符
        Assert.Equal(349528, CloudWriteOperations.MaxEncodedLength);
        Assert.Null(CloudWriteOperations.ValidateEncodedLength(0));
        Assert.Null(CloudWriteOperations.ValidateEncodedLength(CloudWriteOperations.MaxEncodedLength));
        Assert.NotNull(CloudWriteOperations.ValidateEncodedLength(CloudWriteOperations.MaxEncodedLength + 1));
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
        public string? AliasAfterWrite { get; set; }
        public Func<string, string?>? ListingOverride { get; set; }
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
            string directory = "dir", string file = "a.txt", int contentLength = 12,
            int? maxContentBytes = null)
        {
            _directory = directory;
            _file = file;
            _contentLength = contentLength;
            return JsonDocument.Parse(CloudWriteOperations.CreateFile(
                unlocked, confirm, dryRun, 7, 11, directory, file, contentLength,
                Bindings, List, Detail, CreateDirectory, CreateFile,
                maxContentBytes: maxContentBytes ?? CloudWriteOperations.MaxContentBytes));
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
            var replacement = ListingOverride?.Invoke(path);
            if (replacement is not null) return replacement;
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
            if (AliasAfterWrite is not null) Objects[AliasAfterWrite] = File(_contentLength, "alias-hash");
            if (ThrowOnFileWrite) throw new InvalidOperationException("simulated response failure");
            return JsonSerializer.Serialize(new { ok = true, cloudHash = CloudHash, verification = new { ok = true } });
        }

        // Create 的入参留一份给两个委托（真实调用点也是闭包捕获）。
        private string _directory = "dir";
        private string _file = "a.txt";
        private int _contentLength = 12;
    }
}
