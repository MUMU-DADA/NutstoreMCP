namespace NutstoreMcp.Plugin.Contract;

/// <summary>
/// 桥 ↔ 插件之间的唯一契约。
///
/// <para>
/// <b>为什么需要独立程序集</b>：插件跑在独立的子 AppDomain 里（版本隔离），
/// 跨域调用靠 <see cref="System.MarshalByRefObject"/> 代理。透明代理上的
/// <c>GetType()</c> 返回的是 <c>MarshalByRefObject</c> 而不是真实类型，
/// 所以没法用反射调用 —— 必须有一个两边都认识的接口。
/// </para>
/// <para>
/// <b>这个接口要尽量少变</b>：程序目录里的桥引用了它，桥的更新要提权。
/// 加方法意味着桥也要跟着更新。插件主体（工具、传输、界面注入）不在这里，
/// 那些天天变的东西都在插件程序集内部。
/// </para>
/// </summary>
public interface IPluginHost
{
    /// <summary>启动插件。返回错误信息；成功时为 <c>null</c>。</summary>
    string? Start(string home);

    /// <summary>停止插件并释放资源。</summary>
    void Stop();

    bool IsRunning();

    int Port();

    string Version();

    string? LogPath();

    /// <summary>状态快照（JSON），避免跨域传复杂对象。</summary>
    string StatusJson();
}
