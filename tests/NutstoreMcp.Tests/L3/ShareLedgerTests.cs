using System.Text.Json;
using NutstoreMcp.Plugin.L3;

namespace NutstoreMcp.Tests;

/// <summary>分享台账的读写测试：只存 URL 的 SHA-256，不存原文。</summary>
public sealed class ShareLedgerTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        AppContext.BaseDirectory, "shareledger-temp-" + Guid.NewGuid().ToString("N").Substring(0, 8));

    public ShareLedgerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string FilePath => Path.Combine(_dir, "shares.jsonl");

    private static ShareLedger.Entry Entry(string path = "/dir/a.txt") => new(
        DateTimeOffset.Parse("2026-10-19T00:00:00Z"), 7, path, 3, 1893456000000,
        PasswordSet: true, DownloadDisabled: true, EnableUpload: false,
        UrlSha256: string.Concat(Enumerable.Repeat("0123456789abcdef", 4)),
        Action: ShareLedger.ActionPublished);

    [Fact]
    public void 追加后能原样读回()
    {
        Assert.True(ShareLedger.TryAppend(FilePath, Entry()));

        var entry = Assert.Single(ShareLedger.ReadAll(FilePath));

        Assert.Equal(7, entry.SandboxId);
        Assert.Equal("/dir/a.txt", entry.CloudPath);
        Assert.Equal(3, entry.PublishVersion);
        Assert.Equal(1893456000000, entry.ExpireMillsSinceEpoch);
        Assert.True(entry.PasswordSet);
        Assert.True(entry.DownloadDisabled);
        Assert.False(entry.EnableUpload);
        Assert.Equal("published", entry.Action);
    }

    [Fact]
    public void 台账里没有URL原文()
    {
        Assert.True(ShareLedger.TryAppend(FilePath, Entry()));

        var raw = File.ReadAllText(FilePath);

        Assert.DoesNotContain("http", raw, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("urlSha256", raw, StringComparison.Ordinal);
    }

    [Fact]
    public void 坏行被跳过()
    {
        File.WriteAllLines(FilePath, new[]
        {
            "not json",
            "{\"sandboxId\":7}",
            new string('x', ShareLedger.MaxLineLength + 1),
            JsonSerializer.Serialize(new
            {
                time = "2026-10-19T00:00:00+00:00",
                sandboxId = 7,
                cloudPath = "/dir/ok.txt",
                publishVersion = 1,
                expireMillsSinceEpoch = 0,
                passwordSet = false,
                downloadDisabled = false,
                enableUpload = false,
                urlSha256 = "abcdef",
                action = "published",
            }),
        });

        var entry = Assert.Single(ShareLedger.ReadAll(FilePath));
        Assert.Equal("/dir/ok.txt", entry.CloudPath);
    }

    [Fact]
    public void 找不到文件时返回空列表()
    {
        Assert.Empty(ShareLedger.ReadAll(Path.Combine(_dir, "missing.jsonl")));
    }
}
