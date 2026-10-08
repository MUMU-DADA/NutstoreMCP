using System.Text;
using Microsoft.AspNetCore.Http;
using NutstoreMcp;

namespace NutstoreMcp.Tests;

public class WebDavClientTests
{
    private static AppConfig MakeConfig(string baseUrl, Dictionary<string, string?>? overrides = null)
    {
        var env = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["NUTSTORE_USER"] = "test@example.com",
            ["NUTSTORE_PASS"] = "app-password",
            ["NUTSTORE_HOST"] = baseUrl,
            ["NUTSTORE_ROOTS"] = "/code",
            ["NUTSTORE_READ_ONLY"] = "false",
            // 指向不存在的文件，保证测试不读仓库根目录的 .env
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

    private static (WebDavClient Client, SlidingWindowRateLimiter Limiter) MakeClient(AppConfig config)
    {
        var limiter = new SlidingWindowRateLimiter(config.RateLimit, config.RateWindowMs);
        return (new WebDavClient(config, limiter), limiter);
    }

    private static async Task<(MockWebDavServer Server, WebDavClient Client)> SetupAsync(
        Action<MockWebDavServer>? configure = null,
        Dictionary<string, string?>? envOverrides = null)
    {
        var server = await MockWebDavServer.StartAsync();
        server.AddDirectory("/code");
        configure?.Invoke(server);
        var config = MakeConfig(server.BaseUrl, envOverrides);
        var (client, _) = MakeClient(config);
        return (server, client);
    }

    [Fact]
    public async Task List_ParsesMultiStatusAndDistinguishesTypes()
    {
        var (server, client) = await SetupAsync(s =>
        {
            s.AddFile("/code/a.md", "hello");
            s.AddDirectory("/code/sub");
            s.AddFile("/code/sub/b.txt", "x");
        });

        await using (server)
        using (client)
        {
            var result = await client.ListAsync("/code", TestContext.Current.CancellationToken);

            Assert.Equal("/code", result.Path);
            Assert.False(result.Truncated);
            Assert.Null(result.Warning);

            var byPath = result.Entries.ToDictionary(e => e.Path, StringComparer.Ordinal);
            Assert.Equal(["/code/a.md", "/code/sub"], byPath.Keys.OrderBy(x => x, StringComparer.Ordinal));

            Assert.False(byPath["/code/a.md"].IsCollection);
            Assert.Equal(5, byPath["/code/a.md"].Size);
            Assert.True(byPath["/code/sub"].IsCollection);

            // 只返回直接子项，不含孙项
            Assert.DoesNotContain("/code/sub/b.txt", byPath.Keys);
        }
    }

    [Fact]
    public async Task StatAndExists()
    {
        var (server, client) = await SetupAsync(configure: s => s.AddFile("/code/a.md", "abc"));

        await using (server)
        using (client)
        {
            var stat = await client.StatAsync("/code/a.md", TestContext.Current.CancellationToken);
            Assert.Equal("/code/a.md", stat.Path);
            Assert.Equal(3, stat.Size);
            Assert.False(stat.IsCollection);

            Assert.True(await client.ExistsAsync("/code/a.md", TestContext.Current.CancellationToken));
            Assert.False(await client.ExistsAsync("/code/missing.md", TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task Stat_ThrowsNotFoundForMissingPath()
    {
        var (server, client) = await SetupAsync();
        await using (server)
        using (client)
        {
            await Assert.ThrowsAsync<RemoteNotFoundException>(
                () => client.StatAsync("/code/nope", TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task ReadText_EnforcesSizeLimit()
    {
        var (server, client) = await SetupAsync(s =>
        {
            s.AddFile("/code/small.txt", "你好，世界");
            s.AddFile("/code/big.txt", new string('a', 5000));
        });

        await using (server)
        using (client)
        {
            var (text, bytes) = await client.ReadTextAsync("/code/small.txt", 1024 * 1024, TestContext.Current.CancellationToken);
            Assert.Equal("你好，世界", text);
            Assert.Equal(Encoding.UTF8.GetByteCount("你好，世界"), bytes);

            var ex = await Assert.ThrowsAsync<RemoteException>(
                () => client.ReadTextAsync("/code/big.txt", 1000, TestContext.Current.CancellationToken));
            Assert.Contains("超过读取上限", ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task UploadAndDownload_RoundTrips()
    {
        var (server, client) = await SetupAsync();

        await using (server)
        using (client)
        {
            var payload = Encoding.UTF8.GetBytes("payload-1");
            var up = await client.UploadAsync(
                "/code/new.txt", () => new MemoryStream(payload), payload.Length, "text/plain",
                TestContext.Current.CancellationToken);

            Assert.True(up.Created);
            Assert.Equal(9, up.Bytes);
            Assert.True(up.SizeKnown);

            using var download = await client.DownloadAsync("/code/new.txt", TestContext.Current.CancellationToken);
            using var reader = new StreamReader(download.Stream, Encoding.UTF8);
            Assert.Equal("payload-1", await reader.ReadToEndAsync(TestContext.Current.CancellationToken));
            Assert.Equal(9, download.Size);

            Assert.Equal("payload-1", Encoding.UTF8.GetString(server.Tree["/code/new.txt"].Data!));
        }
    }

    [Fact]
    public async Task Upload_RejectsOversizeBeforeSendingRequest()
    {
        var (server, client) = await SetupAsync(envOverrides: new Dictionary<string, string?> { ["NUTSTORE_MAX_UPLOAD_BYTES"] = "10" });

        await using (server)
        using (client)
        {
            var before = server.Requests.Count;
            var ex = await Assert.ThrowsAsync<RemoteException>(
                () => client.UploadAsync(
                    "/code/big.bin", () => new MemoryStream(new byte[100]), 100, null,
                    TestContext.Current.CancellationToken));

            Assert.Contains("超过上传上限", ex.Message, StringComparison.Ordinal);
            Assert.Contains("500 MB", ex.Hint!, StringComparison.Ordinal);
            Assert.Equal(before, server.Requests.Count);
        }
    }

    [Fact]
    public async Task Mkdir_IsIdempotent_AndMkdirpCreatesEachLevel()
    {
        var (server, client) = await SetupAsync();

        await using (server)
        using (client)
        {
            var first = await client.MkdirAsync("/code/x", TestContext.Current.CancellationToken);
            Assert.True(first.Created);
            Assert.False(first.Existed);

            var again = await client.MkdirAsync("/code/x", TestContext.Current.CancellationToken);
            Assert.False(again.Created);
            Assert.True(again.Existed);

            var deep = await client.MkdirpAsync("/code/p/q/r", TestContext.Current.CancellationToken);
            Assert.Equal(["/code/p", "/code/p/q", "/code/p/q/r"], deep.CreatedPaths);
        }
    }

    [Fact]
    public async Task MoveAndDelete()
    {
        var (server, client) = await SetupAsync(configure: s => s.AddFile("/code/a.md", "v"));

        await using (server)
        using (client)
        {
            var moved = await client.MoveAsync("/code/a.md", "/code/b.md", overwrite: false, TestContext.Current.CancellationToken);
            Assert.Equal("/code/b.md", moved.To);
            Assert.False(moved.Overwritten);
            Assert.True(server.Tree.ContainsKey("/code/b.md"));
            Assert.False(server.Tree.ContainsKey("/code/a.md"));

            var deleted = await client.RemoveAsync("/code/b.md", TestContext.Current.CancellationToken);
            Assert.True(deleted.Deleted);

            var gone = await client.RemoveAsync("/code/b.md", TestContext.Current.CancellationToken);
            Assert.False(gone.Deleted);
            Assert.False(gone.Existed);
        }
    }

    [Fact]
    public async Task AuthFailure_HintMentionsAppPassword()
    {
        var (server, client) = await SetupAsync(configure: s => s.Pass = "the-right-one");

        await using (server)
        using (client)
        {
            var ex = await Assert.ThrowsAsync<AuthException>(
                () => client.ListAsync("/code", TestContext.Current.CancellationToken));

            Assert.Equal("AUTH_FAILED", ex.Code);
            Assert.Contains("应用密码", ex.Hint!, StringComparison.Ordinal);
            Assert.Contains("不是登录密码", ex.Hint!, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task MissingCredentials_ErrorsWithoutSendingRequest()
    {
        var (server, client) = await SetupAsync();
        await using (server)
        using (client)
        {
            var config = MakeConfig(server.BaseUrl, new Dictionary<string, string?>
            {
                ["NUTSTORE_USER"] = string.Empty,
                ["NUTSTORE_PASS"] = string.Empty,
            });

            var (anon, _) = MakeClient(config);
            using (anon)
            {
                var before = server.Requests.Count;
                var ex = await Assert.ThrowsAsync<AuthException>(
                    () => anon.ListAsync("/code", TestContext.Current.CancellationToken));
                Assert.Contains("NUTSTORE_USER", ex.Hint!, StringComparison.Ordinal);
                Assert.Equal(before, server.Requests.Count);
            }
        }
    }

    [Fact]
    public async Task Allowlist_RejectsOutsidePathsWithoutSendingRequest()
    {
        var (server, client) = await SetupAsync(configure: s => s.AddFile("/secret/x", "s"));

        await using (server)
        using (client)
        {
            var before = server.Requests.Count;
            await Assert.ThrowsAsync<PathNotAllowedException>(() => client.ListAsync("/secret", TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<PathNotAllowedException>(() => client.StatAsync("/secret/x", TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<PathNotAllowedException>(() => client.RemoveAsync("/secret/x", TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<PathNotAllowedException>(() => client.MoveAsync("/code/a", "/secret/a", false, TestContext.Current.CancellationToken));
            Assert.Equal(before, server.Requests.Count);
        }
    }

    [Fact]
    public async Task TruncationIsReportedNotSilentlySwallowed()
    {
        var (server, client) = await SetupAsync(s =>
        {
            for (var i = 0; i < MultiStatus.ListLimit + 50; i++)
            {
                s.AddFile($"/code/f{i:D4}.txt", "x");
            }

            s.PropfindLimit = MultiStatus.ListLimit;
        });

        await using (server)
        using (client)
        {
            var result = await client.ListAsync("/code", TestContext.Current.CancellationToken);
            Assert.True(result.Truncated);
            // 750 条响应含目录自身，所以子项是 749
            Assert.Equal(MultiStatus.ListLimit - 1, result.Entries.Count);
            Assert.Contains("750", result.Warning!, StringComparison.Ordinal);
            Assert.Contains("可能不完整", result.Warning!, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task SameOriginRedirectIsFollowedWithMethodPreserved()
    {
        var (server, client) = await SetupAsync(s =>
        {
            s.AddDirectory("/code/redir");
            s.RedirectPrefix = "/code/redir";
        });

        await using (server)
        using (client)
        {
            var result = await client.ListAsync("/code/redir", TestContext.Current.CancellationToken);
            Assert.Equal("/code/redir", result.Path);

            var propfinds = server.Requests.Where(r => r.Method == "PROPFIND").ToList();
            Assert.Equal(2, propfinds.Count);
            Assert.All(propfinds, r => Assert.NotNull(r.Authorization));
        }
    }

    [Fact]
    public async Task CrossOriginRedirectIsRefused()
    {
        await using var other = await MockWebDavServer.StartAsync();

        var (server, client) = await SetupAsync(s =>
        {
            s.CustomHandler = ctx =>
            {
                ctx.Response.StatusCode = 302;
                ctx.Response.Headers.Location = $"{other.BaseUrl}/code/";
                return true;
            };
        });

        await using (server)
        using (client)
        {
            var ex = await Assert.ThrowsAsync<RemoteException>(
                () => client.ListAsync("/code", TestContext.Current.CancellationToken));
            Assert.Contains("跨源重定向", ex.Message, StringComparison.Ordinal);
            Assert.Contains("泄露", ex.Hint!, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task RateLimiterAppliesAtClientLevel()
    {
        var server = await MockWebDavServer.StartAsync();
        await using (server)
        {
            var config = MakeConfig(server.BaseUrl, new Dictionary<string, string?> { ["NUTSTORE_RATE_LIMIT"] = "2" });
            var (client, _) = MakeClient(config);
            using (client)
            {
                var ct = TestContext.Current.CancellationToken;
                Assert.False(await client.ExistsAsync("/code/a", ct));
                Assert.False(await client.ExistsAsync("/code/b", ct));
                await Assert.ThrowsAsync<RateLimitedException>(() => client.ExistsAsync("/code/c", ct));
                Assert.Equal(2, server.Requests.Count);
            }
        }
    }

    [Fact]
    public async Task Health_ProbesFirstAllowlistedRootNotCloudRoot()
    {
        var (server, client) = await SetupAsync(s =>
        {
            s.CustomHandler = ctx =>
            {
                if (ctx.Request.Method == "PROPFIND" && ctx.Request.Path.Value == "/dav/code")
                {
                    ctx.Response.StatusCode = 207;
                    ctx.Response.ContentType = "application/xml";
                    ctx.Response.WriteAsync("""
                        <?xml version="1.0"?><D:multistatus xmlns:D="DAV:"><D:response>
                        <D:href>/dav/code/</D:href><D:propstat><D:prop>
                        <D:resourcetype><D:collection/></D:resourcetype>
                        <D:quota-available-bytes>1073741824</D:quota-available-bytes>
                        <D:quota-used-bytes>536870912</D:quota-used-bytes>
                        </D:prop><D:status>HTTP/1.1 200 OK</D:status></D:propstat></D:response></D:multistatus>
                        """).GetAwaiter().GetResult();
                    return true;
                }

                return false;
            };
        });

        await using (server)
        using (client)
        {
            var h = await client.HealthAsync(TestContext.Current.CancellationToken);
            Assert.Equal("test@example.com", h.AuthenticatedAs);
            Assert.Equal("/code", h.ProbedPath);
            Assert.Equal(1073741824, h.Quota!.AvailableBytes);
            Assert.Equal(600, h.RateLimit.Limit);
            Assert.Equal(["/code"], h.Roots);
            Assert.False(h.ReadOnly);
            Assert.DoesNotContain(server.Requests, r => r.Path == "/");
        }
    }

    [Fact]
    public async Task Health_RefusesWhenAllowlistIsEmptyInsteadOfProbingRoot()
    {
        var (server, client) = await SetupAsync(envOverrides: new Dictionary<string, string?> { ["NUTSTORE_ROOTS"] = string.Empty });

        await using (server)
        using (client)
        {
            var before = server.Requests.Count;
            var ex = await Assert.ThrowsAsync<RemoteException>(
                () => client.HealthAsync(TestContext.Current.CancellationToken));
            Assert.Contains("白名单为空", ex.Message, StringComparison.Ordinal);
            Assert.Equal(before, server.Requests.Count);
        }
    }

    [Fact]
    public void NonHttpsHostIsRejected()
    {
        var ex = Assert.Throws<ConfigException>(() => MakeConfig("http://evil.example.com/dav"));
        Assert.Contains("必须使用 https", ex.Message, StringComparison.Ordinal);
        Assert.Contains("明文", ex.Hint!, StringComparison.Ordinal);
    }
}
