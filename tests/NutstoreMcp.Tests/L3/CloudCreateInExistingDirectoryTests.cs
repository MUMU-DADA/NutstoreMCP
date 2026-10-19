using System.Text;
using System.Text.Json;
using NutstoreMcp.Plugin.L3;

namespace NutstoreMcp.Tests;

/// <summary>
/// P1「已有目录里创建文件」的守卫测试。要证明的是三层约束都在：
/// <list type="number">
/// <item>两端预检：云端父目录 + 本地绑定文件夹，任一大小写等价即拒绝；</item>
/// <item>写后第一重：父目录恰好多出目标，其余成员版本/哈希不变；</item>
/// <item>写后第二/三重：目标大小与云端 hash、整文件回读字节的 SHA-256。</item>
/// </list>
/// 用可变云端 + 可注入的本地枚举验证，不接触真实客户端、不产生任何流量。
/// </summary>
public class CloudCreateInExistingDirectoryTests
{
    [Fact]
    public void 已有目录创建成功并完成三重复查()
    {
        var cloud = new Fake();
        using var result = cloud.Create();

        Assert.True(Flag(result, "allowed"));
        Assert.True(Flag(result, "executed"));
        Assert.Equal(1, cloud.FileWrites);
        Assert.Equal("/dir/new.txt", cloud.CreatedFile);
        Assert.Equal("hello world", Encoding.UTF8.GetString(cloud.Objects["/dir/new.txt"].Content!));

        var probe = result.RootElement.GetProperty("probe");
        Assert.True(probe.GetProperty("existingDirectory").GetBoolean());
        Assert.Equal(1, probe.GetProperty("parentMembersBefore").GetInt32());

        var verification = result.RootElement.GetProperty("verification");
        Assert.True(verification.GetProperty("ok").GetBoolean());
        Assert.True(verification.GetProperty("membersUnchanged").GetBoolean());
        Assert.True(verification.GetProperty("readBackMatches").GetBoolean());
        Assert.Equal(1, verification.GetProperty("memberCountAfter").GetInt32());
        Assert.Empty(verification.GetProperty("errors").EnumerateArray());
    }

    [Fact]
    public void 云端已有同名或大小写别名时拒绝()
    {
        foreach (var existing in new[] { "new.txt", "NEW.TXT", "New.Txt" })
        {
            var cloud = new Fake();
            cloud.Objects["/dir/" + existing] = Item.Of("existing");
            using var result = cloud.Create();

            Assert.False(Flag(result, "allowed"));
            Assert.Equal(0, cloud.FileWrites);
            Assert.Contains(result.RootElement.GetProperty("refusals").EnumerateArray(),
                error => error.GetString()!.Contains("目标已存在"));
        }
    }

    [Fact]
    public void 本地已有同名或大小写别名时拒绝()
    {
        var cloud = new Fake
        {
            LocalProbe = new LocalDirectoryProbe.Result(true, new[] { "NEW.TXT" }, null),
        };
        using var result = cloud.Create();

        Assert.False(Flag(result, "allowed"));
        Assert.Equal(0, cloud.FileWrites);
        Assert.Contains(result.RootElement.GetProperty("refusals").EnumerateArray(),
            error => error.GetString()!.Contains("本地绑定文件夹里已有同名或大小写别名"));
    }

    [Fact]
    public void 本地父路径大小写不一致时拒绝()
    {
        var cloud = new Fake
        {
            LocalProbe = LocalDirectoryProbe.Result.Fail("本地绑定目录里的路径段大小写与云端不一致：C:\\sync-root\\Dir"),
        };
        using var result = cloud.Create();

        Assert.False(Flag(result, "allowed"));
        Assert.Equal(0, cloud.FileWrites);
        Assert.Contains(result.RootElement.GetProperty("refusals").EnumerateArray(),
            error => error.GetString()!.Contains("大小写"));
    }

    [Fact]
    public void 父目录不存在或不是目录时拒绝()
    {
        foreach (var corrupt in new Action<Fake>[]
                 {
                     cloud => cloud.Objects.Remove("/dir"),
                     cloud => cloud.Objects["/dir"] = Item.Of("not-a-directory"),
                 })
        {
            var cloud = new Fake();
            corrupt(cloud);
            using var result = cloud.Create();

            Assert.False(Flag(result, "allowed"));
            Assert.Equal(0, cloud.FileWrites);
        }
    }

    [Fact]
    public void 父目录成员超过上限时拒绝()
    {
        var cloud = new Fake();
        for (var i = 0; i <= CloudWriteOperations.MaxExistingDirectoryMembers; i++)
        {
            cloud.Objects["/dir/item-" + i.ToString("D3") + ".txt"] = Item.Of("x");
        }

        using var result = cloud.Create();

        Assert.False(Flag(result, "allowed"));
        Assert.Equal(0, cloud.FileWrites);
        Assert.Contains(result.RootElement.GetProperty("refusals").EnumerateArray(),
            error => error.GetString()!.Contains("上限"));
    }

    [Fact]
    public void 写后父目录多出别的成员不能报告成功()
    {
        var cloud = new Fake
        {
            MutationAfterWrite = fake => fake.Objects["/dir/side-effect.txt"] = Item.Of("extra"),
        };
        using var result = cloud.Create();

        Assert.Equal(1, cloud.FileWrites);
        Assert.False(Flag(result, "executed"));
        Assert.False(result.RootElement.GetProperty("verification").GetProperty("membersUnchanged").GetBoolean());
    }

    [Fact]
    public void 写后既有成员版本变化不能报告成功()
    {
        var cloud = new Fake
        {
            MutationAfterWrite = fake => fake.Objects["/dir/existing.txt"] =
                fake.Objects["/dir/existing.txt"] with { Version = 2 },
        };
        using var result = cloud.Create();

        Assert.Equal(1, cloud.FileWrites);
        Assert.False(Flag(result, "executed"));
        Assert.False(result.RootElement.GetProperty("verification").GetProperty("membersUnchanged").GetBoolean());
        Assert.Contains(result.RootElement.GetProperty("verification").GetProperty("errors").EnumerateArray(),
            error => error.GetString()!.Contains("既有成员"));
    }

    [Fact]
    public void 写后既有成员被删除不能报告成功()
    {
        var cloud = new Fake
        {
            MutationAfterWrite = fake => fake.Objects.Remove("/dir/existing.txt"),
        };
        using var result = cloud.Create();

        Assert.False(Flag(result, "executed"));
        Assert.False(result.RootElement.GetProperty("verification").GetProperty("membersUnchanged").GetBoolean());
    }

    [Fact]
    public void 写后目标云端hash不符不能报告成功()
    {
        var cloud = new Fake
        {
            HookJsonOverride = JsonSerializer.Serialize(new
            {
                ok = true,
                cloudHash = "not-the-hash",
                verification = new { ok = true },
            }),
        };
        using var result = cloud.Create();

        Assert.Equal(1, cloud.FileWrites);
        Assert.False(Flag(result, "executed"));
        Assert.False(result.RootElement.GetProperty("verification").GetProperty("contentMatches").GetBoolean());
    }

    [Fact]
    public void 写后回读字节不符不能报告成功()
    {
        var cloud = new Fake { CorruptContentAfterWrite = true };
        using var result = cloud.Create();

        Assert.Equal(1, cloud.FileWrites);
        Assert.False(Flag(result, "executed"));
        Assert.False(result.RootElement.GetProperty("verification").GetProperty("readBackMatches").GetBoolean());
        Assert.Contains(result.RootElement.GetProperty("verification").GetProperty("errors").EnumerateArray(),
            error => error.GetString()!.Contains("回读"));
    }

    [Fact]
    public void 回读分段失败不能报告成功()
    {
        var cloud = new Fake
        {
            ReadInlineOverride = (_, _, _) => "{\"ok\":false,\"reason\":\"read failed\"}",
        };
        using var result = cloud.Create();

        Assert.False(Flag(result, "executed"));
        Assert.Contains(result.RootElement.GetProperty("verification").GetProperty("errors").EnumerateArray(),
            error => error.GetString()!.Contains("回读分段"));
    }

    [Fact]
    public void 预演只读不写并给出快照计数()
    {
        var cloud = new Fake();
        using var result = cloud.Create(dryRun: true);

        Assert.True(Flag(result, "allowed"));
        Assert.False(Flag(result, "attempted"));
        Assert.Equal(0, cloud.FileWrites);
        Assert.Equal(1, result.RootElement.GetProperty("probe").GetProperty("parentMembersBefore").GetInt32());
        Assert.False(result.RootElement.GetProperty("verification").GetProperty("checkedState").GetBoolean());
    }

    [Fact]
    public void 本地目录尚未同步时允许创建()
    {
        var cloud = new Fake { LocalProbe = LocalDirectoryProbe.Result.Absent() };
        using var result = cloud.Create();

        Assert.True(Flag(result, "executed"));
        Assert.False(result.RootElement.GetProperty("probe").GetProperty("localSnapshotExists").GetBoolean());
    }

    [Fact]
    public void 根目录也可以创建且会核对根下其它成员()
    {
        var cloud = new Fake();
        using var result = cloud.Create(directory: "/");

        Assert.True(Flag(result, "executed"));
        Assert.Equal("/new.txt", cloud.CreatedFile);
        // 根下原本只有 /dir 一个成员，写后应当仍是 1 个既有成员不变。
        Assert.Equal(1, result.RootElement.GetProperty("verification").GetProperty("memberCountBefore").GetInt32());
        Assert.True(result.RootElement.GetProperty("verification").GetProperty("membersUnchanged").GetBoolean());
    }

    [Fact]
    public void 根路径详情被客户端拒绝也不影响根下创建()
    {
        // 客户端对根路径的详情查询直接报 IllegalArgument（You cannot query the full info
        // of root path）。根一定存在且是目录，所以插件跳过详情、仍按列表检查目标冲突。
        var cloud = new Fake { FailDetailForRoot = true };
        using var result = cloud.Create(directory: "/");

        Assert.True(Flag(result, "executed"));
        Assert.True(result.RootElement.GetProperty("verification").GetProperty("membersUnchanged").GetBoolean());
    }

    [Fact]
    public void 内容超限或负长度在预检前被拒绝()
    {
        var cloud = new Fake();
        using var tooLarge = cloud.Create(content: "12345", maxContentBytes: 4);
        Assert.False(Flag(tooLarge, "allowed"));
        Assert.Equal(0, cloud.FileWrites);

        var negative = new Fake();
        using (var result = negative.Create(contentLengthOverride: -1))
        {
            Assert.False(Flag(result, "allowed"));
            Assert.Equal(0, negative.FileWrites);
        }
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
        Assert.Equal(0, cloud.FileWrites);
    }

    [Fact]
    public void 绑定不是真实本地路径时拒绝()
    {
        var cloud = new Fake { LocalExists = false };
        using var result = cloud.Create();

        Assert.False(Flag(result, "allowed"));
        Assert.Equal(0, cloud.FileWrites);
    }

    private static bool Flag(JsonDocument result, string name) => result.RootElement.GetProperty(name).GetBoolean();

    private sealed record Item(string Type, bool Deleted, long Version, long Size, string Hash, byte[]? Content)
    {
        public static Item Dir(long version = 1) => new("directory", false, version, 0, string.Empty, null);

        public static Item New(byte[] content, long version = 1) => new(
            "file", false, version, content.Length,
            content.Length == 0 ? CloudContentCodec.EmptyFileHashBase64 : CloudContentCodec.CloudHash(content),
            content);

        public static Item Of(string text, long version = 1)
            => New(Encoding.UTF8.GetBytes(text), version);
    }

    private sealed class Fake
    {
        public const string RootPath = "C:\\sync-root";

        public Fake()
        {
            Objects["/"] = Item.Dir();
            Objects["/dir"] = Item.Dir();
            Objects["/dir/existing.txt"] = Item.Of("old");
        }

        public string LocalRoot { get; set; } = RootPath;
        public bool LocalExists { get; set; } = true;
        public LocalDirectoryProbe.Result LocalProbe { get; set; } = new(true, Array.Empty<string>(), null);
        public int BindingCalls { get; private set; }
        public int FileWrites { get; private set; }
        public string? CreatedFile { get; private set; }
        public string? HookJsonOverride { get; set; }
        public Action<Fake>? MutationAfterWrite { get; set; }
        public bool CorruptContentAfterWrite { get; set; }
        public bool FailDetailForRoot { get; set; }
        public Func<string, long, long, string>? ReadInlineOverride { get; set; }
        public Dictionary<string, Item> Objects { get; } = new(StringComparer.Ordinal);
        public List<string> ListCalls { get; } = new();

        private string _directory = "/dir";
        private string _file = "new.txt";
        private byte[] _content = Encoding.UTF8.GetBytes("hello world");

        public JsonDocument Create(
            bool unlocked = true, bool confirm = true, bool dryRun = false,
            string directory = "/dir", string file = "new.txt", string content = "hello world",
            int? contentLengthOverride = null, int? maxContentBytes = null)
        {
            _directory = directory;
            _file = file;
            _content = Encoding.UTF8.GetBytes(content);
            var contentLength = contentLengthOverride ?? _content.Length;

            return JsonDocument.Parse(CloudWriteOperations.CreateFileInExistingDirectory(
                unlocked, confirm, dryRun, 7, 11, directory, file, contentLength,
                Bindings, List, Detail,
                (_, _) => LocalProbe,
                CreateFile,
                ReadInline,
                () => CloudContentCodec.Sha256Hex(_content),
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
            var entries = Objects.Keys
                .Where(key => ParentOf(key) == path)
                .ToArray();
            return JsonSerializer.Serialize(new { ok = true, entries });
        }

        private string Detail(string path)
        {
            if (FailDetailForRoot && path == "/")
            {
                return "{\"ok\":false,\"reason\":\"You cannot query the full info of root path\"}";
            }

            if (!Objects.TryGetValue(path, out var item))
            {
                return "{\"ok\":false,\"reason\":\"not found\"}";
            }

            return JsonSerializer.Serialize(new
            {
                ok = true,
                cloudPath = path,
                objectType = item.Type,
                deleted = item.Deleted.ToString().ToLowerInvariant(),
                version = item.Version.ToString(),
                sizeBytes = item.Size.ToString(),
                hash = item.Hash,
            });
        }

        private string CreateFile()
        {
            FileWrites++;
            var target = _directory == "/" ? "/" + _file : _directory + "/" + _file;
            CreatedFile = target;

            var written = Item.New(_content, 1);
            if (CorruptContentAfterWrite)
            {
                var corrupted = (byte[])written.Content!.Clone();
                corrupted[corrupted.Length - 1] ^= 0xFF;
                written = written with { Content = corrupted };
            }

            Objects[target] = written;
            MutationAfterWrite?.Invoke(this);

            if (HookJsonOverride is not null) return HookJsonOverride;

            var hookHash = Item.New(_content, 1).Hash;
            return JsonSerializer.Serialize(new { ok = true, cloudHash = hookHash, verification = new { ok = true } });
        }

        private string ReadInline(string path, long offset, long length)
        {
            if (ReadInlineOverride is not null) return ReadInlineOverride(path, offset, length);

            if (!Objects.TryGetValue(path, out var item) || item.Content is null)
            {
                return "{\"ok\":false,\"reason\":\"not found\"}";
            }

            var total = item.Content.LongLength;
            var start = (int)Math.Min(offset, total);
            var count = (int)Math.Min(length, total - start);
            var slice = item.Content.AsSpan(start, count).ToArray();

            var bom = slice.Length >= 3 && slice[0] == 0xEF && slice[1] == 0xBB && slice[2] == 0xBF;
            if (CloudContentCodec.TryDecodeUtf8(bom ? slice[3..] : slice, out var text))
            {
                return JsonSerializer.Serialize(new
                {
                    ok = true, offset, length = count, totalSize = total, text, bom,
                    contentBase64 = (string?)null,
                });
            }

            return JsonSerializer.Serialize(new
            {
                ok = true, offset, length = count, totalSize = total, text = (string?)null, bom = false,
                contentBase64 = Convert.ToBase64String(slice),
            });
        }

        private static string ParentOf(string path)
        {
            if (path == "/") return string.Empty;
            var slash = path.LastIndexOf('/');
            return slash <= 0 ? "/" : path.Substring(0, slash);
        }
    }
}

/// <summary>
/// <see cref="LocalDirectoryProbe"/> 的本地枚举测试。核心是两条：
/// 父路径逐段大小写一致，以及「尚未同步到本地」不能被误判成拒绝。
/// </summary>
public sealed class LocalDirectoryProbeTests : IDisposable
{
    private readonly string _temp = Path.Combine(
        AppContext.BaseDirectory, "probe-temp-" + Guid.NewGuid().ToString("N").Substring(0, 8));

    public LocalDirectoryProbeTests()
    {
        Directory.CreateDirectory(Path.Combine(_temp, "dir"));
        File.WriteAllText(Path.Combine(_temp, "dir", "a.txt"), "x");
        File.WriteAllText(Path.Combine(_temp, "dirfile"), "x");
    }

    public void Dispose()
    {
        try { Directory.Delete(_temp, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void 正常枚举给出父目录条目()
    {
        var result = LocalDirectoryProbe.Inspect(_temp, "/dir");

        Assert.True(result.PathExists);
        Assert.Null(result.Error);
        Assert.Contains("a.txt", result.Entries);
    }

    [Fact]
    public void 根目录枚举的是绑定根本身()
    {
        var result = LocalDirectoryProbe.Inspect(_temp, "/");

        Assert.True(result.PathExists);
        Assert.Null(result.Error);
        Assert.Contains("dir", result.Entries);
    }

    [Fact]
    public void 路径段大小写不一致时拒绝()
    {
        var result = LocalDirectoryProbe.Inspect(_temp, "/DIR");

        Assert.NotNull(result.Error);
        Assert.Contains("大小写", result.Error);
    }

    [Fact]
    public void 尚未同步到本地时不算错误()
    {
        var result = LocalDirectoryProbe.Inspect(_temp, "/not-synced-yet");

        Assert.False(result.PathExists);
        Assert.Null(result.Error);
        Assert.Empty(result.Entries);
    }

    [Fact]
    public void 本地同路径是文件时拒绝()
    {
        var result = LocalDirectoryProbe.Inspect(_temp, "/dirfile/sub");

        Assert.NotNull(result.Error);
        Assert.Contains("是文件而不是目录", result.Error);
    }

    [Fact]
    public void 大小写碰撞检测只看名字不区分大小写()
    {
        Assert.Equal("NEW.TXT", LocalDirectoryProbe.FindCaseInsensitiveCollision(new[] { "a.txt", "NEW.TXT" }, "new.txt"));
        Assert.Null(LocalDirectoryProbe.FindCaseInsensitiveCollision(new[] { "a.txt", "other.txt" }, "new.txt"));
    }
}
