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

    /// <summary>
    /// 把**所有**窗口的完整控件树导出到一个文件，返回文件路径（JSON 包装）。
    ///
    /// <para>
    /// 存在的理由：钩子活在默认域、装进去卸不掉，改一次就要重启客户端。
    /// 所以每次重启都该拿到**完整**材料，而不是每次只探一个点 ——
    /// 拿到文件后可以离线反复分析，不必再来一轮。
    /// </para>
    /// </summary>
    string DumpAll(string directory);

    /// <summary>
    /// 在客户端的设置页里放一个开关行。
    ///
    /// <para>
    /// 幂等：先把同名的旧项摘掉再插，重复调用不会出现两行。
    /// 开关状态变化时写进 <paramref name="stateFile"/>（内容为 true/false），
    /// 插件靠监听这个文件来决定启停 MCP 服务 —— 跨域没法直接回调。
    /// </para>
    /// <para>
    /// 这是**扩展界面**，不是 GUI 自动化：不模拟点击、不发按键，
    /// 只是往客户端的设置列表里加一个它自己原生的控件。
    /// </para>
    /// </summary>
    string AddToggle(string title, string subtitle, string stateFile, bool initialChecked);

    /// <summary>
    /// 在**默认域**里用客户端自己的 DAO 读同步文件夹绑定。
    ///
    /// <para>
    /// 为什么必须在这里做：客户端的静态初始化依赖 <c>Assembly.GetEntryAssembly()</c>，
    /// 而它在非默认域里恒为 <c>null</c>（CLR 固有行为，已实测 —— 把入口 exe 加载进子域
    /// 也没用）。所以插件子域里一调就炸在 <c>Path.NormalizeLongPath("")</c>。
    /// </para>
    /// <para>
    /// <b>数据安全</b>：调用前先把数据库连 <c>-wal</c>/<c>-shm</c> 复制一份，
    /// 让 DAO 读副本。真实数据库全程不被这个进程打开，副本用完即删。
    /// </para>
    /// <para>只读，不修改任何客户端状态。</para>
    /// </summary>
    string ReadClientSyncFolders();

    /// <summary>
    /// 把一个已存在于云端的沙箱绑定到本地目录（在默认域里执行）。
    /// </summary>
    /// <remarks>
    /// 参数刻意用平行数组而不是 JSON：钩子住客户端默认域，必须零依赖，
    /// 所以不在这里解析任何东西。跨域传 string[] / long[] 是原生支持的。
    /// 调用方（插件侧，有 System.Text.Json）负责把 all_sandbox_list 里那条记录
    /// 拆成这两个数组。
    /// </remarks>
    string BindSandbox(long sandboxId, long magic, string localPath, string[] permPaths, long[] permCaps);

    /// <summary>
    /// 在云端新建一个同步文件夹（走客户端自己的实现，不需要任何第三方凭据）。
    /// </summary>
    /// <remarks>
    /// 会话令牌由客户端内部处理：目标方法自己拼 <c>xSessionHeader</c> 并发送请求。
    /// 返回新建沙箱的身份（id / magic / 名称 / pathPerms），可直接喂给
    /// <see cref="BindSandbox"/> 完成本地绑定。
    /// </remarks>
    string CreateCloudSyncFolder(string name, string description);

    /// <summary>
    /// 列出云端某个沙箱目录下的内容（走客户端自己的实现）。
    /// </summary>
    /// <remarks>只读。返回该目录下的路径列表。</remarks>
    string ListCloudDirectory(long sandboxId, long magic, string directory);

    /// <summary>
    /// 读出客户端当前的设置项（走它自己的 IConfigureService）。
    /// </summary>
    /// <remarks>只读，不修改任何设置。</remarks>
    string ReadClientSettings();

    /// <summary>
    /// 修改一个客户端设置项（走它自己的 IConfigureService）。
    /// </summary>
    /// <remarks>
    /// 调用方必须已完成放行判断并选定风险层级；本方法只负责执行。
    /// </remarks>
    string WriteClientSetting(string name, string value);
}
