using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using ModelContextProtocol.Server;
using NutstoreMcp.Plugin.Contract;

namespace NutstoreMcp.Plugin;

/// <summary>
/// 插件工具集。
///
/// <para>
/// <b>当前阶段硬性只读</b>：不动本地文件、不同步、绝不改远程内容。
/// 开发期的不确定性不该有机会影响到用户的同步数据。
/// 所有工具一律 <c>ReadOnly = true, Destructive = false</c>。
/// </para>
/// <para>
/// 这里刻意只放「只有进程内才能拿到」的信息。能用 WebDAV 或本地库拿到的能力
/// 仍由独立的 MCP 服务器负责，两边不重复。
/// </para>
/// <para>
/// 界面相关的工具全部经 <see cref="IUiHook"/> 转发到客户端默认域执行 ——
/// 插件自己在子域里，碰不到 WPF。
/// </para>
/// </summary>
[McpServerToolType]
public sealed class PluginTools
{
    private const string UiUnavailable =
        "{\"available\": false, \"reason\": \"UI 钩子未接入（桥没能在客户端默认域里加载 NutstoreMcp.UiHook.dll）\"}";

    [McpServerTool(Name = "nutstore_plugin_status", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("插件自身的运行状态：版本、监听端口、宿主进程、日志位置、UI 钩子是否就绪。用于确认插件已被客户端加载。")]
    public static string Status()
    {
        var proc = Process.GetCurrentProcess();

        return JsonSerializer.Serialize(new
        {
            plugin = new
            {
                version = PluginEntry.Version,
                running = PluginEntry.IsRunning,
                port = PluginEntry.Port,
                endpoint = PluginEntry.Port > 0 ? $"http://127.0.0.1:{PluginEntry.Port}/mcp" : null,
                logPath = PluginEntry.LogPath,
                lastError = PluginEntry.LastError,
                domain = AppDomain.CurrentDomain.FriendlyName,
                uiHookAttached = PluginEntry.Ui is not null,
            },
            host = new
            {
                process = proc.ProcessName,
                pid = proc.Id,
                startedAt = proc.StartTime.ToString("yyyy-MM-dd HH:mm:ss"),
                runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                is64Bit = Environment.Is64BitProcess,
                workingSetMB = Math.Round(proc.WorkingSet64 / 1024.0 / 1024.0, 1),
            },
        }, Indented);
    }

    [McpServerTool(Name = "nutstore_client_assemblies", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("列出**插件所在子 AppDomain** 里已加载的程序集。注意：客户端自己的程序集在默认域，这里看不到它们 —— 这是版本隔离的代价，不是故障。")]
    public static string ClientAssemblies(
        [Description("按名称子串过滤，留空返回全部")] string? filter = null)
    {
        var assemblies = AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => new
            {
                name = a.GetName().Name,
                version = a.GetName().Version?.ToString(),
            })
            .Where(a => a.name is not null)
            .Where(a => string.IsNullOrWhiteSpace(filter)
                        || a.name!.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(a => a.name, StringComparer.Ordinal)
            .ToList();

        return JsonSerializer.Serialize(new
        {
            domain = AppDomain.CurrentDomain.FriendlyName,
            note = "只包含本子域的程序集；客户端的程序集在默认域",
            count = assemblies.Count,
            filter = filter ?? string.Empty,
            assemblies,
        }, Indented);
    }

    [McpServerTool(Name = "nutstore_ui_windows", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("列出客户端当前打开的 WPF 窗口（在客户端默认域里枚举）。只读，不操作界面。")]
    public static string UiWindows()
    {
        var ui = PluginEntry.Ui;
        if (ui is null)
        {
            return UiUnavailable;
        }

        try
        {
            return ui.DescribeWindows();
        }
        catch (Exception ex)
        {
            return Error(ex);
        }
    }

    [McpServerTool(Name = "nutstore_ui_tree", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("导出某个客户端窗口的控件树。用来定位设置界面该把开关注入到哪里。只读。")]
    public static string UiTree(
        [Description("窗口类型名的子串，例如 Settings 或 MainWindow")] string windowType,
        [Description("最大深度，1-40，默认 6")] int maxDepth = 6)
    {
        var ui = PluginEntry.Ui;
        if (ui is null)
        {
            return UiUnavailable;
        }

        try
        {
            return ui.DescribeTree(windowType ?? string.Empty, maxDepth);
        }
        catch (Exception ex)
        {
            return Error(ex);
        }
    }

    [McpServerTool(Name = "nutstore_ui_find", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("在客户端窗口里按类型名/控件名/文本查找控件。只读，用于定位注入点。")]
    public static string UiFind(
        [Description("窗口类型名的子串")] string windowType,
        [Description("要查找的类型名、x:Name 或文本片段")] string query,
        [Description("最多返回多少个，1-200，默认 50")] int limit = 50)
    {
        var ui = PluginEntry.Ui;
        if (ui is null)
        {
            return UiUnavailable;
        }

        try
        {
            return ui.FindElements(windowType ?? string.Empty, query ?? string.Empty, limit);
        }
        catch (Exception ex)
        {
            return Error(ex);
        }
    }

    private static string Error(Exception ex) => JsonSerializer.Serialize(new
    {
        ok = false,
        error = ex.GetType().Name,
        message = ex.Message,
    }, Indented);

    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
