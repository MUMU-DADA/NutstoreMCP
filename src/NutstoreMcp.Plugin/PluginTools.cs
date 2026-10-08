using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using ModelContextProtocol.Server;

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
/// 这里刻意只放「只有进程内才能拿到」的信息 —— 那些能用 WebDAV 或本地库
/// 拿到的能力仍由独立的 MCP 服务器负责，两边不重复。
/// </para>
/// </summary>
[McpServerToolType]
public sealed class PluginTools
{
    [McpServerTool(Name = "nutstore_plugin_status", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("插件自身的运行状态：版本、监听端口、宿主进程、日志位置。用于确认插件已被客户端加载。")]
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
            },
            host = new
            {
                process = proc.ProcessName,
                pid = proc.Id,
                startedAt = proc.StartTime.ToString("yyyy-MM-dd HH:mm:ss"),
                runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                processArchitecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                is64Bit = Environment.Is64BitProcess,
                workingSetMB = Math.Round(proc.WorkingSet64 / 1024.0 / 1024.0, 1),
            },
        }, Indented);
    }

    [McpServerTool(Name = "nutstore_client_assemblies", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("列出宿主客户端进程里已加载的坚果云程序集。这是进程内才能拿到的信息，用来确认真进到客户端内部了。")]
    public static string ClientAssemblies(
        [Description("按名称子串过滤，留空返回全部")] string? filter = null)
    {
        var assemblies = AppDomain.CurrentDomain.GetAssemblies()
            .Select(a =>
            {
                string location;
                try
                {
                    location = a.IsDynamic ? "(动态)" : a.Location;
                }
                catch (Exception)
                {
                    location = "(不可读)";
                }

                return new
                {
                    name = a.GetName().Name,
                    version = a.GetName().Version?.ToString(),
                    location,
                };
            })
            .Where(a => a.name is not null)
            .Where(a => string.IsNullOrWhiteSpace(filter)
                        || a.name!.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(a => a.name, StringComparer.Ordinal)
            .ToList();

        return JsonSerializer.Serialize(new
        {
            count = assemblies.Count,
            filter = filter ?? string.Empty,
            assemblies,
        }, Indented);
    }

    [McpServerTool(Name = "nutstore_client_ui", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("列出客户端当前打开的 WPF 窗口。只读，不操作界面。用于确认 MCP 设置开关该注入到哪个窗口。")]
    public static string ClientWindows()
    {
        var app = System.Windows.Application.Current;
        if (app is null)
        {
            return JsonSerializer.Serialize(new
            {
                available = false,
                reason = "Application.Current 为空 —— 宿主不是 WPF 应用，或尚未初始化",
            }, Indented);
        }

        // WPF 的窗口枚举必须在 UI 线程上做
        var windows = new List<object>();
        Exception? failure = null;

        try
        {
            app.Dispatcher.Invoke(() =>
            {
                foreach (System.Windows.Window w in app.Windows)
                {
                    windows.Add(new
                    {
                        type = w.GetType().FullName,
                        title = w.Title,
                        isVisible = w.IsVisible,
                        isActive = w.IsActive,
                        width = Math.Round(w.ActualWidth),
                        height = Math.Round(w.ActualHeight),
                    });
                }
            });
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        return JsonSerializer.Serialize(new
        {
            available = failure is null,
            error = failure?.Message,
            count = windows.Count,
            windows,
        }, Indented);
    }

    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
