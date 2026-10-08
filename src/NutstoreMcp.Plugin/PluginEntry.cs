using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using NutstoreMcp.Plugin.Contract;
using NutstoreMcp.Plugin.Transport;

namespace NutstoreMcp.Plugin;

/// <summary>
/// 插件入口。程序目录里的桥和本地测试宿主都只认这一对方法。
///
/// <para>
/// <b>契约刻意做到最窄</b>：只有 <see cref="Start(string)"/> 和 <see cref="Stop"/>，
/// 参数只有「插件目录」。桥装在 <c>C:\Program Files</c> 里、改动要提权，
/// 而插件主体在项目目录里天天重编 —— 契约窄才能保证桥常年不用动。
/// </para>
/// <para>
/// 所有异常都在这里兜住：这是跑在用户同步客户端进程里的代码，
/// 任何逃逸的异常都可能影响同步。
/// </para>
/// </summary>
public static class PluginEntry
{
    private static readonly object Gate = new();

    private static IHost? _host;
    private static HttpMcpServer? _http;
    private static PluginLog? _log;

    public static bool IsRunning { get; private set; }

    public static string Version =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";

    /// <summary>插件当前监听端口；未运行时为 0。</summary>
    public static int Port => _http?.Port ?? 0;

    /// <summary>日志文件路径，便于排查。</summary>
    public static string? LogPath => _log?.Path;

    /// <summary>
    /// 最近一次启动失败的原因。
    ///
    /// 存在的理由：日志本身也可能写不进去（权限、路径不可写），
    /// 那时如果只靠日志，故障就成了黑盒。这个属性让调用方**总能**拿到原因。
    /// </summary>
    public static string? LastError { get; private set; }

    /// <summary>
    /// UI 钩子 —— 由桥在**客户端默认 AppDomain** 里创建后传进来。
    ///
    /// <para>
    /// 插件在独立子域里，WPF 对象跨不了 AppDomain，所以凡是碰界面的操作
    /// 都要经这个跨域代理转给默认域执行。钩子可能为 <c>null</c>（加载失败），
    /// 调用方必须容忍。
    /// </para>
    /// </summary>
    public static IUiHook? Ui { get; private set; }

    /// <summary>插件所在目录。热重载触发文件写在它的上一级。</summary>
    public static string Home { get; private set; } = string.Empty;

    /// <summary>接上 UI 钩子。桥在插件启动后调用。</summary>
    public static void AttachUi(IUiHook? ui)
    {
        Ui = ui;
        _log?.Info(ui is null ? "UI 钩子为空 —— 界面工具不可用" : "UI 钩子已接入");
    }

    /// <summary>
    /// 请求热重载：写一个触发文件，桥看到后会卸载子域并重新加载插件。
    ///
    /// <para>
    /// 为什么用文件：插件不能卸载自己（进程不能自杀重建），必须由默认域里的桥来做。
    /// 而桥在 Main 之前加载、不能依赖 IPC 库，文件是最省事又最稳的信号。
    /// </para>
    /// <para>
    /// <b>局限性</b>：UI 钩子在默认域，重载动不了它 —— 钩子的改动仍然要重启客户端。
    /// 本方法只对插件（MCP 服务器 + 工具）有效。
    /// </para>
    /// </summary>
    public static string RequestReload()
    {
        try
        {
            var baseDir = Path.GetDirectoryName(Home.TrimEnd(Path.DirectorySeparatorChar));
            if (string.IsNullOrEmpty(baseDir))
            {
                return "无法确定触发文件位置";
            }

            var trigger = Path.Combine(baseDir, "reload.request");
            File.WriteAllText(trigger, DateTime.Now.ToString("O"), Encoding.UTF8);
            return trigger;
        }
        catch (Exception ex)
        {
            return $"写触发文件失败: {ex.GetType().Name}: {ex.Message}";
        }
    }

    /// <summary>
    /// 启动插件。<paramref name="home"/> 是插件所在目录（放 <c>plugin.json</c> 的地方）。
    /// </summary>
    public static void Start(string home)
    {
        lock (Gate)
        {
            if (IsRunning)
            {
                return;
            }

            try
            {
                LastError = null;
                Home = home;
                var config = PluginConfig.Load(home);
                _log = new PluginLog(config.LogPath);
                _log.Info($"插件启动 v{Version}，home={home}，宿主={AppDomain.CurrentDomain.FriendlyName}");

                if (!config.Enabled)
                {
                    _log.Info("配置里 enabled=false，不启动");
                    LastError = "配置里 enabled=false";
                    return;
                }

                _http = new HttpMcpServer(msg => _log.Info(msg));
                _http.Start(config.Port);
                _log.Info($"监听端口 {_http.Port}");

                _host = BuildHost(_http);
                _host.Start();

                IsRunning = true;
                _log.Info("插件就绪");
            }
            catch (Exception ex)
            {
                IsRunning = false;
                LastError = $"{ex.GetType().Name}: {ex.Message}";
                _log?.Error("启动失败，插件自行停用（客户端不受影响）", ex);

                try
                {
                    _http?.Dispose();
                }
                catch (Exception)
                {
                    // 清理失败也无所谓了
                }

                _http = null;
                _host = null;
            }
        }
    }

    public static void Stop()
    {
        lock (Gate)
        {
            if (!IsRunning)
            {
                return;
            }

            try
            {
                _log?.Info("插件停止");
                _http?.Dispose();
                _host?.StopAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
                _host?.Dispose();
            }
            catch (Exception ex)
            {
                _log?.Error("停止时出错", ex);
            }
            finally
            {
                _http = null;
                _host = null;
                IsRunning = false;
            }
        }
    }

    private static IHost BuildHost(HttpMcpServer http)
    {
        var builder = Host.CreateApplicationBuilder([]);
        builder.Logging.ClearProviders();

        builder.Services
            .AddMcpServer(options =>
            {
                options.ServerInfo = new() { Name = "nutstore-mcp", Version = Version };
                options.ServerInstructions =
                    "坚果云客户端内置的 MCP 服务器。当前为只读模式：不会同步文件、不会改动远程内容。";
            })
            .WithStreamServerTransport(http.SdkInput, http.SdkOutput)
            .WithTools<PluginTools>();

        return builder.Build();
    }
}

/// <summary>插件的运行配置，从 <c>{home}/plugin.json</c> 读。</summary>
internal sealed class PluginConfig
{
    public bool Enabled { get; private set; } = true;

    public int Port { get; private set; } = 17321;

    public string LogPath { get; private set; } = string.Empty;

    public static PluginConfig Load(string home)
    {
        var cfg = new PluginConfig
        {
            // 日志放在插件自己旁边：插件目录一定是可写的（否则插件根本装不进来），
            // 而 %LOCALAPPDATA% 在受限环境下未必能写 —— 日志写不进去会让排障变成黑盒。
            LogPath = Path.Combine(home, "logs", "plugin.log"),
        };

        try
        {
            var file = Path.Combine(home, "plugin.json");
            if (File.Exists(file))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file, Encoding.UTF8));
                var root = doc.RootElement;

                if (root.TryGetProperty("enabled", out var enabled)
                    && enabled.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    cfg.Enabled = enabled.GetBoolean();
                }

                if (root.TryGetProperty("port", out var port)
                    && port.TryGetInt32(out var p) && p is > 1024 and < 65536)
                {
                    cfg.Port = p;
                }

                if (root.TryGetProperty("logPath", out var lp) && lp.ValueKind == JsonValueKind.String)
                {
                    var v = lp.GetString();
                    if (!string.IsNullOrWhiteSpace(v))
                    {
                        cfg.LogPath = v!;
                    }
                }
            }
        }
        catch (Exception)
        {
            // 配置坏了就用默认值 —— 宁可起不来也不要抛
        }

        // 环境变量**最后**生效：用来在不干扰运行中实例的前提下跑第二个实例做测试。
        // 放在文件之后是刻意的 —— 否则会被 plugin.json 里的值覆盖掉。
        var envPort = Environment.GetEnvironmentVariable("NUTSTORE_MCP_PORT");
        if (int.TryParse(envPort, out var ep) && ep is > 1024 and < 65536)
        {
            cfg.Port = ep;
        }

        return cfg;
    }
}

/// <summary>
/// 极简文件日志。
///
/// 不接 log4net：客户端自己的 log4net 配置指向它自己的日志文件，
/// 混进去只会让排查变难。插件写自己的文件，边界清楚。
/// </summary>
internal sealed class PluginLog(string path)
{
    private readonly object _gate = new();

    public string Path { get; } = path;

    public void Info(string message) => Write("INFO ", message, null);

    public void Error(string message, Exception ex) => Write("ERROR", message, ex);

    private void Write(string level, string message, Exception? ex)
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
            if (ex is not null)
            {
                line += Environment.NewLine + ex;
            }

            lock (_gate)
            {
                File.AppendAllText(Path, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch (Exception)
        {
            // 日志写不进去也不能影响客户端
        }
    }
}
