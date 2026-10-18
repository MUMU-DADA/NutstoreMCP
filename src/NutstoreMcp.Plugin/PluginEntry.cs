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

/// <summary>管理插件启动、配置和 MCP 服务。</summary>
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

        if (ui is not null)
        {
            StartToggleInstaller(ui);
        }
    }

    /// <summary>设置页里那个开关的标题。</summary>
    public const string ToggleTitle = "MCP 服务器";

    /// <summary>设置页里那个开关的说明文字。</summary>
    public const string ToggleSubtitle =
        "允许 AI 助手读取坚果云的同步状态。关闭后停止监听，重启客户端或切换开关可恢复。";

    /// <summary>
    /// 把设置页里的 MCP 开关注入回去。
    /// </summary>
    /// <remarks>
    /// 开关是**运行时插进设置页的 WPF 元素**，不写配置、不进 XAML，所以客户端一重启就没了。
    /// 不自动装回去的话，用户看到的就是「开关凭空消失」，而界面上没有任何地方能把它找回来。
    ///
    /// 插件启动时设置页可能还没实例化，`AddToggle` 会返回「没找到设置项容器」，
    /// 因此先密后疏地重试：前 2 分钟每 2 秒一次（覆盖正常启动），之后每 15 秒一次直到成功
    /// （覆盖「用户很久以后才第一次打开设置页」）。成功即停 —— 注入本身幂等，
    /// 但没必要反复摘了再装。
    ///
    /// 即使 MCP 服务当前是关闭的也要装：开关关着的时候，用户恰恰需要它来重新打开。
    /// </remarks>
    private static void StartToggleInstaller(IUiHook ui)
    {
        var worker = new Thread(() =>
        {
            var failures = 0;

            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    Thread.Sleep(attempt <= 60 ? 2000 : 15000);

                    var result = ui.AddToggle(ToggleTitle, ToggleSubtitle, StateFilePath, ReadEnabledState());
                    if (result is not null && result.IndexOf("\"ok\":true", StringComparison.Ordinal) >= 0)
                    {
                        _log?.Info($"设置页开关已自动恢复（第 {attempt} 次尝试）");
                        return;
                    }
                }
                catch (ThreadAbortException)
                {
                    // 子域被卸载 —— 正常关闭路径
                    return;
                }
                catch (Exception ex)
                {
                    // 客户端退出时跨域代理会抛 RemotingException；偶发失败继续试，
                    // 连续多次才放弃，避免一次抖动就永久失去自愈能力。
                    failures++;
                    _log?.Info($"自动恢复设置页开关第 {attempt} 次失败: {ex.GetType().Name}: {ex.Message}");

                    if (failures >= 5)
                    {
                        _log?.Info("自动恢复设置页开关连续失败，停止重试（可调用 nutstore_ui_install_toggle 手动装）");
                        return;
                    }
                }
            }
        })
        {
            IsBackground = true,
            Name = "NutstoreMcp.ToggleInstaller",
        };

        worker.Start();
    }

    /// <summary>
    /// 开启状态文件的监听：设置界面里的开关改的就是这个文件。
    ///
    /// <para>
    /// 用轮询而不是 FileSystemWatcher：Watcher 在网络盘、快速连续写入、
    /// 缓冲区溢出时会静默丢事件，而这里一次漏读就意味着开关失效且无人察觉。
    /// 两秒一次的读文件成本可以忽略。
    /// </para>
    /// </summary>
    public static void WatchEnabledState(string stateFile, Action<string> log)
    {
        _stateFile = stateFile;

        var worker = new Thread(() =>
        {
            bool? last = null;

            while (true)
            {
                try
                {
                    Thread.Sleep(2000);

                    var raw = File.Exists(stateFile) ? File.ReadAllText(stateFile).Trim() : "true";
                    var enabled = !string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase);

                    if (last is null)
                    {
                        // 首次轮询只记录基线，**绝不触发动作**。
                        // 少了这个分支就是死循环：插件启动 → last=null → 读到 true
                        // 当成「变了」→ 触发重载 → 插件重启 → last 又是 null → 再触发…
                        last = enabled;
                        continue;
                    }

                    if (last == enabled)
                    {
                        continue;
                    }

                    last = enabled;

                    // 不在这里自己启停 HTTP 服务，而是**触发一次插件热重载**：
                    // 重载会让插件按新的状态文件重新决定要不要起服务。
                    // 这样开关和应用状态永远一致 —— 自己管理启停要处理
                    // HttpListener 解绑、SDK 流对断开、host 生命周期一堆事，
                    // 而重载这条路已经被验证过很多次了。
                    log($"设置界面里的开关变为 {(enabled ? "开" : "关")} —— 触发插件重载");
                    RequestReload();
                }
                catch (ThreadAbortException)
                {
                    // 子域被卸载时会中止这个线程 —— 正常的关闭路径，不是错误
                    return;
                }
                catch (Exception ex)
                {
                    log($"监听开关状态出错: {ex.GetType().Name}: {ex.Message}");
                }
            }
        })
        {
            IsBackground = true,
            Name = "NutstoreMcp.EnabledWatcher",
        };

        worker.Start();
    }

    /// <summary>设置界面开关的状态文件路径。</summary>
    public static string StateFilePath => string.IsNullOrEmpty(_stateFile)
        ? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NutstoreMcp", "enabled.state")
        : _stateFile;

    /// <summary>
    /// L3 写入的解锁令牌文件。
    /// </summary>
    /// <remarks>
    /// 为什么不用环境变量：环境变量属于客户端进程，改了要重启客户端才生效 ——
    /// 而重启客户端是我们最想避免的操作。令牌文件可以随时创建、随时删除，
    /// 撤销是立刻的。
    ///
    /// 要求内容是 <c>yes</c> 而不是「文件存在」：随手 New-Item 出来的空文件
    /// 不该解锁写入。必须是刻意写下那个词。
    /// </remarks>
    public static string UnlockFilePath => Path.Combine(
        Path.GetDirectoryName(StateFilePath) ?? ".",
        "l3-write.enabled");

    /// <summary>L3 写入是否已解锁。环境变量或令牌文件任一满足即可。</summary>
    public static bool IsWriteUnlocked() => IsUnlocked(UnlockFilePath, "NUTSTORE_L3_WRITE");

    /// <summary>
    /// 系统级设置改动是否已解锁（第二把锁）。
    /// </summary>
    /// <remarks>
    /// 与普通写入分开：`EnableFileProtectionLock` 会安装文件系统驱动，
    /// `EnableSafeGuard` 影响权限验证 —— 它们做完不好撤。
    /// 共用一把锁的话，「为了改开机自启而解锁」就等于同时允许装驱动。
    /// </remarks>
    public static bool IsSystemWriteUnlocked() => IsUnlocked(SystemUnlockFilePath, "NUTSTORE_L3_WRITE_SYSTEM");

    /// <summary>系统级改动的令牌文件。默认不存在。</summary>
    public static string SystemUnlockFilePath => Path.Combine(
        Path.GetDirectoryName(StateFilePath) ?? ".",
        "l3-write-system.enabled");

    private static bool IsUnlocked(string tokenPath, string envName)
    {
        try
        {
            var env = Environment.GetEnvironmentVariable(envName);
            if (env is not null
                && (env.Trim() == "1" || env.Trim().Equals("true", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }
        catch (Exception)
        {
            // 读环境变量失败就当没设
        }

        try
        {
            return File.Exists(tokenPath)
                && File.ReadAllText(tokenPath).Trim().Equals("yes", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>读开关状态。文件不存在时视为开启（首次安装即用）。</summary>
    public static bool ReadEnabledState()
    {
        try
        {
            var path = StateFilePath;
            if (!File.Exists(path))
            {
                return true;
            }

            return !string.Equals(File.ReadAllText(path).Trim(), "false", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            // 读不到就当开着 —— 宁可多跑也不要让用户以为开关坏了
            return true;
        }
    }

    private static string _stateFile = string.Empty;

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

                // 设置界面里的开关优先于配置文件 —— 那才是用户看得到的那个开关。
                // 监听器无论如何都要起来：开关关着的时候它负责在用户打开时触发重载。
                WatchEnabledState(StateFilePath, msg => _log?.Info(msg));

                if (!ReadEnabledState())
                {
                    _log.Info("设置界面里的开关是关闭的 —— 不启动 MCP 服务");
                    LastError = "设置界面里的开关为关闭";
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
                    "坚果云客户端内置的 MCP 服务器。查询可直接使用；专用写工具默认只预演，" + "执行需要写入解锁、confirm=true、dryRun=false。移动只支持普通文件，已有目标拒绝覆盖。" + "通用桥用于开发诊断，脚本可能修改状态，删除入口禁止调用。";
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