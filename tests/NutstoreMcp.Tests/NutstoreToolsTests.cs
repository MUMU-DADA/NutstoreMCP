using System.Text.Json;
using NutstoreMcp;

namespace NutstoreMcp.Tests;

/// <summary>
/// 工具层护栏。用真实的 <see cref="WebDavClient"/> 打 mock 服务器，
/// 所以「守卫在发请求之前生效」这类断言是真的在验证行为，而不是验证桩。
/// </summary>
public class NutstoreToolsTests
{
    private static AppConfig MakeConfig(string baseUrl, Dictionary<string, string?>? overrides = null)
    {
        var env = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["NUTSTORE_USER"] = "test@example.com",
            ["NUTSTORE_PASS"] = "app-password",
            ["NUTSTORE_HOST"] = baseUrl,
            ["NUTSTORE_ROOTS"] = "/code",
            ["NUTSTORE_READ_ONLY"] = "true",
            ["NUTSTORE_ENV_FILE"] = Path.Combine(Path.GetTempPath(), "ns-mcp-no-such.env"),
        };

        if (overrides is not null)
        {
            foreach (var (k, v) in overrides)
            {
                env[k] = v;
            }
        }

        return AppConfig.Load(env);
    }

    private sealed class Harness : IAsyncDisposable
    {
        public required MockWebDavServer Server { get; init; }
        public required WebDavClient Webdav { get; init; }
        public required LocalNutstoreData Local { get; init; }
        public required NutstoreTools Tools { get; init; }
        public required string LocalRoot { get; init; }
        public required AppConfig Config { get; init; }

        public async ValueTask DisposeAsync()
        {
            Webdav.Dispose();
            Local.Dispose();
            await Server.DisposeAsync();
        }
    }

    private static async Task<Harness> SetupAsync(
        Dictionary<string, string?>? envOverrides = null,
        Action<MockWebDavServer>? configure = null)
    {
        var server = await MockWebDavServer.StartAsync();
        server.AddDirectory("/code");
        configure?.Invoke(server);

        var localRoot = TestScratch.NewDirectory("tools");

        var overrides = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["NUTSTORE_LOCAL_ROOTS"] = localRoot,
        };
        if (envOverrides is not null)
        {
            foreach (var (k, v) in envOverrides)
            {
                overrides[k] = v;
            }
        }

        var config = MakeConfig(server.BaseUrl, overrides);
        var limiter = new SlidingWindowRateLimiter(config.RateLimit, config.RateWindowMs);
        var webdav = new WebDavClient(config, limiter);
        var local = new LocalNutstoreData(Path.Combine(localRoot, "__no_db__"));
        var tools = new NutstoreTools(config, webdav, local);

        return new Harness
        {
            Server = server,
            Webdav = webdav,
            Local = local,
            Tools = tools,
            LocalRoot = localRoot,
            Config = config,
        };
    }

    // ───────────────────────── 只读闸门 ─────────────────────────

    [Fact]
    public async Task ReadOnly_BlocksEveryWriteWithoutTouchingWebDav()
    {
        await using var h = await SetupAsync();
        var ct = TestContext.Current.CancellationToken;
        var before = h.Server.Requests.Count;

        await Assert.ThrowsAsync<ReadOnlyException>(() => h.Tools.UploadAsync("a.txt", "/code/a.txt", ct));
        await Assert.ThrowsAsync<ReadOnlyException>(() => h.Tools.MkdirAsync("/code/x", false, ct));
        await Assert.ThrowsAsync<ReadOnlyException>(() => h.Tools.MoveAsync("/code/a", "/code/b", false, ct));
        await Assert.ThrowsAsync<ReadOnlyException>(() => h.Tools.DeleteAsync("/code/a", confirm: true, ct));

        Assert.Equal(before, h.Server.Requests.Count);
    }

    [Fact]
    public async Task ReadOnly_StillAllowsReads()
    {
        await using var h = await SetupAsync(configure: s => s.AddFile("/code/a.md", "hi"));

        await h.Tools.ListAsync("/code", TestContext.Current.CancellationToken);
        await h.Tools.StatAsync("/code/a.md", TestContext.Current.CancellationToken);
        await h.Tools.ReadTextAsync("/code/a.md", ct: TestContext.Current.CancellationToken);

        Assert.Equal(3, h.Server.Requests.Count);
    }

    [Fact]
    public async Task WritableMode_AllowsMkdir()
    {
        await using var h = await SetupAsync(new Dictionary<string, string?> { ["NUTSTORE_READ_ONLY"] = "false" });

        var single = JsonDocument.Parse(await h.Tools.MkdirAsync("/code/new", false, TestContext.Current.CancellationToken));
        Assert.True(single.RootElement.GetProperty("created").GetBoolean());

        var deep = JsonDocument.Parse(await h.Tools.MkdirAsync("/code/deep/sub", true, TestContext.Current.CancellationToken));
        Assert.Contains("/code/deep/sub", deep.RootElement.GetProperty("createdPaths")
            .EnumerateArray().Select(e => e.GetString()));
    }

    // ───────────────────────── 删除二次确认 ─────────────────────────

    [Fact]
    public async Task Delete_RequiresExplicitConfirm()
    {
        await using var h = await SetupAsync(new Dictionary<string, string?> { ["NUTSTORE_READ_ONLY"] = "false" }, s => s.AddFile("/code/a.md", "x"));
        var ct = TestContext.Current.CancellationToken;
        var before = h.Server.Requests.Count;

        var ex = await Assert.ThrowsAsync<RemoteException>(() => h.Tools.DeleteAsync("/code/a.md", confirm: false, ct));
        Assert.Contains("需要显式确认", ex.Message, StringComparison.Ordinal);
        Assert.Equal(before, h.Server.Requests.Count);

        var ok = JsonDocument.Parse(await h.Tools.DeleteAsync("/code/a.md", confirm: true, ct));
        Assert.True(ok.RootElement.GetProperty("deleted").GetBoolean());
    }

    [Fact]
    public async Task Delete_ConfirmDoesNotBypassReadOnly()
    {
        await using var h = await SetupAsync();
        var ex = await Assert.ThrowsAsync<ReadOnlyException>(
            () => h.Tools.DeleteAsync("/code/a", confirm: true, TestContext.Current.CancellationToken));
        Assert.Equal("READ_ONLY", ex.Code);
    }

    // ───────────────────────── 云端白名单 ─────────────────────────

    [Fact]
    public async Task CloudAllowlist_AppliesToEveryTool()
    {
        await using var h = await SetupAsync(new Dictionary<string, string?> { ["NUTSTORE_READ_ONLY"] = "false" });
        var ct = TestContext.Current.CancellationToken;
        var before = h.Server.Requests.Count;

        await Assert.ThrowsAsync<PathNotAllowedException>(() => h.Tools.ListAsync("/secret", ct));
        await Assert.ThrowsAsync<PathNotAllowedException>(() => h.Tools.StatAsync("/secret/a", ct));
        await Assert.ThrowsAsync<PathNotAllowedException>(() => h.Tools.ReadTextAsync("/secret/a", ct: ct));
        await Assert.ThrowsAsync<PathNotAllowedException>(() => h.Tools.MkdirAsync("/secret/x", false, ct));
        await Assert.ThrowsAsync<PathNotAllowedException>(() => h.Tools.MoveAsync("/code/a", "/secret/a", false, ct));
        await Assert.ThrowsAsync<PathNotAllowedException>(() => h.Tools.DeleteAsync("/secret/a", true, ct));
        await Assert.ThrowsAsync<PathNotAllowedException>(() => h.Tools.UploadAsync("f.txt", "/secret/f.txt", ct));
        Assert.Throws<PathNotAllowedException>(() => h.Tools.FileStatus("/secret/a"));

        Assert.Equal(before, h.Server.Requests.Count);
    }

    [Fact]
    public async Task EmptyAllowlist_DeniesEverything()
    {
        await using var h = await SetupAsync(new Dictionary<string, string?> { ["NUTSTORE_ROOTS"] = string.Empty });
        await Assert.ThrowsAsync<PathNotAllowedException>(() => h.Tools.ListAsync("/code", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TraversalAndBackslashAreRejected()
    {
        await using var h = await SetupAsync();
        await Assert.ThrowsAsync<PathNotAllowedException>(() => h.Tools.ListAsync("/code/../../etc", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<PathNotAllowedException>(() => h.Tools.ListAsync("C:\\Users", TestContext.Current.CancellationToken));
    }

    // ───────────────────────── 本机白名单 ─────────────────────────

    [Fact]
    public async Task Download_TargetMustBeInsideLocalAllowlist()
    {
        await using var h = await SetupAsync(null, s => s.AddFile("/code/a.txt", "payload"));
        var outside = Path.Combine(Path.GetTempPath(), "ns-mcp-outside-" + Guid.NewGuid().ToString("N"), "x.txt");
        var before = h.Server.Requests.Count;

        var ex = await Assert.ThrowsAsync<PathNotAllowedException>(
            () => h.Tools.DownloadAsync("/code/a.txt", outside, TestContext.Current.CancellationToken));
        Assert.Contains("NUTSTORE_LOCAL_ROOTS", ex.Hint!, StringComparison.Ordinal);
        Assert.Equal(before, h.Server.Requests.Count);
    }

    [Fact]
    public async Task Download_ToAllowedPathActuallyWritesAndReportsOverwrite()
    {
        await using var h = await SetupAsync(null, s => s.AddFile("/code/a.txt", "payload"));

        var first = JsonDocument.Parse(await h.Tools.DownloadAsync("/code/a.txt", Path.Combine("sub", "a.txt"), TestContext.Current.CancellationToken));
        Assert.Equal(7, first.RootElement.GetProperty("bytes").GetInt64());
        Assert.False(first.RootElement.GetProperty("overwritten").GetBoolean());

        var target = Path.Combine(h.LocalRoot, "sub", "a.txt");
        Assert.Equal("payload", await File.ReadAllTextAsync(target, TestContext.Current.CancellationToken));

        var second = JsonDocument.Parse(await h.Tools.DownloadAsync("/code/a.txt", Path.Combine("sub", "a.txt"), TestContext.Current.CancellationToken));
        Assert.True(second.RootElement.GetProperty("overwritten").GetBoolean());
    }

    [Fact]
    public async Task Upload_SourceMustBeInsideLocalAllowlist()
    {
        await using var h = await SetupAsync(new Dictionary<string, string?> { ["NUTSTORE_READ_ONLY"] = "false" });
        var before = h.Server.Requests.Count;

        await Assert.ThrowsAsync<PathNotAllowedException>(
            () => h.Tools.UploadAsync(@"C:\Windows\win.ini", "/code/win.ini", TestContext.Current.CancellationToken));
        Assert.Equal(before, h.Server.Requests.Count);
    }

    [Fact]
    public async Task Upload_TrailingSlashAppendsFileName_OtherwiseExactPath()
    {
        await using var h = await SetupAsync(new Dictionary<string, string?> { ["NUTSTORE_READ_ONLY"] = "false" });
        await File.WriteAllTextAsync(Path.Combine(h.LocalRoot, "report.md"), "x", TestContext.Current.CancellationToken);
        var ct = TestContext.Current.CancellationToken;

        var a = JsonDocument.Parse(await h.Tools.UploadAsync("report.md", "/code/2026/", ct));
        Assert.Equal("/code/2026/report.md", a.RootElement.GetProperty("path").GetString());

        var b = JsonDocument.Parse(await h.Tools.UploadAsync("report.md", "/code/renamed.md", ct));
        Assert.Equal("/code/renamed.md", b.RootElement.GetProperty("path").GetString());

        // 无扩展名的目标不应被「猜」成目录
        var c = JsonDocument.Parse(await h.Tools.UploadAsync("report.md", "/code/notes", ct));
        Assert.Equal("/code/notes", c.RootElement.GetProperty("path").GetString());
    }

    // ───────────────────────── health ─────────────────────────

    [Fact]
    public async Task Health_WithoutCredentialsReportsL1UnavailableButKeepsGoing()
    {
        await using var h = await SetupAsync(new Dictionary<string, string?>
        {
            ["NUTSTORE_USER"] = string.Empty,
            ["NUTSTORE_PASS"] = string.Empty,
        });

        var doc = JsonDocument.Parse(await h.Tools.HealthAsync(TestContext.Current.CancellationToken));
        Assert.False(doc.RootElement.GetProperty("config").GetProperty("hasCredentials").GetBoolean());

        var webdav = doc.RootElement.GetProperty("webdav");
        Assert.False(webdav.GetProperty("ok").GetBoolean());
        Assert.Equal("NO_CREDENTIALS", webdav.GetProperty("reason").GetString());
        Assert.Contains("NUTSTORE_USER", webdav.GetProperty("hint").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Health_WithCredentialsReportsConnectivity()
    {
        await using var h = await SetupAsync();
        var doc = JsonDocument.Parse(await h.Tools.HealthAsync(TestContext.Current.CancellationToken));
        var webdav = doc.RootElement.GetProperty("webdav");
        Assert.True(webdav.GetProperty("ok").GetBoolean());
        Assert.Equal("test@example.com", webdav.GetProperty("authenticatedAs").GetString());
    }

    [Fact]
    public async Task Health_LocalUnavailableStillReportsHint()
    {
        await using var h = await SetupAsync();
        var doc = JsonDocument.Parse(await h.Tools.HealthAsync(TestContext.Current.CancellationToken));
        var local = doc.RootElement.GetProperty("local");
        Assert.False(local.GetProperty("available").GetBoolean());
        Assert.Contains("L1 文件面不受影响", local.GetProperty("hint").GetString()!, StringComparison.Ordinal);
    }

    // ───────────────────────── 参数透传 ─────────────────────────

    [Fact]
    public async Task ReadText_ForwardsMaxBytes()
    {
        await using var h = await SetupAsync(null, s => s.AddFile("/code/a.md", new string('x', 500)));
        var ct = TestContext.Current.CancellationToken;

        await Assert.ThrowsAsync<RemoteException>(() => h.Tools.ReadTextAsync("/code/a.md", 10, ct));

        var ok = JsonDocument.Parse(await h.Tools.ReadTextAsync("/code/a.md", 1000, ct));
        Assert.Equal(500, ok.RootElement.GetProperty("bytes").GetInt64());
    }

    [Fact]
    public async Task Move_DefaultsToNotOverwriting()
    {
        await using var h = await SetupAsync(
            new Dictionary<string, string?> { ["NUTSTORE_READ_ONLY"] = "false" },
            s => s.AddFile("/code/a.md", "x"));

        var doc = JsonDocument.Parse(await h.Tools.MoveAsync("/code/a.md", "/code/b.md", false, TestContext.Current.CancellationToken));
        Assert.Equal("/code/b.md", doc.RootElement.GetProperty("to").GetString());
        // overwrite=false 时目标不存在，mock 返回 201 → overwritten=false
        Assert.False(doc.RootElement.GetProperty("overwritten").GetBoolean());
    }

    [Fact]
    public async Task SyncFolders_WithoutDatabaseGivesActionableError()
    {
        await using var h = await SetupAsync();
        var ex = Assert.Throws<LocalDataException>(() => h.Tools.SyncFolders());
        Assert.Equal("LOCAL_DATA_UNAVAILABLE", ex.Code);
    }
}
