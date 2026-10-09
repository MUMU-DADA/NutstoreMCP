using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace NutstoreMcp.Tests;

/// <summary>
/// 端到端：把编译好的 <c>nutstore-mcp.exe</c> 作为子进程拉起来，
/// 用**官方 MCP C# 客户端**走真实 stdio 协议对话。
///
/// 单元测试证明各模块自洽；这个测试证明它**能作为一个 MCP 服务器被真实客户端使用** ——
/// 包括最容易翻车的一点：stdout 上不能出现任何非协议内容。
/// </summary>
public class McpEndToEndTests
{
    private static string ServerExecutable()
    {
        var configuration = AppContext.BaseDirectory.Contains(
            $"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
            ? "Release"
            : "Debug";

        var exe = Path.Combine(
            TestScratch.RepoRoot(), "src", "NutstoreMcp", "bin", configuration, "net10.0-windows", "nutstore-mcp.exe");

        Assert.True(File.Exists(exe), $"找不到服务端可执行文件：{exe}（请先 dotnet build）");
        return exe;
    }

    private static async Task<McpClient> ConnectAsync(Dictionary<string, string?>? envOverrides = null)
    {
        var env = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["NUTSTORE_ROOTS"] = "我的坚果云,code",
            ["NUTSTORE_READ_ONLY"] = "true",
            ["NUTSTORE_USER"] = string.Empty,
            ["NUTSTORE_PASS"] = string.Empty,
            ["NUTSTORE_DEBUG"] = string.Empty,
            ["NUTSTORE_ENV_FILE"] = Path.Combine(TestScratch.Root, "no-such.env"),
        };

        if (envOverrides is not null)
        {
            foreach (var (k, v) in envOverrides)
            {
                env[k] = v;
            }
        }

        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "nutstore-mcp",
            Command = ServerExecutable(),
            EnvironmentVariables = env,
        });

        return await McpClient.CreateAsync(transport);
    }

    private static string TextOf(CallToolResult result)
        => string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text));

    [Fact]
    public async Task ServerExposesAllToolsAndReadsLocalState()
    {
        await using var client = await ConnectAsync();

        var tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(15, tools.Count);

        var names = tools.Select(t => t.Name).ToList();
        Assert.Contains("nutstore_list", names);
        Assert.Contains("nutstore_sync_folders", names);
        Assert.Contains("nutstore_delete", names);
        Assert.Contains("nutstore_l3_bind_candidates", names);

        // 读本地库的工具在无凭据时也应可用
        var result = await client.CallToolAsync(
            "nutstore_sync_status", new Dictionary<string, object?>(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotEqual(true, result.IsError);
        var text = TextOf(result);
        Assert.Contains("syncFolders", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PathOutsideAllowlistFailsAsToolErrorNotProtocolError()
    {
        await using var client = await ConnectAsync();

        var result = await client.CallToolAsync(
            "nutstore_list",
            new Dictionary<string, object?> { ["path"] = "/不在白名单里" },
            cancellationToken: TestContext.Current.CancellationToken);

        // 关键：会话没断，错误以工具级 IsError 返回
        Assert.True(result.IsError);
        Assert.Contains("PATH_NOT_ALLOWED", TextOf(result), StringComparison.Ordinal);

        // 会话仍然可用
        var after = await client.CallToolAsync(
            "nutstore_settings", new Dictionary<string, object?>(), cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotEqual(true, after.IsError);
    }

    [Fact]
    public async Task WriteToolsAreBlockedInReadOnlyMode()
    {
        await using var client = await ConnectAsync();

        var result = await client.CallToolAsync(
            "nutstore_mkdir",
            new Dictionary<string, object?> { ["path"] = "/code/测试目录" },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsError);
        Assert.Contains("READ_ONLY", TextOf(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingCredentialsSurfaceAsActionableError()
    {
        await using var client = await ConnectAsync();

        var result = await client.CallToolAsync(
            "nutstore_list",
            new Dictionary<string, object?> { ["path"] = "/code" },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsError);
        var text = TextOf(result);
        Assert.Contains("AUTH_FAILED", text, StringComparison.Ordinal);
        Assert.Contains("NUTSTORE_USER", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DoctorModePrintsHumanReadableReportAndNeverLeaksPassword()
    {
        var psi = new System.Diagnostics.ProcessStartInfo(ServerExecutable())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        psi.ArgumentList.Add("--doctor");
        psi.Environment["NUTSTORE_USER"] = "someone@example.com";
        psi.Environment["NUTSTORE_PASS"] = "super-secret-app-password";
        psi.Environment["NUTSTORE_ROOTS"] = "我的坚果云";
        psi.Environment["NUTSTORE_ENV_FILE"] = Path.Combine(TestScratch.Root, "no-such.env");

        using var proc = System.Diagnostics.Process.Start(psi)!;
        var stdout = await proc.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        await proc.WaitForExitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, proc.ExitCode);
        Assert.Contains("自检", stdout, StringComparison.Ordinal);
        Assert.Contains("云端白名单", stdout, StringComparison.Ordinal);
        Assert.Contains("someone@example.com", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("super-secret-app-password", stdout, StringComparison.Ordinal);
    }
}
