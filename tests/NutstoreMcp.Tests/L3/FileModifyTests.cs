using System.Text;
using System.Text.Json;
using NutstoreMcp.Plugin.L3;

namespace NutstoreMcp.Tests;

/// <summary>
/// P3（C4）修改文件当前版本的守卫测试。要证明的是：
/// 只改台账内、未被改动过的 MCP 自建文件；执行必须带预演看到的版本；
/// 写后版本/大小/hash、整文件回读与历史版本四条都要确认才算成功。
/// </summary>
public sealed class FileModifyTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        AppContext.BaseDirectory, "modify-temp-" + Guid.NewGuid().ToString("N").Substring(0, 8));

    public FileModifyTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string LedgerPath => Path.Combine(_dir, "created-objects.jsonl");

    [Fact]
    public void 预演展示当前版本与新内容摘要但不写()
    {
        var fake = new Fake();
        fake.SeedLedger(LedgerPath);
        using var result = fake.Modify(LedgerPath, dryRun: true);

        Assert.True(Flag(result, "allowed"));
        Assert.False(Flag(result, "attempted"));
        Assert.False(Flag(result, "executed"));
        Assert.Equal(1, result.RootElement.GetProperty("probe").GetProperty("current").GetProperty("version").GetInt64());
        Assert.Equal(CloudContentCodec.Sha256Hex(Encoding.UTF8.GetBytes("new content")),
            result.RootElement.GetProperty("probe").GetProperty("newContent").GetProperty("sha256").GetString());
        Assert.Equal(0, fake.ModifyCalls);
        Assert.Single(CreatedObjectLedger.ReadAll(LedgerPath));
    }

    [Fact]
    public void 执行成功并写入modified台账记录()
    {
        var fake = new Fake();
        fake.SeedLedger(LedgerPath);
        using var result = fake.Modify(LedgerPath, expectedVersion: 1);

        Assert.True(Flag(result, "allowed"));
        Assert.True(Flag(result, "attempted"));
        Assert.True(Flag(result, "executed"));

        var verification = result.RootElement.GetProperty("verification");
        Assert.True(verification.GetProperty("ok").GetBoolean());
        Assert.True(verification.GetProperty("contentMatches").GetBoolean());
        Assert.True(verification.GetProperty("readBackMatches").GetBoolean());
        Assert.True(verification.GetProperty("historyHasPrevious").GetBoolean());
        Assert.True(verification.GetProperty("ledgerRecorded").GetBoolean());
        Assert.Equal(2, verification.GetProperty("versionAfter").GetInt64());

        var entries = CreatedObjectLedger.ReadAll(LedgerPath);
        Assert.Equal(2, entries.Count);
        Assert.Equal("modified", entries[^1].Action);
        Assert.Equal(2, entries[^1].Version);
    }

    [Fact]
    public void 台账里没有记录时拒绝()
    {
        var fake = new Fake();
        using var result = fake.Modify(LedgerPath, expectedVersion: 1);

        Assert.False(Flag(result, "allowed"));
        Assert.Equal(0, fake.ModifyCalls);
        Assert.Contains(result.RootElement.GetProperty("refusals").EnumerateArray(),
            error => error.GetString()!.Contains("创建台账"));
    }

    [Fact]
    public void 台账最后一条是modified或deleted时拒绝()
    {
        foreach (var action in new[] { "modified", "deleted" })
        {
            var fake = new Fake();
            fake.SeedLedger(LedgerPath);
            CreatedObjectLedger.TryAppend(LedgerPath, new CreatedObjectLedger.Entry(
                DateTimeOffset.Now, fake.SandboxId, fake.CloudPath, 1, fake.Hash, fake.Size,
                "nutstore_l3_create_file", "inline-base64", action));

            using var result = fake.Modify(LedgerPath, expectedVersion: 1);

            Assert.False(Flag(result, "allowed"));
            Assert.Equal(0, fake.ModifyCalls);
        }
    }

    [Fact]
    public void 当前状态与台账不一致时拒绝()
    {
        foreach (var mutate in new Action<Fake>[]
                 {
                     fake => fake.Version = 2,
                     fake => fake.Size = 99,
                     fake => fake.Hash = "changed",
                 })
        {
            var fake = new Fake();
            fake.SeedLedger(LedgerPath);
            mutate(fake);

            using var result = fake.Modify(LedgerPath, expectedVersion: 1);

            Assert.False(Flag(result, "allowed"));
            Assert.Equal(0, fake.ModifyCalls);
            Assert.Contains(result.RootElement.GetProperty("refusals").EnumerateArray(),
                error => error.GetString()!.Contains("创建台账不一致"));
        }
    }

    [Fact]
    public void expectedVersion与当前版本不一致时拒绝()
    {
        var fake = new Fake();
        fake.SeedLedger(LedgerPath);
        using var result = fake.Modify(LedgerPath, expectedVersion: 9);

        Assert.False(Flag(result, "allowed"));
        Assert.Equal(0, fake.ModifyCalls);
        Assert.Contains(result.RootElement.GetProperty("refusals").EnumerateArray(),
            error => error.GetString()!.Contains("expectedVersion"));
    }

    [Fact]
    public void 执行时未传expectedVersion则拒绝()
    {
        var fake = new Fake();
        fake.SeedLedger(LedgerPath);
        using var result = fake.Modify(LedgerPath, dryRun: false, expectedVersion: 0);

        Assert.False(Flag(result, "allowed"));
        Assert.Equal(0, fake.ModifyCalls);
        Assert.Contains(result.RootElement.GetProperty("refusals").EnumerateArray(),
            error => error.GetString()!.Contains("必须显式传 expectedVersion"));
    }

    [Fact]
    public void 钩子失败只报告不重试()
    {
        var fake = new Fake { ModifyHookOverride = "{\"ok\":false,\"reason\":\"server refused\"}" };
        fake.SeedLedger(LedgerPath);
        using var result = fake.Modify(LedgerPath, expectedVersion: 1);

        Assert.Equal(1, fake.ModifyCalls);
        Assert.False(Flag(result, "executed"));
        Assert.Contains(result.RootElement.GetProperty("refusals").EnumerateArray(),
            error => error.GetString()!.Contains("客户端修改失败"));
    }

    [Fact]
    public void 写后版本没有变化时不能报告成功()
    {
        var fake = new Fake { ApplyModify = false };
        fake.SeedLedger(LedgerPath);
        using var result = fake.Modify(LedgerPath, expectedVersion: 1);

        Assert.False(Flag(result, "executed"));
        Assert.Contains(result.RootElement.GetProperty("verification").GetProperty("errors").EnumerateArray(),
            error => error.GetString()!.Contains("不大于修改前版本"));
    }

    [Fact]
    public void 回读字节不一致时不能报告成功()
    {
        var fake = new Fake { CorruptContentAfterModify = true };
        fake.SeedLedger(LedgerPath);
        using var result = fake.Modify(LedgerPath, expectedVersion: 1);

        Assert.False(Flag(result, "executed"));
        Assert.False(result.RootElement.GetProperty("verification").GetProperty("readBackMatches").GetBoolean());
    }

    [Fact]
    public void 历史版本里没有旧版本时不能报告成功()
    {
        var fake = new Fake { HistoryDropsPrevious = true };
        fake.SeedLedger(LedgerPath);
        using var result = fake.Modify(LedgerPath, expectedVersion: 1);

        Assert.False(Flag(result, "executed"));
        Assert.False(result.RootElement.GetProperty("verification").GetProperty("historyHasPrevious").GetBoolean());
    }

    [Fact]
    public void 空内容或超限内容在预检前被拒绝()
    {
        var fake = new Fake();
        fake.SeedLedger(LedgerPath);
        using (var empty = fake.Modify(LedgerPath, content: "", expectedVersion: 1))
        {
            Assert.False(Flag(empty, "allowed"));
        }

        var big = new byte[FileModify.MaxContentBytes + 1];
        using (var tooLarge = fake.Modify(LedgerPath, contentBytes: big, expectedVersion: 1))
        {
            Assert.False(Flag(tooLarge, "allowed"));
        }

        Assert.Equal(0, fake.ModifyCalls);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void 未解锁或未确认直接拒绝(bool unlocked, bool confirm)
    {
        var fake = new Fake();
        fake.SeedLedger(LedgerPath);
        using var result = fake.Modify(LedgerPath, expectedVersion: 1, unlocked: unlocked, confirm: confirm);

        Assert.False(Flag(result, "allowed"));
        Assert.Equal(0, fake.ModifyCalls);
    }

    private static bool Flag(JsonDocument result, string name) => result.RootElement.GetProperty(name).GetBoolean();

    private sealed class Fake
    {
        public string RootPath { get; } = "C:\\sync-root";
        public long SandboxId { get; } = 7;
        public long Magic { get; } = 11;
        public string CloudPath { get; } = "/dir/new.txt";
        public long Version { get; set; } = 1;
        public long Size { get; set; } = 9;
        public string Hash { get; set; } = "OLD-HASH";
        public bool ApplyModify { get; set; } = true;
        public bool CorruptContentAfterModify { get; set; }
        public bool HistoryDropsPrevious { get; set; }
        public string? ModifyHookOverride { get; set; }
        public int ModifyCalls { get; private set; }

        private byte[] _oldBytes = Encoding.UTF8.GetBytes("old bytes");
        private byte[] _newBytes = Encoding.UTF8.GetBytes("new content");
        private string _newHash = string.Empty;

        public void SeedLedger(string ledgerPath)
        {
            Assert.True(CreatedObjectLedger.TryAppend(ledgerPath, new CreatedObjectLedger.Entry(
                DateTimeOffset.Now, SandboxId, CloudPath, Version, Hash, Size,
                "nutstore_l3_create_file", "inline-base64", CreatedObjectLedger.ActionCreated)));
        }

        public JsonDocument Modify(string ledgerPath, bool dryRun = false, long expectedVersion = 0,
            string? content = "new content", byte[]? contentBytes = null,
            bool? unlocked = null, bool? confirm = null)
        {
            _ledgerPath = ledgerPath;
            _newBytes = contentBytes ?? Encoding.UTF8.GetBytes(content ?? string.Empty);
            _newHash = _newBytes.Length == 0 ? CloudContentCodec.EmptyFileHashBase64 : CloudContentCodec.CloudHash(_newBytes);
            return JsonDocument.Parse(FileModify.Modify(
                ledgerPath,
                unlocked ?? true, confirm ?? true, dryRun, SandboxId, Magic, CloudPath,
                _newBytes.Length, CloudContentCodec.Sha256Hex(_newBytes),
                Convert.ToBase64String(_newBytes), expectedVersion,
                Bindings, Detail, ReadInline, ExecuteModify, ListVersions, AppendModified));
        }

        private string Bindings() => JsonSerializer.Serialize(new
        {
            ok = true,
            sandboxes = new[]
            {
                new { id = SandboxId.ToString(), magic = Magic.ToString(), localPath = RootPath, localExists = true },
            },
        });

        private string Detail(string path) => JsonSerializer.Serialize(new
        {
            ok = true,
            cloudPath = path,
            objectType = "file",
            deleted = "false",
            version = Version.ToString(),
            sizeBytes = Size.ToString(),
            hash = Hash,
        });

        private string ReadInline(string path, long offset, long length)
        {
            var start = (int)Math.Min(offset, _newBytes.Length);
            var count = (int)Math.Min(length, _newBytes.Length - start);
            var slice = _newBytes.AsSpan(start, count).ToArray();
            var corrupt = CorruptContentAfterModify && slice.Length > 0;
            if (corrupt) slice[^1] ^= 0xFF;
            return JsonSerializer.Serialize(new
            {
                ok = true, offset, length = count, totalSize = _newBytes.Length,
                text = (string?)null, bom = false, contentBase64 = Convert.ToBase64String(slice),
            });
        }

        private string ExecuteModify(long id, string path, long version, string base64)
        {
            ModifyCalls++;
            if (ModifyHookOverride is not null) return ModifyHookOverride;
            if (ApplyModify)
            {
                Version = version + 1;
                Size = _newBytes.Length;
                Hash = _newHash;
            }

            return JsonSerializer.Serialize(new
            {
                ok = true,
                cloudHash = _newHash,
                version = version + 1,
                historyOk = true,
                verification = new { ok = true },
            });
        }

        private string ListVersions(string path)
        {
            var versions = HistoryDropsPrevious
                ? new[] { Version }
                : new[] { 1L, Version };
            return JsonSerializer.Serialize(new
            {
                ok = true,
                count = versions.Length,
                versions = versions.Select(v => new { version = v.ToString() }).ToArray(),
            });
        }

        private bool AppendModified(CreatedObjectLedger.Entry entry)
            => CreatedObjectLedger.TryAppend(_ledgerPath!, entry);

        private string? _ledgerPath;
    }
}
