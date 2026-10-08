using System.Net.Http;
using System.Reflection;
using System.Runtime.Remoting;
using System.Text;
using System.Text.Json.Nodes;

// 本地测试宿主 —— 用和程序目录里的「桥」完全相同的方式加载插件，
// 但**不碰坚果云客户端**。开发期可以随便重编、随便重启，零风险。
//
// 关键点：插件加载在**带独立配置文件的子 AppDomain** 里。
// 这不是洁癖 —— 客户端自己的 NutstoreClient.exe.config 把 System.Memory
// 重定向到 4.0.2.0，而插件依赖 4.0.5.0，同域加载必然有一边崩。
//
// 用法：
//   NutstoreMcp.Host                      加载插件并常驻（Ctrl+C 退出）
//   NutstoreMcp.Host --selftest           自检：起插件 → 走 MCP 握手 → 报告 → 退出
//   NutstoreMcp.Host --home <目录>        指定插件目录
//   NutstoreMcp.Host --same-domain        在宿主自己的 AppDomain 里加载（对照实验用）

var home = ArgValue("--home") ?? DefaultPluginHome();
var selfTest = args.Contains("--selftest");
var sameDomain = args.Contains("--same-domain");

Console.WriteLine($"插件目录  : {home}");
Console.WriteLine($"宿主运行时: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
Console.WriteLine($"加载方式  : {(sameDomain ? "宿主 AppDomain（对照）" : "独立子 AppDomain")}");
Console.WriteLine();

var pluginDll = Path.Combine(home, "NutstoreMcp.Plugin.dll");
if (!File.Exists(pluginDll))
{
    Console.Error.WriteLine($"找不到插件: {pluginDll}");
    Console.Error.WriteLine("先构建：dotnet build src/NutstoreMcp.Plugin");
    return 1;
}

// ── 桥的契约：只认 IPluginHost 上的方法 ──
NutstoreMcp.Plugin.Contract.IPluginHost host;
AppDomain? pluginDomain = null;

try
{
    if (sameDomain)
    {
        // 对照实验：不用子域，直接把插件塞进宿主自己的域
        var asm = Assembly.Load(File.ReadAllBytes(pluginDll));
        HookResolve(Path.GetDirectoryName(pluginDll)!);
        var t = asm.GetType("NutstoreMcp.Plugin.PluginDomainHost")
            ?? throw new InvalidOperationException("找不到 PluginDomainHost");
        host = (NutstoreMcp.Plugin.Contract.IPluginHost)Activator.CreateInstance(t)!;
    }
    else
    {
        var setup = new AppDomainSetup
        {
            ApplicationBase = home,
            ConfigurationFile = Path.Combine(home, "NutstoreMcp.Plugin.dll.config"),
            LoaderOptimization = LoaderOptimization.SingleDomain,
        };

        if (!File.Exists(setup.ConfigurationFile))
        {
            Console.Error.WriteLine($"缺少 {Path.GetFileName(setup.ConfigurationFile)} —— 插件的绑定重定向配置。");
            Console.Error.WriteLine("它由 MSBuild 生成（AutoGenerateBindingRedirects），确认插件构建过。");
            return 1;
        }

        pluginDomain = AppDomain.CreateDomain("NutstoreMcp.Plugin", null, setup);

        // 跨域代理必须经接口转换 —— 透明代理上的 GetType() 返回 MarshalByRefObject，
        // 反射拿不到真实类型。
        host = (NutstoreMcp.Plugin.Contract.IPluginHost)pluginDomain.CreateInstanceAndUnwrap(
            "NutstoreMcp.Plugin",
            "NutstoreMcp.Plugin.PluginDomainHost");
    }

    // 注意：透明代理上的 GetType() 返回的是 MarshalByRefObject，不是真实类型。
    // 跨域调用**必须**经共享接口（IPluginHost），反射拿不到真实类型。
    Console.WriteLine($"契约接口  : {typeof(NutstoreMcp.Plugin.Contract.IPluginHost).FullName}");
    Console.WriteLine($"所在域    : {(pluginDomain is null ? AppDomain.CurrentDomain.FriendlyName : pluginDomain.FriendlyName)}");
}
catch (Exception ex)
{
    Console.Error.WriteLine($"创建插件句柄失败: {ex.GetType().Name}: {ex.Message}");
    return 1;
}

Console.WriteLine($"插件版本  : {host.Version()}");
Console.WriteLine("启动中…");

string? startError;
try
{
    startError = host.Start(home);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"启动抛异常: {ex.GetType().Name}: {ex.Message}");
    return 1;
}

var running = host.IsRunning();
var port = host.Port();
var logPath = host.LogPath();

Console.WriteLine($"运行中    : {running}");
Console.WriteLine($"端口      : {port}");
Console.WriteLine($"日志      : {logPath}");
Console.WriteLine();

if (!running)
{
    Console.Error.WriteLine($"插件没有启动成功：{startError ?? "(无错误信息)"}");
    Console.Error.WriteLine($"日志：{logPath}");
    return 1;
}

try
{
    return selfTest ? await SelfTestAsync(port) : await HoldAsync();
}
finally
{
    try
    {
        host.Stop();
    }
    catch (Exception)
    {
        // 退出路径，尽力而为
    }

    if (pluginDomain is not null)
    {
        AppDomain.Unload(pluginDomain);
    }
}

async Task<int> HoldAsync()
{
    Console.WriteLine("插件常驻中。MCP 端点：");
    Console.WriteLine($"  http://127.0.0.1:{port}/mcp");
    Console.WriteLine("Ctrl+C 退出。");
    await Task.Delay(Timeout.Infinite);
    return 0;
}

async Task<int> SelfTestAsync(int p)
{
    Console.WriteLine("══ 自检：直接对插件端点走 MCP 握手 ══");
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    var url = $"http://127.0.0.1:{p}/mcp";

    async Task<JsonNode?> Rpc(string method, JsonNode? prms, string? id)
    {
        var msg = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method };
        if (id is not null)
        {
            msg["id"] = id;
        }

        if (prms is not null)
        {
            msg["params"] = prms;
        }

        var body = new StringContent(msg.ToJsonString(), Encoding.UTF8, "application/json");
        var resp = await http.PostAsync(url, body);
        var text = await resp.Content.ReadAsStringAsync();

        if (resp.StatusCode == System.Net.HttpStatusCode.Accepted || string.IsNullOrWhiteSpace(text))
        {
            // 通知（无 id）按规范回 202 且**没有响应体** —— 不能拿它去解析 JSON
            return null;
        }

        if (!resp.IsSuccessStatusCode)
        {
            Console.WriteLine($"  ❌ {method} → HTTP {(int)resp.StatusCode}: {text}");
            return null;
        }

        return JsonNode.Parse(text);
    }

    // 1. initialize
    var init = await Rpc("initialize", new JsonObject
    {
        ["protocolVersion"] = "2025-06-18",
        ["capabilities"] = new JsonObject(),
        ["clientInfo"] = new JsonObject { ["name"] = "host-selftest", ["version"] = "1.0" },
    }, "1");

    var serverName = init?["result"]?["serverInfo"]?["name"]?.ToString();
    var protocol = init?["result"]?["protocolVersion"]?.ToString();
    if (serverName is null)
    {
        Console.WriteLine($"  ❌ initialize 失败: {init?.ToJsonString()}");
        return 1;
    }

    Console.WriteLine($"  ✅ initialize → {serverName} (协议 {protocol})");

    // 2. notifications/initialized（通知，应回 202）
    await Rpc("notifications/initialized", null, null);
    Console.WriteLine("  ✅ notifications/initialized 已投递");

    // 3. tools/list
    var list = await Rpc("tools/list", new JsonObject(), "2");
    var tools = list?["result"]?["tools"]?.AsArray();
    if (tools is null)
    {
        Console.WriteLine($"  ❌ tools/list 失败: {list?.ToJsonString()}");
        return 1;
    }

    Console.WriteLine($"  ✅ tools/list → {tools.Count} 个工具");
    foreach (var t in tools)
    {
        var ro = t?["annotations"]?["readOnlyHint"]?.GetValue<bool>() == true;
        Console.WriteLine($"       · {t?["name"],-32} [{(ro ? "只读" : "可写")}]");
    }

    // 4. 实际调用工具
    var call = await Rpc("tools/call", new JsonObject
    {
        ["name"] = "nutstore_plugin_status",
        ["arguments"] = new JsonObject(),
    }, "3");

    var text = call?["result"]?["content"]?.AsArray()?.FirstOrDefault()?["text"]?.ToString();
    if (text is null)
    {
        Console.WriteLine($"  ❌ tools/call 失败: {call?.ToJsonString()}");
        return 1;
    }

    Console.WriteLine("  ✅ tools/call → nutstore_plugin_status");
    foreach (var line in text.Split('\n').Take(20))
    {
        Console.WriteLine($"       {line.TrimEnd()}");
    }

    Console.WriteLine();
    Console.WriteLine("══ 自检通过 ══");
    return 0;
}

string? ArgValue(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

// 同域对照实验时用：让插件的依赖能从插件目录解析
void HookResolve(string dir)
{
    AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
    {
        var simpleName = new AssemblyName(e.Name).Name;
        if (simpleName is null)
        {
            return null;
        }

        var candidate = Path.Combine(dir, simpleName + ".dll");
        if (!File.Exists(candidate))
        {
            return null;
        }

        try
        {
            return Assembly.Load(File.ReadAllBytes(candidate));
        }
        catch (Exception)
        {
            return null;
        }
    };
}

static string DefaultPluginHome()
{
    var dir = AppContext.BaseDirectory;
    for (var i = 0; i < 8 && !string.IsNullOrEmpty(dir); i++)
    {
        var candidate = Path.Combine(dir, "src", "NutstoreMcp.Plugin", "bin", "Debug");
        if (Directory.Exists(candidate))
        {
            return candidate;
        }

        dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
    }

    return Path.Combine(AppContext.BaseDirectory, "plugin");
}
