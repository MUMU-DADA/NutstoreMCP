using System.Reflection;
using System.Text.Json;
using NutstoreMcp.Plugin.Contract;

namespace NutstoreMcp.Plugin;

/// <summary>
/// 跨 AppDomain 的句柄。
///
/// <para>
/// 插件不能直接加载进 <c>NutstoreClient.exe</c> 的默认 AppDomain：插件依赖
/// <c>System.Memory 4.0.5.0</c> / <c>Microsoft.Extensions.* 10.x</c>，而客户端自己的
/// <c>NutstoreClient.exe.config</c> 已经把 <c>System.Memory</c> 重定向到 4.0.2.0。
/// 同域加载只能二选一 —— 要么插件炸，要么客户端自己的代码炸。
/// </para>
/// <para>
/// 所以桥会创建一个**带独立配置文件的子 AppDomain**，把插件加载在那里，
/// 再通过这个 <see cref="MarshalByRefObject"/> 跨域调用。附带好处：
/// 子 AppDomain 可以卸载重载，改插件不必重启客户端。
/// </para>
/// </summary>
public sealed class PluginDomainHost : MarshalByRefObject, IPluginHost
{
    /// <summary>跨域句柄不过期。默认租约 5 分钟会把长驻插件悄悄断掉。</summary>
    public override object InitializeLifetimeService() => null!;

    /// <summary>启动插件，返回错误信息（成功时为 null）。</summary>
    public string? Start(string home)
    {
        PluginEntry.Start(home);
        return PluginEntry.LastError;
    }

    public void Stop() => PluginEntry.Stop();

    public bool IsRunning() => PluginEntry.IsRunning;

    public int Port() => PluginEntry.Port;

    public string Version() => PluginEntry.Version;

    public string? LogPath() => PluginEntry.LogPath;

    /// <summary>
    /// 接上 UI 钩子。<paramref name="ui"/> 是桥在默认域里创建的对象 ——
    /// 跨域传进来时会自动变成透明代理。
    /// </summary>
    public void AttachUi(IUiHook? ui) => PluginEntry.AttachUi(ui);

    /// <summary>把状态打包成 JSON，避免跨域传复杂对象。</summary>
    public string StatusJson()
    {
        var proc = System.Diagnostics.Process.GetCurrentProcess();

        return JsonSerializer.Serialize(new
        {
            version = PluginEntry.Version,
            running = PluginEntry.IsRunning,
            port = PluginEntry.Port,
            logPath = PluginEntry.LogPath,
            lastError = PluginEntry.LastError,
            domainName = AppDomain.CurrentDomain.FriendlyName,
            configFile = AppDomain.CurrentDomain.SetupInformation.ConfigurationFile,
            process = proc.ProcessName,
            pid = proc.Id,
            runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// 试加载一个客户端类型，验证「子 AppDomain 里能不能拿到客户端的程序集」。
    /// 返回类型的完整名称清单，或错误说明。
    /// </summary>
    public string ProbeClientTypes(string assemblyName, string filter)
    {
        try
        {
            var asm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => string.Equals(a.GetName().Name, assemblyName, StringComparison.OrdinalIgnoreCase));

            if (asm is null)
            {
                var dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".";
                var candidate = Path.Combine(dir, assemblyName + ".dll");
                if (!File.Exists(candidate))
                {
                    return $"未加载且文件不存在: {candidate}";
                }

                asm = Assembly.LoadFrom(candidate);
            }

            var types = asm.GetTypes()
                .Where(t => string.IsNullOrEmpty(filter) || t.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .Select(t => t.FullName)
                .Take(50)
                .ToList();

            return JsonSerializer.Serialize(new { assembly = asm.FullName, count = types.Count, types },
                new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            return $"{ex.GetType().Name}: {ex.Message}";
        }
    }
}
