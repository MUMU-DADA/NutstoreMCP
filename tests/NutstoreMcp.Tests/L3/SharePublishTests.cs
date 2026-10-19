using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NutstoreMcp.Plugin.L3;

namespace NutstoreMcp.Tests;

/// <summary>
/// P4（E1 分享）的守卫测试。核心有两条：
/// ① 默认不返回分享 URL（访问凭证），只有显式 returnUrl 才给；
/// ② 第一版只创建新分享，已有分享时拒绝，不覆盖既有权限/密码/有效期。
/// </summary>
public sealed class SharePublishTests : IDisposable
{
    private const string ShareUrl = "https://share.example.invalid/s/credential-token";

    private readonly string _dir = Path.Combine(
        AppContext.BaseDirectory, "share-temp-" + Guid.NewGuid().ToString("N").Substring(0, 8));

    public SharePublishTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string LedgerPath => Path.Combine(_dir, "shares.jsonl");

    [Fact]
    public void 成功创建分享但默认不返回URL()
    {
        var fake = new Fake();
        using var result = fake.Publish(LedgerPath);

        Assert.True(Flag(result, "allowed"));
        Assert.True(Flag(result, "attempted"));
        Assert.True(Flag(result, "executed"));

        var shares = result.RootElement.GetProperty("shares");
        Assert.Equal(JsonValueKind.Null, shares.GetProperty("url").ValueKind);
        Assert.True(shares.GetProperty("urlWithheld").GetBoolean());
        Assert.Equal(Sha256Hex(ShareUrl), shares.GetProperty("urlSha256").GetString());
        Assert.True(result.RootElement.GetProperty("verification").GetProperty("settingsMatch").GetBoolean());
        Assert.True(result.RootElement.GetProperty("verification").GetProperty("ledgerRecorded").GetBoolean());
        Assert.Contains(result.RootElement.GetProperty("notes").EnumerateArray(),
            note => note.GetString()!.Contains("访问凭证"));

        // 台账存在，且**不含 URL 原文**。
        var raw = File.ReadAllText(LedgerPath);
        Assert.DoesNotContain("https://", raw, StringComparison.Ordinal);
        var entries = ShareLedger.ReadAll(LedgerPath);
        var entry = Assert.Single(entries);
        Assert.Equal(Sha256Hex(ShareUrl), entry.UrlSha256);
        Assert.Equal("published", entry.Action);
    }

    [Fact]
    public void 显式returnUrl才返回链接()
    {
        var fake = new Fake();
        using var result = fake.Publish(LedgerPath, returnUrl: true);

        Assert.True(Flag(result, "executed"));
        var shares = result.RootElement.GetProperty("shares");
        Assert.Equal(ShareUrl, shares.GetProperty("url").GetString());
        Assert.False(shares.GetProperty("urlWithheld").GetBoolean());
    }

    [Fact]
    public void 目标已有分享时拒绝()
    {
        var fake = new Fake { Shared = true };
        using var result = fake.Publish(LedgerPath);

        Assert.False(Flag(result, "allowed"));
        Assert.Equal(0, fake.PublishCalls);
        Assert.Contains(result.RootElement.GetProperty("refusals").EnumerateArray(),
            error => error.GetString()!.Contains("已有分享"));
    }

    [Fact]
    public void 预演只读不创建()
    {
        var fake = new Fake();
        using var result = fake.Publish(LedgerPath, dryRun: true);

        Assert.True(Flag(result, "allowed"));
        Assert.False(Flag(result, "attempted"));
        Assert.False(Flag(result, "executed"));
        Assert.Equal(0, fake.PublishCalls);
        Assert.Empty(ShareLedger.ReadAll(LedgerPath));
    }

    [Fact]
    public void 钩子失败只报告不重试()
    {
        var fake = new Fake { PublishHookOverride = "{\"ok\":false,\"reason\":\"server refused\"}" };
        using var result = fake.Publish(LedgerPath);

        Assert.Equal(1, fake.PublishCalls);
        Assert.False(Flag(result, "executed"));
        Assert.Empty(ShareLedger.ReadAll(LedgerPath));
    }

    [Fact]
    public void 生效设置不一致不能报告成功()
    {
        var fake = new Fake
        {
            PublishHookOverride = JsonSerializer.Serialize(new
            {
                ok = true,
                url = ShareUrl,
                publishVersion = 3,
                settingsMatch = false,
                stateAfter = new { ok = true, shared = true },
            }),
        };
        using var result = fake.Publish(LedgerPath);

        Assert.False(Flag(result, "executed"));
        Assert.Contains(result.RootElement.GetProperty("refusals").EnumerateArray(),
            error => error.GetString()!.Contains("生效设置"));
    }

    [Fact]
    public void 写后分享状态不是已分享时不能报告成功()
    {
        var fake = new Fake { ApplyPublish = false };
        using var result = fake.Publish(LedgerPath);

        Assert.False(Flag(result, "executed"));
        Assert.Contains(result.RootElement.GetProperty("verification").GetProperty("errors").EnumerateArray(),
            error => error.GetString()!.Contains("不是「已分享」"));
    }

    [Theory]
    [InlineData(-1L, null)]
    [InlineData(0L, "password-too-long-0123456789012345678901234567890123456789012345678901234567890123")]
    public void 有效期或密码非法时拒绝(long expireHoursAsMills, string? password)
    {
        var fake = new Fake();
        using var result = fake.Publish(LedgerPath, expireMills: expireHoursAsMills, password: password);

        Assert.False(Flag(result, "allowed"));
        Assert.Equal(0, fake.PublishCalls);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void 未解锁或未确认直接拒绝(bool unlocked, bool confirm)
    {
        var fake = new Fake();
        using var result = fake.Publish(LedgerPath, unlocked: unlocked, confirm: confirm);

        Assert.False(Flag(result, "allowed"));
        Assert.Equal(0, fake.PublishCalls);
    }

    [Fact]
    public void 台账写失败不影响分享结果但会标注()
    {
        var fake = new Fake { ThrowOnLedgerWrite = true };
        using var result = fake.Publish(LedgerPath);

        Assert.True(Flag(result, "executed"));
        Assert.False(result.RootElement.GetProperty("verification").GetProperty("ledgerRecorded").GetBoolean());
    }

    private static string Sha256Hex(string text)
    {
        using var sha = SHA256.Create();
        var digest = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    private static bool Flag(JsonDocument result, string name) => result.RootElement.GetProperty(name).GetBoolean();

    private sealed class Fake
    {
        public long SandboxId { get; } = 7;
        public long Magic { get; } = 11;
        public string CloudPath { get; } = "/dir/new.txt";
        public bool Shared { get; set; }
        public bool ApplyPublish { get; set; } = true;
        public bool ThrowOnLedgerWrite { get; set; }
        public string? PublishHookOverride { get; set; }
        public int PublishCalls { get; private set; }

        public JsonDocument Publish(string ledgerPath, bool dryRun = false, bool returnUrl = false,
            long expireMills = 0, string? password = null,
            bool? unlocked = null, bool? confirm = null)
        {
            _ledgerPath = ledgerPath;
            var effectiveExpire = expireMills < 0 ? expireMills : expireMills;
            return JsonDocument.Parse(SharePublish.Publish(
                ledgerPath,
                unlocked ?? true, confirm ?? true, dryRun, SandboxId, Magic, CloudPath,
                effectiveExpire, password, downloadDisabled: false, enableUpload: false, returnUrl,
                Bindings, Detail, ReadShareInfo, ExecutePublish, AppendShare));
        }

        private string Bindings() => JsonSerializer.Serialize(new
        {
            ok = true,
            sandboxes = new[]
            {
                new { id = SandboxId.ToString(), magic = Magic.ToString(), localPath = "C:\\sync-root", localExists = true },
            },
        });

        private string Detail(string path) => JsonSerializer.Serialize(new
        {
            ok = true,
            cloudPath = path,
            objectType = "file",
            deleted = "false",
            version = "1",
            sizeBytes = "9",
        });

        private string ReadShareInfo(string path) => JsonSerializer.Serialize(new
        {
            ok = true,
            shared = Shared ? "true" : "false",
            version = Shared ? "3" : "0",
        });

        private string ExecutePublish(long id, string path, long expireMills, string? password,
            bool downloadDisabled, bool enableUpload)
        {
            PublishCalls++;
            if (PublishHookOverride is not null) return PublishHookOverride;

            if (ApplyPublish) Shared = true;
            return JsonSerializer.Serialize(new
            {
                ok = true,
                url = ShareUrl,
                publishVersion = 3,
                settingsMatch = true,
                stateAfter = new { ok = true, shared = ApplyPublish },
            });
        }

        private bool AppendShare(ShareLedger.Entry entry)
        {
            if (ThrowOnLedgerWrite) throw new InvalidOperationException("simulated ledger failure");
            return ShareLedger.TryAppend(_ledgerPath!, entry);
        }

        private string? _ledgerPath;
    }
}
