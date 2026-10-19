using System.Text;
using System.Text.Json;
using NutstoreMcp.Plugin.L3;

namespace NutstoreMcp.Tests;

/// <summary>
/// P2 台账限定删除的守卫测试。核心要证明的是**删除范围只有台账里、且状态完全一致的对象**：
/// 台账没有的路径、被修改过的对象、目录、大小写别名一律拒绝；失败只报告、不重试不恢复。
/// </summary>
public sealed class CreatedObjectDeleteTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        AppContext.BaseDirectory, "delete-temp-" + Guid.NewGuid().ToString("N").Substring(0, 8));

    public CreatedObjectDeleteTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string LedgerPath => Path.Combine(_dir, "created-objects.jsonl");

    [Fact]
    public void 台账匹配且状态一致时删除成功并写删除记录()
    {
        var fake = new Fake();
        fake.Seed("/dir/new.txt", "hello");
        fake.SeedLedgerFor(LedgerPath, "/dir/new.txt");

        using var result = fake.Delete(LedgerPath);

        Assert.True(Flag(result, "allowed"));
        Assert.True(Flag(result, "attempted"));
        Assert.True(Flag(result, "executed"));
        Assert.False(fake.Objects.ContainsKey("/dir/new.txt"));

        var verification = result.RootElement.GetProperty("verification");
        Assert.True(verification.GetProperty("ok").GetBoolean());
        Assert.False(verification.GetProperty("targetPresent").GetBoolean());
        Assert.True(verification.GetProperty("tombstoneWritten").GetBoolean());

        var entries = CreatedObjectLedger.ReadAll(LedgerPath);
        Assert.Equal(2, entries.Count);
        Assert.Equal(CreatedObjectLedger.ActionDeleted, entries[^1].Action);
    }

    [Fact]
    public void 台账里没有的路径拒绝删除()
    {
        var fake = new Fake();
        fake.Seed("/dir/user-file.txt", "user data");
        // 故意不登记台账：用户已有对象不能通过这个工具删除
        using var result = fake.Delete(LedgerPath, "/dir/user-file.txt");

        Assert.False(Flag(result, "allowed"));
        Assert.False(Flag(result, "attempted"));
        Assert.True(fake.Objects.ContainsKey("/dir/user-file.txt"));
        Assert.Contains(result.RootElement.GetProperty("refusals").EnumerateArray(),
            error => error.GetString()!.Contains("创建台账"));
    }

    [Fact]
    public void 台账最后一条是删除记录时拒绝重复删除()
    {
        var fake = new Fake();
        fake.Seed("/dir/new.txt", "hello");
        fake.SeedLedgerFor(LedgerPath, "/dir/new.txt");
        Assert.True(CreatedObjectLedger.TryAppend(LedgerPath, new CreatedObjectLedger.Entry(
            DateTimeOffset.Now, fake.SandboxId, "/dir/new.txt", 1, fake.Objects["/dir/new.txt"].Hash,
            fake.Objects["/dir/new.txt"].Size, "nutstore_l3_create_file", "inline-base64",
            CreatedObjectLedger.ActionDeleted)));

        using var result = fake.Delete(LedgerPath);

        Assert.False(Flag(result, "allowed"));
        Assert.True(fake.Objects.ContainsKey("/dir/new.txt"));
    }

    [Fact]
    public void 创建后被修改的对象拒绝删除()
    {
        foreach (var mutate in new Action<Fake>[]
                 {
                     fake => fake.Objects["/dir/new.txt"] = fake.Objects["/dir/new.txt"] with { Version = 2 },
                     fake => fake.Objects["/dir/new.txt"] = fake.Objects["/dir/new.txt"] with { Size = 99 },
                     fake => fake.Objects["/dir/new.txt"] = fake.Objects["/dir/new.txt"] with { Hash = "changed" },
                 })
        {
            var fake = new Fake();
            fake.Seed("/dir/new.txt", "hello");
            fake.SeedLedgerFor(LedgerPath, "/dir/new.txt");
            mutate(fake);

            using var result = fake.Delete(LedgerPath);

            Assert.False(Flag(result, "allowed"));
            Assert.True(fake.Objects.ContainsKey("/dir/new.txt"));
            Assert.Contains(result.RootElement.GetProperty("refusals").EnumerateArray(),
                error => error.GetString()!.Contains("台账不一致"));
        }
    }

    [Fact]
    public void 目录不能被这个工具删除()
    {
        var fake = new Fake();
        fake.Seed("/dir/sub", "ignored");
        fake.Objects["/dir/sub"] = new Item("directory", 1, 0, "");
        fake.SeedLedgerFor(LedgerPath, "/dir/sub");

        using var result = fake.Delete(LedgerPath, "/dir/sub");

        // 目录没有 hash，台账记录本身不合法 → 无论哪条规则先命中都必须拒绝
        Assert.False(Flag(result, "allowed"));
        Assert.True(fake.Objects.ContainsKey("/dir/sub"));
    }

    [Fact]
    public void 台账路径与请求大小写不同时拒绝()
    {
        var fake = new Fake();
        fake.Seed("/dir/New.txt", "hello");
        fake.SeedLedgerFor(LedgerPath, "/dir/New.txt");

        using var result = fake.Delete(LedgerPath, "/dir/new.txt");

        Assert.False(Flag(result, "allowed"));
        Assert.True(fake.Objects.ContainsKey("/dir/New.txt"));
        Assert.Contains(result.RootElement.GetProperty("refusals").EnumerateArray(),
            error => error.GetString()!.Contains("逐字符"));
    }

    [Fact]
    public void 预演只读不删()
    {
        var fake = new Fake();
        fake.Seed("/dir/new.txt", "hello");
        fake.SeedLedgerFor(LedgerPath, "/dir/new.txt");

        using var result = fake.Delete(LedgerPath, dryRun: true);

        Assert.True(Flag(result, "allowed"));
        Assert.False(Flag(result, "attempted"));
        Assert.False(Flag(result, "executed"));
        Assert.True(fake.Objects.ContainsKey("/dir/new.txt"));
        Assert.Single(CreatedObjectLedger.ReadAll(LedgerPath));
    }

    [Fact]
    public void 钩子删除失败时只报告不恢复()
    {
        var fake = new Fake { DeleteHookOverride = "{\"ok\":false,\"reason\":\"server refused\"}" };
        fake.Seed("/dir/new.txt", "hello");
        fake.SeedLedgerFor(LedgerPath, "/dir/new.txt");

        using var result = fake.Delete(LedgerPath);

        Assert.True(Flag(result, "attempted"));
        Assert.False(Flag(result, "executed"));
        Assert.True(fake.Objects.ContainsKey("/dir/new.txt"));
        Assert.Contains(result.RootElement.GetProperty("refusals").EnumerateArray(),
            error => error.GetString()!.Contains("客户端删除失败"));
    }

    [Fact]
    public void 钩子说成功但对象仍在列表里不能报告成功()
    {
        var fake = new Fake { ApplyDelete = false };
        fake.Seed("/dir/new.txt", "hello");
        fake.SeedLedgerFor(LedgerPath, "/dir/new.txt");

        using var result = fake.Delete(LedgerPath);

        Assert.False(Flag(result, "executed"));
        Assert.Contains(result.RootElement.GetProperty("verification").GetProperty("errors").EnumerateArray(),
            error => error.GetString()!.Contains("仍能查到"));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void 未解锁或未确认直接拒绝(bool unlocked, bool confirm)
    {
        var fake = new Fake();
        fake.Seed("/dir/new.txt", "hello");
        fake.SeedLedgerFor(LedgerPath, "/dir/new.txt");

        using var result = fake.Delete(LedgerPath, unlocked: unlocked, confirm: confirm);

        Assert.False(Flag(result, "allowed"));
        Assert.False(Flag(result, "attempted"));
        Assert.True(fake.Objects.ContainsKey("/dir/new.txt"));
        Assert.Empty(fake.DetailCalls);
    }

    [Fact]
    public void 沙箱没有真实本地绑定时拒绝()
    {
        var fake = new Fake { LocalExists = false };
        fake.Seed("/dir/new.txt", "hello");
        fake.SeedLedgerFor(LedgerPath, "/dir/new.txt");

        using var result = fake.Delete(LedgerPath);

        Assert.False(Flag(result, "allowed"));
        Assert.True(fake.Objects.ContainsKey("/dir/new.txt"));
    }

    [Theory]
    [InlineData("/dir/*.txt")]
    [InlineData("/dir/../new.txt")]
    [InlineData("/")]
    [InlineData("relative/new.txt")]
    public void 通配根路径与相对路径一律拒绝(string cloudPath)
    {
        var fake = new Fake();
        fake.Seed("/dir/new.txt", "hello");
        fake.SeedLedgerFor(LedgerPath, "/dir/new.txt");

        using var result = fake.Delete(LedgerPath, cloudPath);

        Assert.False(Flag(result, "allowed"));
        Assert.True(fake.Objects.ContainsKey("/dir/new.txt"));
    }

    [Fact]
    public void 台账写失败不影响删除结果但会标注()
    {
        var fake = new Fake { ThrowOnLedgerWrite = true };
        fake.Seed("/dir/new.txt", "hello");
        fake.SeedLedgerFor(LedgerPath, "/dir/new.txt");

        using var result = fake.Delete(LedgerPath);

        Assert.True(Flag(result, "executed"));
        Assert.False(result.RootElement.GetProperty("verification").GetProperty("tombstoneWritten").GetBoolean());
    }

    private static bool Flag(JsonDocument result, string name) => result.RootElement.GetProperty(name).GetBoolean();

    private sealed record Item(string Type, long Version, long Size, string Hash)
    {
        public bool Deleted => false;
    }

    private sealed class Fake
    {
        public bool Unlocked { get; set; } = true;
        public bool Confirm { get; set; } = true;
        public bool DryRun { get; set; }
        public long SandboxId { get; } = 7;
        public long Magic { get; } = 11;
        public bool LocalExists { get; set; } = true;
        public bool ApplyDelete { get; set; } = true;
        public bool ThrowOnLedgerWrite { get; set; }
        public string? DeleteHookOverride { get; set; }
        public Dictionary<string, Item> Objects { get; } = new(StringComparer.Ordinal);
        public List<string> DetailCalls { get; } = new();

        public Fake()
        {
            Objects["/"] = new Item("directory", 1, 0, "");
            Objects["/dir"] = new Item("directory", 1, 0, "");
        }

        public void Seed(string path, string content)
        {
            Objects[path] = new Item("file", 1, Encoding.UTF8.GetByteCount(content), "HASH-" + path.GetHashCode().ToString("X"));
        }

        public void SeedLedgerFor(string ledgerPath, string path)
        {
            var item = Objects[path];
            Assert.True(CreatedObjectLedger.TryAppend(ledgerPath, new CreatedObjectLedger.Entry(
                DateTimeOffset.Now, SandboxId, path, item.Version, item.Hash, item.Size,
                "nutstore_l3_create_file", "inline-base64", CreatedObjectLedger.ActionCreated)));
        }

        public JsonDocument Delete(string ledgerPath, string cloudPath = "/dir/new.txt",
            bool? unlocked = null, bool? confirm = null, bool? dryRun = null)
        {
            _ledgerPath = ledgerPath;
            _cloudPath = cloudPath;
            return JsonDocument.Parse(CreatedObjectDelete.Delete(
                ledgerPath,
                unlocked ?? Unlocked, confirm ?? Confirm, dryRun ?? DryRun,
                SandboxId, Magic, cloudPath,
                Bindings, Detail, List, ExecuteDelete, AppendTombstone));
        }

        private string Bindings() => JsonSerializer.Serialize(new
        {
            ok = true,
            sandboxes = new[]
            {
                new
                {
                    id = SandboxId.ToString(),
                    magic = Magic.ToString(),
                    localPath = "C:\\sync-root",
                    localExists = LocalExists,
                },
            },
        });

        private string Detail(string path)
        {
            DetailCalls.Add(path);
            if (!Objects.TryGetValue(path, out var item))
            {
                return "{\"ok\":false,\"reason\":\"not found\"}";
            }

            return JsonSerializer.Serialize(new
            {
                ok = true,
                cloudPath = path,
                objectType = item.Type,
                deleted = "false",
                version = item.Version.ToString(),
                sizeBytes = item.Size.ToString(),
                hash = item.Hash,
            });
        }

        private string List(string path) => JsonSerializer.Serialize(new
        {
            ok = true,
            entries = Objects.Keys.Where(key => ParentOf(key) == path).ToArray(),
        });

        private string ExecuteDelete(long id, string path, long version, string hash)
        {
            if (DeleteHookOverride is not null) return DeleteHookOverride;
            if (ApplyDelete && Objects.TryGetValue(path, out var item))
            {
                if (item.Version != version || item.Hash != hash)
                {
                    return "{\"ok\":false,\"reason\":\"hook mismatch\"}";
                }

                Objects.Remove(path);
            }

            return JsonSerializer.Serialize(new { ok = true, deleted = true, verification = new { ok = true } });
        }

        private bool AppendTombstone(CreatedObjectLedger.Entry entry)
        {
            if (ThrowOnLedgerWrite) throw new InvalidOperationException("simulated ledger failure");
            return CreatedObjectLedger.TryAppend(_ledgerPath!, entry);
        }

        private string? _ledgerPath;
        private string _cloudPath = "/dir/new.txt";

        private static string ParentOf(string path)
        {
            if (path == "/") return string.Empty;
            var slash = path.LastIndexOf('/');
            return slash <= 0 ? "/" : path.Substring(0, slash);
        }
    }
}
