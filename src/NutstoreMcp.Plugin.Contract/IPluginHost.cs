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

    /// <summary>
    /// 接上 UI 钩子。桥在**客户端的默认 AppDomain** 里创建钩子后传进来。
    ///
    /// <para>
    /// 为什么必须这样：插件跑在独立子域里（版本隔离），而 WPF 对象**跨不了 AppDomain** ——
    /// 客户端的窗口、控件树、Application.Current 全在默认域。所以 UI 只能由默认域里的
    /// 钩子代劳，插件通过这个引用跨域调用它。
    /// </para>
    /// <para>钩子可能为 <c>null</c>（加载失败时），插件必须容忍这种情况。</para>
    /// </summary>
    void AttachUi(IUiHook? ui);
}

/// <summary>
/// UI 钩子 —— 在客户端**默认 AppDomain** 里执行，是插件触碰 WPF 的唯一途径。
///
/// <para>
/// 刻意只提供**通用操作**（枚举窗口、导出控件树、在指定容器上做操作），
/// 不包含任何策略。策略（注入到哪、注入什么）留在插件里，
/// 因为那部分要反复迭代，而钩子装在程序目录、改动成本高。
/// </para>
/// <para>
/// 所有方法都返回 JSON 字符串或简单类型 —— 跨域传复杂对象容易踩 remoting 的坑。
/// </para>
/// </summary>
public interface IUiHook
{
    /// <summary>钩子存活探测。返回运行时的简短描述。</summary>
    string Ping();

    /// <summary>枚举顶层窗口，返回 JSON 数组字符串。</summary>
    string DescribeWindows();

    /// <summary>
    /// 导出某个窗口的控件树，返回 JSON 字符串。
    /// <paramref name="typeFilter"/> 为窗口类型名的子串；<paramref name="maxDepth"/> 限制深度。
    /// </summary>
    string DescribeTree(string typeFilter, int maxDepth);

    /// <summary>
    /// 在匹配的控件里按名字/类型查找，返回 JSON 字符串。
    /// </summary>
    string FindElements(string typeFilter, string elementNameOrType, int limit);
}
