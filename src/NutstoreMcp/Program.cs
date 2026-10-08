using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using NutstoreMcp;

// ⚠️ 走 MCP 模式时 stdout 只属于协议。所有诊断信息一律走 stderr。

if (args.Contains("--help") || args.Contains("-h"))
{
    Console.WriteLine(HelpText);
    return 0;
}

if (args.Contains("--doctor"))
{
    return await Doctor.RunAsync();
}

AppConfig config;
try
{
    config = AppConfig.Load();
}
catch (ConfigException ex)
{
    // 配置错误不能走 stdout —— 会污染协议流
    Console.Error.WriteLine($"[nutstore-mcp] 配置错误：{ex.Message}");
    if (ex.Hint is not null)
    {
        Console.Error.WriteLine($"[nutstore-mcp] hint: {ex.Hint}");
    }

    return 2;
}

var debug = string.Equals(
    AppConfig.ProcessEnvironment().GetValueOrDefault("NUTSTORE_DEBUG"), "1", StringComparison.Ordinal);

// 传空数组：不把我们的命令行参数喂给 Host 的配置系统
var builder = Host.CreateApplicationBuilder([]);

builder.Logging.ClearProviders();
builder.Logging.AddConsole(options =>
{
    // 全部日志走 stderr —— 一个混进 stdout 的字符都会破坏整个会话
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});
builder.Logging.SetMinimumLevel(debug ? LogLevel.Debug : LogLevel.Warning);

builder.Services.AddSingleton(config);
builder.Services.AddSingleton(_ => new SlidingWindowRateLimiter(config.RateLimit, config.RateWindowMs));
builder.Services.AddSingleton(sp => new WebDavClient(config, sp.GetRequiredService<SlidingWindowRateLimiter>()));

// 用工厂注册，让容器持有并负责 Dispose
builder.Services.AddSingleton(_ => new LocalNutstoreData(config.DataDir));

builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new() { Name = "nutstore-mcp", Version = "0.2.0" };
        options.ServerInstructions = Instructions;
    })
    .WithStdioServerTransport()
    .WithTools<NutstoreTools>();

var host = builder.Build();

if (debug)
{
    var log = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("nutstore-mcp");
    log.LogDebug(
        "启动：host={Host} roots={Roots} readOnly={ReadOnly} credentials={HasCreds}",
        config.Host,
        string.Join(",", config.Roots),
        config.ReadOnly,
        config.HasCredentials);
    if (config.Roots.Count == 0)
    {
        log.LogWarning("云端白名单为空 —— 所有 L1 路径访问都会被拒绝（fail-closed）");
    }
}

await host.RunAsync();
return 0;

internal static partial class Program
{
    internal const string Instructions = """
        坚果云本地 MCP。

        能力分两层，稳定性不同：

        - L1 文件面（nutstore_list / stat / read_text / download / upload / mkdir / move / delete）
          经官方 WebDAV 接口读写云端文件。受两个硬限制约束：单次目录枚举 750 项、
          请求频率 600 次/30 分钟。因此**不要做递归全盘扫描**。
        - L2 数据面（nutstore_sync_* / nutstore_settings）
          读本地客户端数据库，只读。可回答「哪些目录在同步」「某文件同步到哪一步」
          「有没有同步失败」。

        安全约束：
        1. 云端访问受 NUTSTORE_ROOTS 白名单限制，本机文件访问受 NUTSTORE_LOCAL_ROOTS 限制。
           路径不在白名单内会被直接拒绝 —— 这是刻意的，不要尝试绕过。
        2. 只读模式（NUTSTORE_READ_ONLY=true）下所有写操作会被拒绝。
        3. 「创建同步文件夹」「把远程目录挂载到本地」「修改客户端设置」属于 L3 控制面，
           尚未实现。请不要用文件操作去模拟它们 —— 那样只会造出云端有、本地没挂载的
           孤立目录（客户端会标记为 doNotSync）。
        """;

    internal const string HelpText = """
        nutstore-mcp — 坚果云本地 MCP 服务器（L1 WebDAV 文件面 + L2 本地只读数据面）

        用法：
          nutstore-mcp             以 MCP stdio 服务运行
          nutstore-mcp --doctor    配置与连通性自检
          nutstore-mcp --help      显示本帮助

        配置见 .env.example 与 ARCHITECTURE.md。
        """;
}
