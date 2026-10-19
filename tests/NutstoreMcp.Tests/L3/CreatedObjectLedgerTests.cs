using System.Text.Json;
using NutstoreMcp.Plugin.L3;

namespace NutstoreMcp.Tests;

/// <summary>
/// 创建台账的读写测试。删除工具只认这里，所以「读不出来」必须等于「不能删」，
/// 而不是「当作没有记录继续」。
/// </summary>
public sealed class CreatedObjectLedgerTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        AppContext.BaseDirectory, "ledger-temp-" + Guid.NewGuid().ToString("N").Substring(0, 8));

    public CreatedObjectLedgerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string FilePath => Path.Combine(_dir, "created-objects.jsonl");

    private static CreatedObjectLedger.Entry Entry(string path, long version = 1,
        string hash = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=", long size = 5,
        string action = CreatedObjectLedger.ActionCreated, long sandboxId = 7,
        string source = "inline-base64")
        => new(DateTimeOffset.Parse("2026-10-18T20:00:00Z"), sandboxId, path, version, hash, size,
            "nutstore_l3_create_file", source, action);

    [Fact]
    public void 追加后能原样读回()
    {
        Assert.True(CreatedObjectLedger.TryAppend(FilePath, Entry("/dir/a.txt")));

        var entries = CreatedObjectLedger.ReadAll(FilePath);

        var entry = Assert.Single(entries);
        Assert.Equal(7, entry.SandboxId);
        Assert.Equal("/dir/a.txt", entry.CloudPath);
        Assert.Equal(1, entry.Version);
        Assert.Equal(5, entry.Size);
        Assert.Equal(CreatedObjectLedger.ActionCreated, entry.Action);
        Assert.Equal("inline-base64", entry.ContentSource);
    }

    [Fact]
    public void 找不到台账文件时返回空列表()
    {
        Assert.Empty(CreatedObjectLedger.ReadAll(Path.Combine(_dir, "missing.jsonl")));
        Assert.Null(CreatedObjectLedger.FindLatestForPath(Path.Combine(_dir, "missing.jsonl"), 7, "/dir/a.txt"));
    }

    [Fact]
    public void 以最后一条记录为准并能区分删除动作()
    {
        Assert.True(CreatedObjectLedger.TryAppend(FilePath, Entry("/dir/a.txt")));
        Assert.True(CreatedObjectLedger.TryAppend(FilePath, Entry("/dir/a.txt", action: CreatedObjectLedger.ActionDeleted)));

        var latest = CreatedObjectLedger.FindLatestForPath(FilePath, 7, "/dir/a.txt");

        Assert.NotNull(latest);
        Assert.Equal(CreatedObjectLedger.ActionDeleted, latest!.Action);
    }

    [Fact]
    public void 再次创建会覆盖删除动作成为可删记录()
    {
        Assert.True(CreatedObjectLedger.TryAppend(FilePath, Entry("/dir/a.txt")));
        Assert.True(CreatedObjectLedger.TryAppend(FilePath, Entry("/dir/a.txt", action: CreatedObjectLedger.ActionDeleted)));
        Assert.True(CreatedObjectLedger.TryAppend(FilePath, Entry("/dir/a.txt", version: 2)));

        var latest = CreatedObjectLedger.FindLatestForPath(FilePath, 7, "/dir/a.txt");

        Assert.Equal(CreatedObjectLedger.ActionCreated, latest!.Action);
        Assert.Equal(2, latest.Version);
    }

    [Fact]
    public void 沙箱与路径都必须精确匹配()
    {
        Assert.True(CreatedObjectLedger.TryAppend(FilePath, Entry("/dir/A.txt")));

        Assert.NotNull(CreatedObjectLedger.FindLatestForPath(FilePath, 7, "/dir/A.txt"));
        // 大小写不同不算同一条记录：删除工具要求逐字符一致。
        Assert.Null(CreatedObjectLedger.FindLatestForPath(FilePath, 7, "/dir/a.txt"));
        // 别的沙箱的路径不能拿来删。
        Assert.Null(CreatedObjectLedger.FindLatestForPath(FilePath, 8, "/dir/A.txt"));
    }

    [Fact]
    public void 坏行与超长行被跳过而不是让整份台账失效()
    {
        File.WriteAllLines(FilePath, new[]
        {
            "not json",
            "{\"time\":\"nope\",\"sandboxId\":7,\"cloudPath\":\"/x\",\"version\":1,\"hash\":\"h\",\"size\":1,\"tool\":\"t\",\"action\":\"created\"}",
            new string('x', CreatedObjectLedger.MaxLineLength + 1),
            JsonSerializer.Serialize(new
            {
                time = "2026-10-18T20:00:00+00:00",
                sandboxId = 7,
                cloudPath = "/dir/ok.txt",
                version = 1,
                hash = "HASH",
                size = 3,
                tool = "nutstore_l3_create_file",
                contentSource = (string?)null,
                action = "created",
            }),
        });

        var entries = CreatedObjectLedger.ReadAll(FilePath);

        var entry = Assert.Single(entries);
        Assert.Equal("/dir/ok.txt", entry.CloudPath);
    }

    [Fact]
    public void 缺字段的记录不算有效记录()
    {
        File.WriteAllLines(FilePath, new[]
        {
            "{\"sandboxId\":7,\"cloudPath\":\"/dir/a.txt\"}",
            "{\"time\":\"2026-10-18T20:00:00+00:00\",\"sandboxId\":0,\"cloudPath\":\"/dir/a.txt\",\"version\":1,\"hash\":\"h\",\"size\":1,\"tool\":\"t\",\"action\":\"created\"}",
        });

        Assert.Empty(CreatedObjectLedger.ReadAll(FilePath));
    }
}
