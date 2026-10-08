using System.Globalization;
using System.Reflection;
using System.Text;
using NutstoreMcp.Plugin.Contract;

namespace NutstoreMcp.Bridge;

/// <summary>
/// 加载进 <c>NutstoreClient.exe</c> 的引导器。
///
/// <para>
/// 通过 <c>NutstoreClient.exe.config</c> 里的 <c>appDomainManagerAssembly</c> /
/// <c>appDomainManagerType</c> 挂上去，由 CLR 在 <c>Main</c> 之前实例化。
/// 这是 .NET Framework 的官方扩展点，不是注入 hack。
/// </para>
///
/// <para><b>三条铁律</b>（这里是用户的同步客户端，任何闪失都可能影响同步）：</para>
/// <list type="number">
///   <item>绝不抛异常 —— <c>InitializeNewDomain</c> 抛异常会让客户端**直接起不来**。</item>
///   <item>绝不阻塞 —— 引导工作放到后台线程，客户端照常启动。</item>
///   <item>找不到配置就什么都不做 —— 安装不完整时客户端行为与未安装完全一致。</item>
/// </list>
///
/// <para>
/// <b>为什么要有桥</b>：插件主体在项目目录里天天重编，而程序目录的改动要提权。
/// 桥只做一件事：读一个用户可写的配置文件，按里面的路径加载插件。
/// 所以装一次之后，日常迭代完全不需要再碰程序目录。
/// </para>
/// </summary>
public sealed class BridgeDomainManager : AppDomainManager
{
    /// <summary>子 AppDomain 的名字。桥在子域里会看到它，用来避免递归加载自己。</summary>
    internal const string PluginDomainName = "NutstoreMcp.Plugin";

    public override void InitializeNewDomain(AppDomainSetup appDomainInfo)
    {
        base.InitializeNewDomain(appDomainInfo);

        // 绝不抛：这里抛出去客户端就起不来了
        try
        {
            // 子 AppDomain 默认会**继承** AppDomainManager，于是桥会在子域里再被实例化一次。
            // 子域的 ApplicationBase 是插件目录，根本找不到桥程序集 —— 不加这个判断
            // 创建子域就会失败。
            if (string.Equals(AppDomain.CurrentDomain.FriendlyName, PluginDomainName, StringComparison.Ordinal))
            {
                return;
            }

            var log = new BridgeLog();
            log.Info($"桥已挂载，进程={AppDomain.CurrentDomain.FriendlyName}，CLR={Environment.Version}");

            var config = BridgeConfig.Load();

            if (!config.Enabled)
            {
                log.Info("配置里 enabled=false —— 桥不加载插件");
                return;
            }

            if (!config.IsUsable)
            {
                // 正常情况：没装插件。不吵，直接静默退出。
                log.Info($"未找到可用配置（{config.Source}）—— 桥不做任何事");
                return;
            }

            // 后台线程：不要在 Main 之前阻塞客户端启动
            var worker = new Thread(() => Boot(config, log))
            {
                IsBackground = true,
                Name = "NutstoreMcp.Bridge",
            };
            worker.Start();
        }
        catch (Exception ex)
        {
            // 连日志都可能失败，所以再兜一层
            try
            {
                new BridgeLog().Error("桥挂载失败（客户端不受影响）", ex);
            }
            catch (Exception)
            {
                // 彻底放弃
            }
        }
    }

    private static void Boot(BridgeConfig config, BridgeLog log)
    {
        AppDomain? domain = null;

        try
        {
            var pluginDll = Path.Combine(config.Home!, "NutstoreMcp.Plugin.dll");
            if (!File.Exists(pluginDll))
            {
                log.Error($"插件不存在: {pluginDll}", null!);
                return;
            }

            var configFile = Path.Combine(config.Home!, "NutstoreMcp.Plugin.dll.config");
            if (!File.Exists(configFile))
            {
                log.Error($"插件缺少绑定重定向配置: {configFile}", null!);
                return;
            }

            var setup = new AppDomainSetup
            {
                ApplicationBase = config.Home!,
                ConfigurationFile = configFile,
                LoaderOptimization = LoaderOptimization.SingleDomain,

                // 关键：清掉 AppDomainManager 的继承。
                // 否则新域会再实例化一次桥，而桥不在插件目录里，创建域会直接失败。
                AppDomainManagerAssembly = string.Empty,
                AppDomainManagerType = string.Empty,
            };

            // ── 先在**默认域**里建 UI 钩子 ──
            // WPF 对象跨不了 AppDomain，客户端的窗口全在默认域，
            // 所以碰界面的代码必须留在这边，插件通过跨域代理调用。
            var uiHook = CreateUiHook(config.Home!, log);

            domain = AppDomain.CreateDomain(PluginDomainName, null, setup);

            // 跨域代理必须经共享接口转换 —— 透明代理上的 GetType() 返回
            // MarshalByRefObject 而不是真实类型，反射拿不到真实方法。
            var host = (IPluginHost)domain.CreateInstanceAndUnwrap(
                "NutstoreMcp.Plugin",
                "NutstoreMcp.Plugin.PluginDomainHost");

            var error = host.Start(config.Home!);

            if (error is null)
            {
                log.Info($"插件已启动 v{host.Version()}，端口 {host.Port()}，日志 {host.LogPath()}");
            }
            else
            {
                log.Error($"插件启动失败: {error}", null!);
            }

            // UI 钩子要在插件起来之后再接 —— 失败也不影响 MCP 本身可用
            if (uiHook is not null)
            {
                try
                {
                    host.AttachUi(uiHook);
                    log.Info($"UI 钩子已接入: {uiHook.Ping()}");
                }
                catch (Exception ex)
                {
                    log.Error("UI 钩子接入失败（不影响 MCP 工具）", ex);
                }
            }

            // 域对象留活口：不要在这里 Unload，插件要一直跑。
            GC.KeepAlive(host);
        }
        catch (Exception ex)
        {
            log.Error("加载插件失败（客户端不受影响）", ex);

            try
            {
                if (domain is not null)
                {
                    AppDomain.Unload(domain);
                }
            }
            catch (Exception)
            {
                // 卸载失败无所谓
            }
        }
    }

    /// <summary>
    /// 在**默认 AppDomain** 里加载 UI 钩子。
    ///
    /// <para>
    /// 钩子程序集放在插件目录（不是程序目录）：它要跟着插件一起迭代，
    /// 而程序目录的写入要提权。它只引用 WPF（GAC）和契约，没有版本冲突风险，
    /// 所以可以安全地加载进默认域。
    /// </para>
    /// </summary>
    private static IUiHook? CreateUiHook(string home, BridgeLog log)
    {
        try
        {
            var hookDll = Path.Combine(home, "NutstoreMcp.UiHook.dll");
            if (!File.Exists(hookDll))
            {
                log.Info($"没有 UI 钩子（{hookDll}）—— 界面相关的工具会不可用");
                return null;
            }

            // 按字节加载：不锁文件，插件目录可以随时重编
            var asm = Assembly.Load(File.ReadAllBytes(hookDll));
            var type = asm.GetType("NutstoreMcp.UiHook.UiHook", throwOnError: false);
            if (type is null)
            {
                log.Error("钩子程序集里找不到 NutstoreMcp.UiHook.UiHook", null!);
                return null;
            }

            var hook = (IUiHook?)Activator.CreateInstance(type);
            log.Info($"UI 钩子已加载（默认域）");
            return hook;
        }
        catch (Exception ex)
        {
            log.Error("UI 钩子加载失败（不影响 MCP 工具）", ex);
            return null;
        }
    }
}

/// <summary>
/// 桥的配置。刻意用最简单的 <c>key=value</c> 而不是 JSON ——
/// 桥在 Main 之前加载，不能用任何额外依赖。
/// </summary>
internal sealed class BridgeConfig
{
    public bool Enabled { get; private set; } = true;

    /// <summary>插件目录；未配置时为 null。</summary>
    public string? Home { get; private set; }

    /// <summary>配置来源，仅用于日志。</summary>
    public string Source { get; private set; } = "(无)";

    public bool IsUsable => Enabled && !string.IsNullOrWhiteSpace(Home) && Directory.Exists(Home);

    /// <summary>用户可写位置的配置文件 —— 放这里是为了改配置不需要提权。</summary>
    public static string UserConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NutstoreMcp", "bridge.ini");

    /// <summary>桥自己旁边的配置文件，作为兜底。</summary>
    private static string LocalConfigPath
    {
        get
        {
            try
            {
                var dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                return string.IsNullOrEmpty(dir) ? string.Empty : Path.Combine(dir, "NutstoreMcp.Bridge.ini");
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }
    }

    public static BridgeConfig Load()
    {
        var cfg = new BridgeConfig();

        // 优先级：环境变量 > 用户可写配置 > 桥旁边的配置
        var envHome = Environment.GetEnvironmentVariable("NUTSTORE_MCP_HOME");
        if (!string.IsNullOrWhiteSpace(envHome))
        {
            cfg.Home = envHome!.Trim();
            cfg.Source = "环境变量 NUTSTORE_MCP_HOME";
            return cfg;
        }

        foreach (var path in new[] { UserConfigPath, LocalConfigPath })
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                continue;
            }

            try
            {
                foreach (var raw in File.ReadAllLines(path, Encoding.UTF8))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line[0] is '#' or ';' or '[')
                    {
                        continue;
                    }

                    var eq = line.IndexOf('=');
                    if (eq <= 0)
                    {
                        continue;
                    }

                    var key = line.Substring(0, eq).Trim();
                    var value = line.Substring(eq + 1).Trim().Trim('"');

                    switch (key.ToLowerInvariant())
                    {
                        case "home":
                            cfg.Home = value;
                            break;
                        case "enabled":
                            cfg.Enabled = !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase)
                                          && value != "0";
                            break;
                    }
                }

                cfg.Source = path;
                return cfg;
            }
            catch (Exception)
            {
                // 配置读不了就当没有
            }
        }

        return cfg;
    }
}

/// <summary>
/// 桥的日志。写在用户目录而不是程序目录 —— 程序目录不可写。
/// </summary>
internal sealed class BridgeLog
{
    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NutstoreMcp", "logs", "bridge.log");

    public void Info(string message) => Write("INFO ", message, null);

    public void Error(string message, Exception? ex) => Write("ERROR", message, ex);

    private void Write(string level, string message, Exception? ex)
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var line = string.Format(
                CultureInfo.InvariantCulture,
                "{0:yyyy-MM-dd HH:mm:ss.fff} [{1}] {2}",
                DateTime.Now, level, message);

            if (ex is not null)
            {
                line += Environment.NewLine + ex;
            }

            File.AppendAllText(_path, line + Environment.NewLine, Encoding.UTF8);
        }
        catch (Exception)
        {
            // 日志失败绝不能影响客户端
        }
    }
}
