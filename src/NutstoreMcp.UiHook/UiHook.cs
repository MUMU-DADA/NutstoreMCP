using System.Globalization;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using NutstoreMcp.Plugin.Contract;

namespace NutstoreMcp.UiHook;

/// <summary>
/// UI 钩子 —— 跑在坚果云客户端的**默认 AppDomain** 里。
///
/// <para>
/// 插件本身在独立子域（版本隔离），而 WPF 对象跨不了 AppDomain。
/// 所以凡是碰界面的操作都必须在这里做，插件通过跨域代理调用本类。
/// </para>
///
/// <para><b>零依赖是硬约束</b>：本程序集被加载进客户端的默认域，那里已经有
/// 客户端自己的 System.Memory / Newtonsoft / Google.Protobuf 等一堆程序集，
/// 一旦我们引入版本不同的同名程序集，**崩的是客户端自己**。
/// 所以这里只用 mscorlib + WPF（GAC 自带），连 System.Text.Json 都不用 ——
/// JSON 靠 <see cref="Json"/> 手写。</para>
///
/// <para>
/// 设计上只提供**通用操作**：枚举窗口、导出控件树、查找控件。
/// 「注入什么、注入到哪」属于策略，留在插件里 —— 那部分要反复迭代，
/// 而本程序集跟着桥走、改动成本高。
/// </para>
///
/// <para>所有方法都在 UI 线程上执行，且**绝不抛异常**。</para>
/// </summary>
public sealed class UiHook : MarshalByRefObject, IUiHook
{
    /// <summary>跨域句柄不过期。</summary>
    public override object InitializeLifetimeService() => null!;

    public string Ping()
    {
        // 必须走 UI 线程：Application.Current.Windows 是 DispatcherObject，
        // 从桥的后台线程直接读会抛「调用线程无法访问此对象」。
        return OnUiThread(() =>
        {
            var app = Application.Current;
            return Json.Obj(
                ("ok", true),
                ("domain", AppDomain.CurrentDomain.FriendlyName),
                ("runtime", System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription),
                ("hasApplication", app is not null),
                ("windowCount", app?.Windows.Count ?? 0),
                ("wpfVersion", typeof(Window).Assembly.GetName().Version?.ToString()),
                ("hookLocation", SafeLocation()));
        });
    }

    public string DescribeWindows()
    {
        return OnUiThread(() =>
        {
            var app = Application.Current;
            if (app is null)
            {
                return Json.Obj(("available", false), ("reason", "Application.Current 为空"));
            }

            var items = new List<string>();
            foreach (Window w in app.Windows)
            {
                items.Add(Json.Obj(
                    ("type", w.GetType().FullName),
                    ("typeName", w.GetType().Name),
                    ("title", w.Title),
                    ("isVisible", w.IsVisible),
                    ("isActive", w.IsActive),
                    ("isLoaded", w.IsLoaded),
                    ("width", Math.Round(w.ActualWidth)),
                    ("height", Math.Round(w.ActualHeight)),
                    ("contentType", w.Content?.GetType().Name)));
            }

            return Json.Obj(
                ("available", true),
                ("count", items.Count),
                ("windows", Json.Array(items)));
        });
    }

    public string DescribeTree(string typeFilter, int maxDepth)
    {
        return OnUiThread(() =>
        {
            var app = Application.Current;
            if (app is null)
            {
                return Json.Obj(("available", false), ("reason", "Application.Current 为空"));
            }

            var window = FindWindow(app, typeFilter);
            if (window is null)
            {
                var open = app.Windows.Cast<Window>().Select(w => w.GetType().Name).ToList();
                return Json.Obj(
                    ("available", true),
                    ("found", false),
                    ("reason", $"没有匹配 \"{typeFilter}\" 的窗口"),
                    ("openWindows", Json.StringArray(open)));
            }

            var nodes = new List<string>();
            var truncated = false;
            var visited = new HashSet<DependencyObject>(RefComparer.Instance);
            var budget = 12000; // 防止控件树过大把响应撑爆

            // 从 **Window 本身**开始，不是 window.Content。
            // 窗口模板（标题栏、承载 adorner 的 AdornerDecorator）是 Content 的
            // **兄弟节点**，只走 Content 会把它们整块漏掉 —— MahApps 的 Flyout
            // 就挂在 AdornerLayer 上，设置面板因此一直看不见。
            Walk(window, 0, Clamp(maxDepth, 1, 40), nodes, ref budget, ref truncated, visited);

            return Json.Obj(
                ("available", true),
                ("found", true),
                ("window", window.GetType().FullName),
                ("title", window.Title),
                ("truncated", truncated),
                ("nodeCount", nodes.Count),
                ("nodes", Json.Array(nodes)));
        });
    }

    public string FindElements(string typeFilter, string elementNameOrType, int limit)
    {
        return OnUiThread(() =>
        {
            var app = Application.Current;
            if (app is null)
            {
                return Json.Obj(("available", false), ("reason", "Application.Current 为空"));
            }

            var window = FindWindow(app, typeFilter);
            if (window is null)
            {
                return Json.Obj(("available", true), ("found", false), ("reason", "找不到窗口"));
            }

            var hits = new List<string>();
            var cap = Clamp(limit, 1, 200);
            var visited = new HashSet<DependencyObject>(RefComparer.Instance);

            // 同上：从 Window 本身开始，否则窗口模板与 adorner 层里的东西搜不到
            Search(window, 0, elementNameOrType ?? string.Empty, hits, cap, visited);

            return Json.Obj(
                ("available", true),
                ("query", elementNameOrType),
                ("count", hits.Count),
                ("truncated", hits.Count >= cap),
                ("matches", Json.Array(hits)));
        });
    }

    /// <summary>
    /// 把所有窗口的完整控件树写到一个文件。
    ///
    /// <para>
    /// 钩子活在默认域、装进去卸不掉，改一次就要重启客户端。所以每次重启都必须
    /// 拿到**完整**材料 —— 拿到文件后可以离线反复分析，而不是每次只探一个点、
    /// 然后又要一轮重启。
    /// </para>
    /// </summary>
    public string DumpAll(string directory)
    {
        return OnUiThread(() =>
        {
            var app = Application.Current;
            if (app is null)
            {
                return Json.Obj(("available", false), ("reason", "Application.Current 为空"));
            }

            Directory.CreateDirectory(directory);
            var file = Path.Combine(directory, $"ui-dump-{DateTime.Now:yyyyMMdd-HHmmss}.json");

            var sb = new StringBuilder();
            sb.Append("{\"dumpedAt\":").Append(Json.Quote(DateTime.Now.ToString("O")));
            sb.Append(",\"windows\":[");

            // 全局共享的访问集：窗口的根视觉也在 PresentationSource 列表里，
            // 不共享的话同一棵树会被导两遍。
            var globalVisited = new HashSet<DependencyObject>(RefComparer.Instance);

            var first = true;
            var totalNodes = 0;

            foreach (Window w in app.Windows)
            {
                if (!first)
                {
                    sb.Append(',');
                }

                first = false;

                var nodes = new List<string>();
                var truncated = false;

                // 导出模式给足预算：这里的目的是「一次拿全」，
                // 慢一点无所谓，漏掉东西才要命。
                var budget = 200000;

                // 从 Window 本身开始 —— 只走 Content 会漏掉整个窗口模板，
                // 包括承载 Flyout / adorner 的 AdornerDecorator。
                Walk(w, 0, 60, nodes, ref budget, ref truncated, globalVisited);

                totalNodes += nodes.Count;

                sb.Append(Json.Obj(
                    ("type", w.GetType().FullName),
                    ("title", w.Title),
                    ("isVisible", w.IsVisible),
                    ("isLoaded", w.IsLoaded),
                    ("truncated", truncated),
                    ("nodeCount", nodes.Count),
                    ("nodes", Json.Array(nodes))));
            }

            // ── 兜底：把所有活跃的 PresentationSource 也走一遍 ──
            // Application.Windows 未必涵盖全部视觉树（独立 Popup、未注册的
            // HwndSource 等）。窗口的根视觉已经走过了，这里靠共享访问集跳过。
            sb.Append("],\"extraSources\":[");

            var extraFirst = true;
            var extraCount = 0;

            foreach (PresentationSource src in PresentationSource.CurrentSources)
            {
                if (src?.RootVisual is not DependencyObject root || globalVisited.Contains(root))
                {
                    continue;
                }

                var nodes = new List<string>();
                var truncated = false;
                var budget = 200000;

                Walk(root, 0, 60, nodes, ref budget, ref truncated, globalVisited);

                if (nodes.Count == 0)
                {
                    continue;
                }

                if (!extraFirst)
                {
                    sb.Append(',');
                }

                extraFirst = false;
                extraCount++;
                totalNodes += nodes.Count;

                sb.Append(Json.Obj(
                    ("type", root.GetType().FullName),
                    ("truncated", truncated),
                    ("nodeCount", nodes.Count),
                    ("nodes", Json.Array(nodes))));
            }

            sb.Append("],\"totalNodes\":").Append(totalNodes).Append('}');

            File.WriteAllText(file, sb.ToString(), Encoding.UTF8);

            return Json.Obj(
                ("ok", true),
                ("file", file),
                ("windowCount", app.Windows.Count),
                ("extraSourceCount", extraCount),
                ("totalNodes", totalNodes));
        });
    }

    /// <summary>
    /// 往客户端的设置页里插一个开关行。
    ///
    /// <para>
    /// 用的是客户端**自己的** <c>Nutstore.Client.Wpf.Controls.SettingItem</c>
    /// （<c>Mode = CheckBox</c>），模板由客户端套 —— 所以外观和原生设置项一致，
    /// 不是贴上去的异物。
    /// </para>
    /// <para>
    /// 状态变化写文件而不是回调插件：钩子在默认域、插件在子域，跨域回调要维护
    /// 反向代理和生命周期，而一个布尔值不值得。文件还能天然持久化。
    /// </para>
    /// </summary>
    public string AddToggle(string title, string subtitle, string stateFile, bool initialChecked)
    {
        return OnUiThread(() =>
        {
            var app = Application.Current;
            if (app is null)
            {
                return Json.Obj(("ok", false), ("reason", "Application.Current 为空"));
            }

            // 1. 找设置项容器：装着多个 SettingItem 的 StackPanel
            var host = FindSettingsPanel(app);
            if (host is null)
            {
                return Json.Obj(("ok", false), ("reason", "没找到设置项容器 —— 设置面板可能没打开"));
            }

            // 2. 拿到客户端自己的 SettingItem 类型
            var itemType = FindLoadedType("Nutstore.Client.Wpf.Controls.SettingItem");
            var modeType = FindLoadedType("Nutstore.Client.Wpf.Controls.SettingItemMode");
            if (itemType is null || modeType is null)
            {
                return Json.Obj(("ok", false), ("reason", "客户端程序集里找不到 SettingItem / SettingItemMode"));
            }

            // 3. 幂等：先摘掉同名的旧项
            var titleProperty = itemType.GetProperty("Title");
            var removed = 0;

            foreach (var old in host.Children.OfType<FrameworkElement>().ToList())
            {
                if (old.GetType() != itemType)
                {
                    continue;
                }

                if (string.Equals(titleProperty?.GetValue(old) as string, title, StringComparison.Ordinal))
                {
                    host.Children.Remove(old);
                    removed++;
                }
            }

            // 4. 造一个新的
            var item = Activator.CreateInstance(itemType);
            if (item is not DependencyObject dep)
            {
                return Json.Obj(("ok", false), ("reason", "SettingItem 实例化失败"));
            }

            SetProperty(item, "Mode", Enum.Parse(modeType, "CheckBox"));
            SetProperty(item, "Title", title);
            SetProperty(item, "Subtitle", subtitle);
            SetProperty(item, "IsChecked", initialChecked);

            // 初始状态先落一次盘，插件启动时才有得读
            TryWriteState(stateFile, initialChecked);

            // 5. 订阅变化 → 写文件
            var isCheckedDp = (DependencyProperty?)itemType
                .GetField("IsCheckedProperty", BindingFlags.Public | BindingFlags.Static)
                ?.GetValue(null);

            if (isCheckedDp is not null)
            {
                var descriptor = System.ComponentModel.DependencyPropertyDescriptor
                    .FromProperty(isCheckedDp, itemType);
                if (descriptor is not null)
                {
                    descriptor.AddValueChanged(item, (_, _) =>
                    {
                        try
                        {
                            TryWriteState(stateFile, (bool)itemType.GetProperty("IsChecked")!.GetValue(item)!);
                        }
                        catch (Exception)
                        {
                            // 写盘失败不影响界面
                        }
                    });
                }
            }

            host.Children.Add((UIElement)item);

            return Json.Obj(
                ("ok", true),
                ("removed", removed),
                ("hostType", host.GetType().Name),
                ("childCount", host.Children.Count),
                ("stateFile", stateFile));
        });
    }

    private static Panel? FindSettingsPanel(Application app)
    {
        foreach (Window w in app.Windows)
        {
            var found = FindSettingsPanelIn(w, 0);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private static Panel? FindSettingsPanelIn(DependencyObject node, int depth)
    {
        if (depth > 60)
        {
            return null;
        }

        // 设置项容器 = 直接装着多个 SettingItem 的 Panel
        if (node is Panel panel)
        {
            var settingItems = panel.Children.OfType<DependencyObject>()
                .Count(c => c.GetType().Name == "SettingItem");

            if (settingItems >= 3)
            {
                return panel;
            }
        }

        foreach (var child in Children(node))
        {
            var found = FindSettingsPanelIn(child, depth + 1);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private static Type? FindLoadedType(string fullName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                var t = asm.GetType(fullName, throwOnError: false);
                if (t is not null)
                {
                    return t;
                }
            }
            catch (Exception)
            {
                // 个别程序集反射会抛，跳过
            }
        }

        return null;
    }

    private static void SetProperty(object target, string name, object? value)
    {
        try
        {
            target.GetType().GetProperty(name)?.SetValue(target, value);
        }
        catch (Exception)
        {
            // 属性不存在或类型不匹配 —— 不影响其它属性
        }
    }

    private static void TryWriteState(string path, bool value)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(path, value ? "true" : "false", Encoding.UTF8);
        }
        catch (Exception)
        {
            // 界面操作不该因为写盘失败而失败
        }
    }

    /// <summary>
    /// 在默认域里用客户端自己的 DAO 读同步文件夹。
    ///
    /// <para>
    /// 客户端那些静态初始化（<c>DirectoryUtils</c>、<c>SignedInUsers</c>）依赖
    /// <c>Assembly.GetEntryAssembly()</c>，而它在非默认域里恒为 <c>null</c> ——
    /// 这是 CLR 固有行为，实测把入口 exe 加载进子域也没用。
    /// 所以这段代码只能待在默认域，也就是这里。
    /// </para>
    ///
    /// <para>
    /// <b>绝不碰真实数据库</b>：先复制（含 <c>-wal</c>/<c>-shm</c>）再让 DAO 读副本。
    /// 这是本方法唯一重要的性质 —— 它跑在客户端的域里，一旦写坏就是用户的数据。
    /// </para>
    ///
    /// <para>整体包在 try/catch 里：钩子跑在客户端进程内，异常不能漏出去。</para>
    /// </summary>
    public string ReadClientSyncFolders()
    {
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var db = Path.Combine(appData, "Nutstore", "db1", "nutstore.db");

            if (!File.Exists(db))
            {
                return Json.Obj(("ok", false), ("reason", "本地数据库不存在: " + db));
            }

            var scratch = Path.Combine(Path.GetTempPath(), "NutstoreMcp-hook-" + Guid.NewGuid().ToString("N"));

            try
            {
                Directory.CreateDirectory(scratch);
                var copy = Path.Combine(scratch, "nutstore.db");

                // WAL 模式：只拷主库会拿到旧快照甚至不一致的状态
                foreach (var suffix in new[] { "", "-wal", "-shm" })
                {
                    var src = db + suffix;
                    if (!File.Exists(src))
                    {
                        continue;
                    }

                    using (var input = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var output = new FileStream(copy + suffix, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        input.CopyTo(output);
                    }
                }

                var dao = FindClientType("NutstoreClient.DAO.NSSandboxDAO");
                if (dao is null)
                {
                    return Json.Obj(("ok", false), ("reason", "默认域里找不到 NSSandboxDAO"));
                }

                var read = dao.GetMethod("ReadAllSandboxesFromDb", BindingFlags.Public | BindingFlags.Static)
                           ?? dao.GetMethod("ReadAllSandboxesFromDb");

                if (read is null)
                {
                    return Json.Obj(("ok", false), ("reason", "ReadAllSandboxesFromDb 不存在"));
                }

                var list = read.Invoke(null, new object[] { copy }) as System.Collections.IEnumerable;
                if (list is null)
                {
                    return Json.Obj(("ok", false), ("reason", "DAO 返回非集合"));
                }

                var items = new List<string>();
                foreach (var item in list)
                {
                    if (item is null)
                    {
                        continue;
                    }

                    var t = item.GetType();
                    string? Prop(string n)
                    {
                        try
                        {
                            return t.GetProperty(n)?.GetValue(item)?.ToString();
                        }
                        catch (Exception)
                        {
                            return null;
                        }
                    }

                    var localPath = Prop("LocalAbsPath");

                    items.Add(Json.Obj(
                        ("id", Prop("ID")),
                        ("name", Prop("Name")),
                        ("localPath", localPath),
                        ("isPlaceholder", Prop("IsPlaceholder")),
                        // magic 是绑定和列目录都要用的内部标识（类似 inode），
                        // 不是凭据 —— 真正的密钥 lanSyncCipher / channelId 永不导出。
                        ("magic", Prop("Magic")),
                        ("localExists", localPath is not null && Directory.Exists(localPath))));
                }

                return Json.Obj(
                    ("ok", true),
                    ("domain", AppDomain.CurrentDomain.FriendlyName),
                    ("source", "默认域 + 客户端 NSSandboxDAO（读副本，真库未被打开）"),
                    ("count", items.Count),
                    ("sandboxes", new Json.Raw("[" + string.Join(",", items) + "]")));
            }
            finally
            {
                try
                {
                    if (Directory.Exists(scratch))
                    {
                        Directory.Delete(scratch, recursive: true);
                    }
                }
                catch (Exception)
                {
                    // 删不掉就留着
                }
            }
        }
        catch (Exception ex)
        {
            return Json.Obj(
                ("ok", false),
                ("reason", ex.GetType().Name + ": " + ex.Message),
                ("chain", Json.StringArray(Chain(ex))));
        }
    }

    /// <summary>
    /// 把已存在于云端的沙箱绑定到本地目录。
    /// </summary>
    /// <remarks>
    /// 本方法不做策略判断（那些在插件侧的 WriteGuards 里，已离线验证），
    /// 只负责执行。但输入的基本合法性仍在这里兜一道 —— 钩子跑在客户端进程内，
    /// 拿到明显不对的参数应该直接拒绝而不是交给客户端代码去炸。
    /// </remarks>
    public string BindSandbox(long sandboxId, long magic, string localPath, string[] permPaths, long[] permCaps)
    {
        try
        {
            // ── 输入兜底 ──
            if (sandboxId <= 0)
            {
                return Json.Obj(("ok", false), ("reason", "sandboxId 必须为正数"));
            }

            if (string.IsNullOrWhiteSpace(localPath))
            {
                return Json.Obj(("ok", false), ("reason", "localPath 为空"));
            }

            // fail-closed：没有权限信息就不绑定。
            // 绑定一个权限不明的沙箱比不绑定危险得多。
            if (permPaths is null || permCaps is null || permPaths.Length == 0
                || permPaths.Length != permCaps.Length)
            {
                return Json.Obj(("ok", false), ("reason", "pathPerms 缺失或长度不匹配 —— 权限不明，拒绝绑定"));
            }

            var dao = FindClientType("NutstoreClient.DAO.NSSandboxDAO");
            var sandboxType = FindClientType("NutstoreClient.Common.NSSandbox");
            var beanType = FindClientType("NutstoreClient.Common.CapsOnPathBean");

            if (dao is null || sandboxType is null || beanType is null)
            {
                return Json.Obj(
                    ("ok", false),
                    ("reason", $"默认域里缺少类型 (dao={dao is not null}, sandbox={sandboxType is not null}, bean={beanType is not null})"));
            }

            // ── 构造 NSSandbox(long id, long magic, string localAbsPath, bool allowUncPath) ──
            var sandbox = Activator.CreateInstance(
                sandboxType,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                args: new object[] { sandboxId, magic, localPath, false },
                culture: null);

            if (sandbox is null)
            {
                return Json.Obj(("ok", false), ("reason", "NSSandbox 构造失败"));
            }

            // ── 构造 List<CapsOnPathBean> ──
            // CapsOnPathBean 的构造函数是非公开的，所以用非公开 ctor + 显式设属性，
            // 而不是赌某个 JSON 库的反序列化行为。
            var beanCtor = beanType.GetConstructor(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                types: Type.EmptyTypes,
                modifiers: null);

            var pathProp = beanType.GetProperty("Path");
            var capsProp = beanType.GetProperty("Caps");

            if (beanCtor is null || pathProp is null || capsProp is null)
            {
                return Json.Obj(("ok", false), ("reason", "CapsOnPathBean 无法构造（ctor/Path/Caps 缺失）"));
            }

            var permsListType = typeof(List<>).MakeGenericType(beanType);
            var perms = (System.Collections.IList)Activator.CreateInstance(permsListType)!;

            for (var i = 0; i < permPaths.Length; i++)
            {
                var bean = beanCtor.Invoke(null);
                pathProp.SetValue(bean, permPaths[i]);

                // Caps 是枚举：从数字还原，而不是硬编码某个常量名
                capsProp.SetValue(bean, Enum.ToObject(capsProp.PropertyType, permCaps[i]));
                perms.Add(bean);
            }

            // ── 执行 ──
            var init = dao.GetMethod("InitSandbox", BindingFlags.Public | BindingFlags.Static)
                       ?? dao.GetMethod("InitSandbox");

            if (init is null)
            {
                return Json.Obj(("ok", false), ("reason", "NSSandboxDAO.InitSandbox 不存在"));
            }

            init.Invoke(null, new object[] { sandbox, perms });

            return Json.Obj(
                ("ok", true),
                ("domain", AppDomain.CurrentDomain.FriendlyName),
                ("sandboxId", sandboxId),
                ("localPath", localPath),
                ("permCount", perms.Count),
                ("note", "InitSandbox 已返回。客户端是否真正接管该目录由它自己决定 —— "
                       + "本方法不做「成功」的断言，实际生效情况请看客户端的同步文件夹列表。"));
        }
        catch (Exception ex)
        {
            return Json.Obj(
                ("ok", false),
                ("reason", ex.GetType().Name + ": " + ex.Message),
                ("chain", Json.StringArray(Chain(ex))));
        }
    }

    /// <summary>
    /// 在云端新建一个同步文件夹，走客户端自己的实现。
    /// </summary>
    /// <remarks>
    /// 不需要任何第三方凭据：目标方法自己拼 xSessionHeader 并发送请求，
    /// 会话令牌是客户端运行时的活值。
    ///
    /// 定位方式说明：这个类和它的方法都**被混淆过**（名字是哈希），
    /// 按语义名字搜永远搜不到 —— 这正是之前几轮找不到入口的原因。
    /// 上面的字面量来自反编译：全程序集只有两处引用
    /// MakeCreateSyncFolderRequest，一处在请求构造器本身，另一处就是这里。
    /// 客户端升级后这两个哈希可能变化，所以要显式处理「找不到」的情况。
    /// </remarks>
    public string CreateCloudSyncFolder(string name, string description)
    {
        try
        {
            // ── 名字先挡住明显非法的 ──
            // 这个名字会写进云端，不该靠服务端去拒绝
            if (string.IsNullOrWhiteSpace(name))
            {
                return Json.Obj(("ok", false), ("reason", "文件夹名为空"));
            }

            if (name.Length > 100)
            {
                return Json.Obj(("ok", false), ("reason", "文件夹名过长"));
            }

            if (name.IndexOfAny(new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' }) >= 0)
            {
                return Json.Obj(("ok", false), ("reason", "文件夹名含非法字符: " + name));
            }

            if (name == "." || name == "..")
            {
                return Json.Obj(("ok", false), ("reason", "文件夹名不能是 . 或 .."));
            }

            const string ApiType = "A.c3795a089d76f103f11c4fe70f38ac8ca";
            const string ApiMethod = "ccdd0d7373903386c87fb2d3077bf8ced";

            var api = FindClientType(ApiType);
            if (api is null)
            {
                return Json.Obj(
                    ("ok", false),
                    ("reason", "找不到客户端的建文件夹实现 " + ApiType + " —— 客户端多半升级了，"
                             + "需要重新反编译确认混淆名。"));
            }

            var aclType = FindClientType("Nutstore.Common.SandboxACL");
            var getDefaultPerm = aclType?.GetMethod("GetDefaultPerm", BindingFlags.Public | BindingFlags.Static);
            if (getDefaultPerm is null)
            {
                return Json.Obj(("ok", false), ("reason", "找不到 SandboxACL.GetDefaultPerm"));
            }

            var acl = getDefaultPerm.Invoke(null, null);
            if (acl is null)
            {
                return Json.Obj(("ok", false), ("reason", "SandboxACL.GetDefaultPerm 返回 null"));
            }

            var create = api.GetMethod(ApiMethod, BindingFlags.Public | BindingFlags.Static);
            if (create is null)
            {
                return Json.Obj(("ok", false), ("reason", "找不到 " + ApiMethod + "（混淆名可能变了）"));
            }

            // ccdd0d73...(string name, string desc, SandboxACL acl, bool doNotSync)
            var info = create.Invoke(null, new object[] { name, description ?? string.Empty, acl, false });
            if (info is null)
            {
                return Json.Obj(("ok", false), ("reason", "客户端返回了 null"));
            }

            // ── 取出绑定所需的身分 ──
            var infoType = info.GetType();

            object? Get(string prop)
            {
                try
                {
                    return infoType.GetProperty(prop)?.GetValue(info);
                }
                catch (Exception)
                {
                    return null;
                }
            }

            var sandboxId = Get("SandboxId");
            var magic = Get("SandboxMagic");

            // PathPerms 是 List<CapsOnPathBean>{ Path, Caps }
            var permPaths = new List<string>();
            var permCaps = new List<long>();

            if (Get("PathPerms") is System.Collections.IEnumerable perms)
            {
                foreach (var bean in perms)
                {
                    if (bean is null)
                    {
                        continue;
                    }

                    var beanType = bean.GetType();
                    try
                    {
                        var path = beanType.GetProperty("Path")?.GetValue(bean) as string;
                        var caps = beanType.GetProperty("Caps")?.GetValue(bean);

                        if (path is not null && caps is not null)
                        {
                            permPaths.Add(path);
                            permCaps.Add(Convert.ToInt64(caps, CultureInfo.InvariantCulture));
                        }
                    }
                    catch (Exception)
                    {
                        // 单条权限取不到就跳过 —— 但下面会因为为空而明确报告
                    }
                }
            }

            return Json.Obj(
                ("ok", true),
                ("domain", AppDomain.CurrentDomain.FriendlyName),
                ("name", Get("Name")?.ToString()),
                ("sandboxId", sandboxId?.ToString()),
                ("magic", magic?.ToString()),
                ("isOwner", Get("IsOwner")?.ToString()),
                ("permPaths", Json.StringArray(permPaths)),
                ("permCaps", new Json.Raw("[" + string.Join(",", permCaps) + "]")),
                ("note", "云端文件夹已创建。把 sandboxId / magic / permPaths / permCaps 喂给 "
                       + "nutstore_l3_bind 即可绑定到本地。"));
        }
        catch (Exception ex)
        {
            // TargetInvocationException 只是外壳，真正的原因在里层
            return Json.Obj(
                ("ok", false),
                ("reason", ex.GetType().Name + ": " + ex.Message),
                ("chain", Json.StringArray(Chain(ex))));
        }
    }

    /// <summary>
    /// 列出云端某个沙箱目录下的内容，走客户端自己的实现。
    /// </summary>
    /// <remarks>
    /// 只读。不需要解锁令牌。
    ///
    /// 目标是 c05097d346f069f32352068d6ff359701(SandboxIdentifier, string dir, string type)
    /// → List&lt;string&gt;：内部自动翻页拉全，返回目录下的路径。
    /// 所有真实调用者都传 type = null。
    ///
    /// 它只要 SandboxIdentifier（公开构造 id+magic），不需要本地路径 ——
    /// 所以对「云端有、本地没绑」的沙箱同样可用。
    /// </remarks>
    public string ListCloudDirectory(long sandboxId, long magic, string directory)
    {
        try
        {
            if (sandboxId <= 0 || magic <= 0)
            {
                return Json.Obj(("ok", false), ("reason", "sandboxId / magic 必须为正数"));
            }

            var dir = string.IsNullOrWhiteSpace(directory) ? "/" : directory.Trim();

            if (!dir.StartsWith("/", StringComparison.Ordinal))
            {
                return Json.Obj(("ok", false), ("reason", "云端路径必须以 / 开头: " + dir));
            }

            const string ApiType = "A.c3795a089d76f103f11c4fe70f38ac8ca";
            const string ApiMethod = "c05097d346f069f32352068d6ff359701";

            var api = FindClientType(ApiType);
            var identifierType = FindClientType("Nutstore.Common.SandboxIdentifier");

            if (api is null || identifierType is null)
            {
                return Json.Obj(
                    ("ok", false),
                    ("reason", "找不到客户端的列目录实现 —— 客户端多半升级了，需要重新反编译确认混淆名。"));
            }

            var identifier = Activator.CreateInstance(identifierType, sandboxId, magic);
            if (identifier is null)
            {
                return Json.Obj(("ok", false), ("reason", "SandboxIdentifier 构造失败"));
            }

            // 按参数类型精确取重载：同名方法有好几个
            var method = api.GetMethod(
                ApiMethod,
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                types: new[] { identifierType, typeof(string), typeof(string) },
                modifiers: null);

            if (method is null)
            {
                return Json.Obj(("ok", false), ("reason", "找不到列目录方法（混淆名可能变了）"));
            }

            var raw = method.Invoke(null, new object[] { identifier, dir, null! }) as System.Collections.IEnumerable;
            if (raw is null)
            {
                return Json.Obj(("ok", false), ("reason", "客户端返回了非集合结果"));
            }

            var paths = new List<string>();
            foreach (var item in raw)
            {
                if (item is string s)
                {
                    paths.Add(s);
                }
            }

            return Json.Obj(
                ("ok", true),
                ("domain", AppDomain.CurrentDomain.FriendlyName),
                ("directory", dir),
                ("count", paths.Count),
                ("entries", Json.StringArray(paths)));
        }
        catch (Exception ex)
        {
            return Json.Obj(
                ("ok", false),
                ("reason", ex.GetType().Name + ": " + ex.Message),
                ("chain", Json.StringArray(Chain(ex))));
        }
    }

    /// <summary>
    /// 读出客户端当前的设置项。
    /// </summary>
    /// <remarks>
    /// 只读。走客户端自己的 IConfigureService —— 它是 Prism 6.x 那套，
    /// 活服务从静态的 CommonServiceLocator.ServiceLocator.Current 取，
    /// 不需要遍历对象图。
    ///
    /// 每个设置项是 ConfigureItem&lt;T&gt;，值在它的 Value 属性上。
    /// 这里按名字动态取，不硬编码泛型参数类型 —— 客户端加新设置时不会漏。
    /// </remarks>
    public string ReadClientSettings()
    {
        try
        {
            var locatorType = FindClientType("CommonServiceLocator.ServiceLocator");
            var serviceType = FindClientType("Nutstore.Client.Wpf.Services.IConfigureService");

            if (locatorType is null || serviceType is null)
            {
                return Json.Obj(
                    ("ok", false),
                    ("reason", "找不到 ServiceLocator 或 IConfigureService"
                             + " —— 客户端多半升级了。"));
            }

            var current = locatorType.GetProperty("Current", BindingFlags.Public | BindingFlags.Static)
                ?.GetValue(null);

            if (current is null)
            {
                return Json.Obj(
                    ("ok", false),
                    ("reason", "ServiceLocator.Current 为空 —— 容器还没建好，或客户端不用这套定位器。"));
            }

            // IServiceLocator.GetInstance(Type)
            var getInstance = current.GetType().GetMethod(
                "GetInstance",
                BindingFlags.Public | BindingFlags.Instance,
                binder: null,
                types: new[] { typeof(Type) },
                modifiers: null);

            if (getInstance is null)
            {
                return Json.Obj(("ok", false), ("reason", "ServiceLocator 上没有 GetInstance(Type)"));
            }

            var service = getInstance.Invoke(current, new object[] { serviceType });
            if (service is null)
            {
                return Json.Obj(("ok", false), ("reason", "容器没能解析出 IConfigureService"));
            }

            var items = new List<string>();
            var raw = new StringBuilder();

            raw.Append('{');
            var first = true;

            foreach (var prop in serviceType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                // 先看属性的**静态类型**再决定要不要读值。
                //
                // 直接对每个属性调 GetValue 会连 Storage / Proxy 的 getter 一起调用 ——
                // 那两个不是 ConfigureItem，getter 里可能有磁盘 I/O 或其他副作用。
                // 一个标着「只读」的工具不该因为读设置而触发那些。
                var propType = prop.PropertyType;
                var isConfigureItem = propType.IsGenericType
                    && propType.GetGenericTypeDefinition().Name == "ConfigureItem`1";

                if (!isConfigureItem)
                {
                    // 只报类型，不读值
                    raw.Append(first ? string.Empty : ",")
                       .Append(Json.Quote(prop.Name))
                       .Append(":{\"type\":").Append(Json.Quote(propType.Name))
                       .Append(",\"value\":\"（未读取 —— 不是 ConfigureItem，getter 可能有副作用）\"}");
                    first = false;
                    items.Add(prop.Name);
                    continue;
                }

                object? item;
                try
                {
                    item = prop.GetValue(service);
                }
                catch (Exception ex)
                {
                    raw.Append(first ? string.Empty : ",").Append(Json.Quote(prop.Name))
                       .Append(":{\"error\":").Append(Json.Quote(ex.GetType().Name)).Append('}');
                    first = false;
                    continue;
                }

                if (item is null)
                {
                    continue;
                }

                var valueProp = item.GetType().GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);

                string rendered;
                if (valueProp is null)
                {
                    rendered = "{\"type\":" + Json.Quote(item.GetType().Name) + "}";
                }
                else
                {
                    object? value;
                    try
                    {
                        value = valueProp.GetValue(item);
                    }
                    catch (Exception ex)
                    {
                        value = "<" + ex.GetType().Name + ">";
                    }

                    rendered = "{\"value\":" + Json.Quote(value?.ToString() ?? "(null)")
                             + ",\"changed\":" + ((item.GetType().GetProperty("IsChanged")
                                 ?.GetValue(item) as bool? ?? false) ? "true" : "false")
                             + "}";
                }

                raw.Append(first ? string.Empty : ",").Append(Json.Quote(prop.Name)).Append(':').Append(rendered);
                first = false;
                items.Add(prop.Name);
            }

            raw.Append('}');

            return Json.Obj(
                ("ok", true),
                ("domain", AppDomain.CurrentDomain.FriendlyName),
                ("source", "IConfigureService（客户端自己的设置服务）"),
                ("count", items.Count),
                ("settings", new Json.Raw(raw.ToString())));
        }
        catch (Exception ex)
        {
            return Json.Obj(
                ("ok", false),
                ("reason", ex.GetType().Name + ": " + ex.Message),
                ("chain", Json.StringArray(Chain(ex))));
        }
    }

    /// <summary>
    /// 修改一个客户端设置项。
    /// </summary>
    /// <remarks>
    /// 策略判断在插件侧的 WriteGuards 里（那边可测），这里只负责执行 ——
    /// 但仍会核对改完之后的值确实变成了目标值，因为「调了没生效」和
    /// 「调了生效了」对调用方是两件完全不同的事。
    ///
    /// 目标设置的属性名必须**已经存在于** IConfigureService 上；
    /// 不接受任意字符串，避免变成通用的属性写入器。
    /// </remarks>
    public string WriteClientSetting(string name, string value)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return Json.Obj(("ok", false), ("reason", "设置项名为空"));
            }

            var locatorType = FindClientType("CommonServiceLocator.ServiceLocator");
            var serviceType = FindClientType("Nutstore.Client.Wpf.Services.IConfigureService");

            if (locatorType is null || serviceType is null)
            {
                return Json.Obj(("ok", false), ("reason", "找不到 ServiceLocator 或 IConfigureService"));
            }

            // 属性名必须真的存在于接口上
            var prop = serviceType.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (prop is null)
            {
                return Json.Obj(
                    ("ok", false),
                    ("reason", $"IConfigureService 上没有名为 {name} 的设置项"));
            }

            var current = locatorType.GetProperty("Current", BindingFlags.Public | BindingFlags.Static)
                ?.GetValue(null);
            if (current is null)
            {
                return Json.Obj(("ok", false), ("reason", "ServiceLocator.Current 为空"));
            }

            var getInstance = current.GetType().GetMethod(
                "GetInstance", BindingFlags.Public | BindingFlags.Instance,
                binder: null, types: new[] { typeof(Type) }, modifiers: null);
            if (getInstance is null)
            {
                return Json.Obj(("ok", false), ("reason", "ServiceLocator 上没有 GetInstance(Type)"));
            }

            var service = getInstance.Invoke(current, new object[] { serviceType });
            var item = service is null ? null : prop.GetValue(service);
            if (item is null)
            {
                return Json.Obj(("ok", false), ("reason", $"{name} 的当前值为 null"));
            }

            var itemType = item.GetType();
            var valueProp = itemType.GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);

            if (valueProp is null)
            {
                return Json.Obj(
                    ("ok", false),
                    ("reason", $"{name} 不是可写的 ConfigureItem（类型 {itemType.Name}）"));
            }

            // ── 解析目标值 ──
            object? target;
            try
            {
                target = ConvertToTarget(value, valueProp.PropertyType);
            }
            catch (Exception ex)
            {
                return Json.Obj(
                    ("ok", false),
                    ("reason", $"值 {value} 无法转成 {valueProp.PropertyType.Name}: {ex.Message}"));
            }

            var before = valueProp.GetValue(item)?.ToString();

            // ── 写入 ──
            // 必须走 Value 属性的 setter，不是 SetValue(T)。
            //
            // 反编译确认（ConfigureItem<T>）：
            //   public T Value { set { if (值不同) { ...通知/回调... } } }
            //   public void SetValue(T value) { 字段 = value; }      ← 只赋值
            //
            // SetValue 只写字段、不触发任何通知 —— 用它会出现「值变了但设置没生效」，
            // 而且要等重启才看得出来。属性 setter 才是界面绑定走的那条路，
            // 用它才等价于「在设置界面点一下」。
            var writeVia = "Value 属性 setter";
            var setter = valueProp.GetSetMethod();

            if (setter is not null)
            {
                valueProp.SetValue(item, target);
            }
            else
            {
                // 属性只读时才退回 SetValue —— 这种情况下没有通知可用
                var setValue = itemType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "SetValue"
                                      && m.GetParameters().Length == 1
                                      && m.GetParameters()[0].ParameterType == valueProp.PropertyType);

                if (setValue is null)
                {
                    return Json.Obj(
                        ("ok", false),
                        ("reason", $"{name} 既没有可写的 Value 属性，也没有匹配的 SetValue(T)"));
                }

                setValue.Invoke(item, new[] { target });
                writeVia = "SetValue(T)（Value 属性只读，无通知）";
            }

            // ── 持久化 ──
            // IsAutoSave 为真时客户端自己会存；否则要显式 Save。
            var isAutoSave = itemType.GetProperty("IsAutoSave")?.GetValue(item) as bool? ?? false;
            var save = itemType.GetMethod("Save", BindingFlags.Public | BindingFlags.Instance,
                binder: null, types: new[] { typeof(bool) }, modifiers: null);

            var saveCalled = false;
            string? saveError = null;

            if (!isAutoSave)
            {
                if (save is not null)
                {
                    try
                    {
                        save.Invoke(item, new object[] { true });
                        saveCalled = true;
                    }
                    catch (Exception ex)
                    {
                        // 保存失败必须报出来：内存里改了、磁盘上没改，
                        // 重启后设置会「自己变回去」，那种现象最难查
                        saveError = ex.InnerException?.Message ?? ex.Message;
                    }
                }
                else
                {
                    saveError = "没找到 Save(bool) 方法";
                }
            }

            var after = valueProp.GetValue(item)?.ToString();
            var matches = string.Equals(after, target?.ToString(), StringComparison.Ordinal);

            var warnings = new List<string>();
            if (!matches)
            {
                warnings.Add("写完之后的值与目标不一致 —— 客户端可能拒绝了这次改动。");
            }

            if (!isAutoSave && !saveCalled)
            {
                warnings.Add(
                    "改动只在内存里，**没有落盘**"
                    + (saveError is null ? "。" : $"（{saveError}）。")
                    + "重启客户端后会变回去。");
            }

            return Json.Obj(
                ("ok", true),
                ("domain", AppDomain.CurrentDomain.FriendlyName),
                ("name", name),
                ("before", before),
                ("after", after),
                ("target", value),
                ("writeVia", writeVia),
                ("autoSave", isAutoSave),
                ("saveCalled", saveCalled),
                ("saveError", saveError),
                ("persisted", isAutoSave || saveCalled),
                ("isChanged", (itemType.GetProperty("IsChanged")?.GetValue(item) as bool? ?? false).ToString()),
                ("warnings", Json.StringArray(warnings)));
        }
        catch (Exception ex)
        {
            return Json.Obj(
                ("ok", false),
                ("reason", ex.GetType().Name + ": " + ex.Message),
                ("chain", Json.StringArray(Chain(ex))));
        }
    }

    /// <summary>把字符串值转成目标属性类型。只支持设置项用到的几种。</summary>
    private static object? ConvertToTarget(string value, Type targetType)
    {
        if (targetType == typeof(bool))
        {
            // 只接受明确的真假写法，不做「非 0 即真」这种模糊判断
            if (value.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
            if (value.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
            throw new ArgumentException("布尔设置只接受 true / false");
        }

        if (targetType == typeof(string)) return value;

        if (targetType.IsEnum) return Enum.Parse(targetType, value, ignoreCase: true);

        if (targetType == typeof(int)) return int.Parse(value, CultureInfo.InvariantCulture);
        if (targetType == typeof(long)) return long.Parse(value, CultureInfo.InvariantCulture);

        // 其余类型（如 GlobalHotKey 这类结构）交给它自己的转换器
        var converter = System.ComponentModel.TypeDescriptor.GetConverter(targetType);
        if (converter is not null && converter.CanConvertFrom(typeof(string)))
        {
            return converter.ConvertFromInvariantString(value);
        }

        throw new ArgumentException($"不支持的设置项类型 {targetType.Name}");
    }

    /// <summary>
    /// 在云端按关键词搜索。只读。
    /// </summary>
    /// <remarks>
    /// 目标：c7a517b89b338a47fe91fa3ec2a0823c3(string) → SearchResponse
    ///   SearchResponse.Results : List&lt;SearchSandboxInfo&gt;
    ///     SearchSandboxInfo { ResultType, SandboxId, Children }
    ///       Children : List&lt;SearchFileInfo&gt; { Path, IsDir, ModifyTime }
    /// </remarks>
    public string SearchCloud(string keyword)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(keyword))
            {
                return Json.Obj(("ok", false), ("reason", "搜索关键词为空"));
            }

            var api = FindClientType(CloudOpsTypeName);
            if (api is null)
            {
                return Json.Obj(("ok", false), ("reason", "找不到客户端的云端操作层（版本可能变了）"));
            }

            var method = api.GetMethod(
                "c7a517b89b338a47fe91fa3ec2a0823c3",
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                types: new[] { typeof(string) },
                modifiers: null);

            if (method is null)
            {
                return Json.Obj(("ok", false), ("reason", "找不到搜索方法（混淆名可能变了）"));
            }

            var response = method.Invoke(null, new object[] { keyword });
            if (response is null)
            {
                return Json.Obj(("ok", false), ("reason", "客户端返回 null"));
            }

            var results = Prop(response, "Results") as System.Collections.IEnumerable;

            var groups = new List<string>();
            var matched = 0;
            var files = 0;

            if (results is not null)
            {
                foreach (var group in results)
                {
                    if (group is null)
                    {
                        continue;
                    }

                    matched++;

                    var children = new List<string>();
                    if (Prop(group, "Children") is System.Collections.IEnumerable kids)
                    {
                        foreach (var kid in kids)
                        {
                            if (kid is null)
                            {
                                continue;
                            }

                            files++;
                            children.Add(Json.Obj(
                                ("path", Prop(kid, "Path")?.ToString()),
                                ("isDir", (Prop(kid, "IsDir") as bool? ?? false) ? "true" : "false"),
                                ("modifyTime", Prop(kid, "ModifyTime")?.ToString())));
                        }
                    }

                    groups.Add(Json.Obj(
                        ("sandboxId", Prop(group, "SandboxId")?.ToString()),
                        ("resultType", Prop(group, "ResultType")?.ToString()),
                        ("fileCount", children.Count.ToString()),
                        ("files", new Json.Raw("[" + string.Join(",", children) + "]"))));
                }
            }

            return Json.Obj(
                ("ok", true),
                ("domain", AppDomain.CurrentDomain.FriendlyName),
                ("keyword", keyword),
                ("groupCount", matched.ToString()),
                ("fileCount", files.ToString()),
                ("groups", new Json.Raw("[" + string.Join(",", groups) + "]")));
        }
        catch (Exception ex)
        {
            // 免费账号不支持搜索 —— 这是功能限制，不是故障。
            // 直接抛 TargetInvocationException 会让调用方以为是我们的 bug。
            var text = Chain(ex);
            var joined = string.Join(" | ", text);

            // net48 没有 string.Contains(string, StringComparison)，只能用 IndexOf
            if (joined.IndexOf("DisabledForFreeUser", StringComparison.OrdinalIgnoreCase) >= 0
                || joined.IndexOf("disabled for free user", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return Json.Obj(
                    ("ok", false),
                    ("reason", "搜索是坚果云的付费功能，当前账号是免费版，服务端拒绝了这次请求。"),
                    ("errorCode", "DisabledForFreeUser"),
                    ("isFeatureLimit", true));
            }

            return Json.Obj(
                ("ok", false),
                ("reason", ex.GetType().Name + ": " + ex.Message),
                ("chain", Json.StringArray(text)));
        }
    }
    /// <remarks>
    /// 目标：cf22046b2931cec246e3a4f4b994e6c84() → UserMeteringBean
    ///   { UsedStorageSize, TotalStorageSize, FreeDownRate, FreeUpRate,
    ///     UsedUpRate, UsedDownRate, IsPaidUser, IsInTeam,
    ///     AccountExpireLeftTime, RateResetLeftMills }
    /// </remarks>
    public string ReadAccountUsage()
    {
        try
        {
            var api = FindClientType(CloudOpsTypeName);
            if (api is null)
            {
                return Json.Obj(("ok", false), ("reason", "找不到客户端的云端操作层（版本可能变了）"));
            }

            var method = api.GetMethod(
                "cf22046b2931cec246e3a4f4b994e6c84",
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                types: Type.EmptyTypes,
                modifiers: null);

            if (method is null)
            {
                return Json.Obj(("ok", false), ("reason", "找不到用量方法（混淆名可能变了）"));
            }

            var bean = method.Invoke(null, null);
            if (bean is null)
            {
                return Json.Obj(("ok", false), ("reason", "客户端返回 null"));
            }

            var used = AsLong(Prop(bean, "UsedStorageSize"));
            var total = AsLong(Prop(bean, "TotalStorageSize"));

            // 字节 → 人类可读。同时保留原始字节数，免得调用方要反推。
            //
            // 注意：免费账号的 TotalStorageSize 是 0 —— 坚果云免费版不按容量计费，
            // 而是限制每月的上传/下载流量（1GB / 3GB）。所以这里不能只报
            // 「0 B / 0 B」，那会让调用方以为读失败了。真正有意义的是 rates。
            var quotaReported = total > 0;

            var freeUp = AsLong(Prop(bean, "FreeUpRate"));
            var freeDown = AsLong(Prop(bean, "FreeDownRate"));
            var usedUp = AsLong(Prop(bean, "UsedUpRate"));
            var usedDown = AsLong(Prop(bean, "UsedDownRate"));
            var resetMills = AsLong(Prop(bean, "RateResetLeftMills"));

            return Json.Obj(
                ("ok", true),
                ("domain", AppDomain.CurrentDomain.FriendlyName),
                ("isPaidUser", (Prop(bean, "IsPaidUser") as bool? ?? false) ? "true" : "false"),
                ("isInTeam", (Prop(bean, "IsInTeam") as bool? ?? false) ? "true" : "false"),
                ("accountExpireLeftTime", Prop(bean, "AccountExpireLeftTime")?.ToString()),

                // 容量：免费账号不报，明确说出来而不是报 0
                ("storage", quotaReported
                    ? Json.Obj(
                        ("reported", "true"),
                        ("usedBytes", used.ToString()),
                        ("totalBytes", total.ToString()),
                        ("used", Human(used)),
                        ("total", Human(total)),
                        ("usedPercent", Math.Round(used * 100.0 / total, 2).ToString(CultureInfo.InvariantCulture)))
                    : Json.Obj(
                        ("reported", "false"),
                        ("note", "该账号不按容量计费，服务端没有返回容量配额。"
                               + "免费版限制的是每月流量，见 rates。"))),

                // 流量：这才是免费版真正要看的
                ("rates", Json.Obj(
                    ("upload", Json.Obj(
                        ("usedBytes", usedUp.ToString()),
                        ("quotaBytes", freeUp.ToString()),
                        ("used", Human(usedUp)),
                        ("quota", Human(freeUp)),
                        ("remaining", Human(Math.Max(0, freeUp - usedUp))))),
                    ("download", Json.Obj(
                        ("usedBytes", usedDown.ToString()),
                        ("quotaBytes", freeDown.ToString()),
                        ("used", Human(usedDown)),
                        ("quota", Human(freeDown)),
                        ("remaining", Human(Math.Max(0, freeDown - usedDown))))),
                    ("resetsInMills", resetMills.ToString()),
                    ("resetsIn", resetMills > 0
                        ? $"{Math.Round(resetMills / 86400000.0, 1).ToString(CultureInfo.InvariantCulture)} 天"
                        : null))));
        }
        catch (Exception ex)
        {
            return Json.Obj(
                ("ok", false),
                ("reason", ex.GetType().Name + ": " + ex.Message),
                ("chain", Json.StringArray(Chain(ex))));
        }
    }

    /// <summary>读对象上的公开属性，取不到就返回 null（不抛）。</summary>
    private static object? Prop(object target, string name)
    {
        try
        {
            return target.GetType()
                .GetProperty(name, BindingFlags.Public | BindingFlags.Instance)
                ?.GetValue(target);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static long AsLong(object? value)
    {
        try
        {
            return value is null ? 0L : Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }
        catch (Exception)
        {
            return 0L;
        }
    }

    /// <summary>字节数转人类可读。只用于展示，原始字节数同时保留在输出里。</summary>
    private static string Human(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        double v = bytes;
        var i = 0;

        while (v >= 1024 && i < units.Length - 1)
        {
            v /= 1024;
            i++;
        }

        return i == 0
            ? $"{bytes} B"
            : $"{Math.Round(v, 2).ToString(CultureInfo.InvariantCulture)} {units[i]}";
    }

    /// <summary>客户端云端操作层的混淆类名。名字是哈希，只能按字面量定位。</summary>
    private const string CloudOpsTypeName = "A.c3795a089d76f103f11c4fe70f38ac8ca";

    /// <summary>
    /// 读账号信息。只读。
    /// </summary>
    /// <remarks>
    /// 目标：cc244cf7442842e41dd4ffb0830aeb3f3() → UserInfoBean
    ///
    /// **注意这个 bean 用的是字段（小写），不是属性** ——
    /// nickName / userChannel / isPaidUser / usedStorageSize ...
    /// 所以不能只按属性找。
    ///
    /// webdomain 与 cometSubServer 是客户端的基础设施端点，不导出 ——
    /// 对调用方没用，且属于不该外传的内部信息。
    /// </remarks>
    public string ReadUserInfo()
    {
        try
        {
            var api = FindClientType(CloudOpsTypeName);
            if (api is null)
            {
                return Json.Obj(("ok", false), ("reason", "找不到客户端的云端操作层（版本可能变了）"));
            }

            var method = api.GetMethod(
                "cc244cf7442842e41dd4ffb0830aeb3f3",
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                types: Type.EmptyTypes,
                modifiers: null);

            if (method is null)
            {
                return Json.Obj(("ok", false), ("reason", "找不到账号信息方法（混淆名可能变了）"));
            }

            var bean = method.Invoke(null, null);
            if (bean is null)
            {
                return Json.Obj(("ok", false), ("reason", "客户端返回 null"));
            }

            var used = AsLong(Get(bean, "usedStorageSize"));
            var total = AsLong(Get(bean, "totalStorageSize"));

            return Json.Obj(
                ("ok", true),
                ("domain", AppDomain.CurrentDomain.FriendlyName),
                ("nickName", Get(bean, "nickName")?.ToString()),
                // userChannel 是不透明的账号关联标识，没有任何诊断价值 —— 不导出。
                // 和 webdomain / cometSubServer 同样的理由：不该外传的内部信息。
                ("isPaidUser", BoolText(Get(bean, "isPaidUser"))),
                ("isInTeam", BoolText(Get(bean, "isInTeam"))),
                ("onlineEdit", BoolText(Get(bean, "onlineEdit"))),
                ("accountExpireLeftTime", Get(bean, "accountExpireLeftTime")?.ToString()),
                ("storage", total > 0
                    ? Json.Obj(
                        ("reported", "true"),
                        ("used", Human(used)),
                        ("total", Human(total)),
                        ("usedPercent", Math.Round(used * 100.0 / total, 2).ToString(CultureInfo.InvariantCulture)))
                    : Json.Obj(("reported", "false"), ("note", "该账号不按容量计费，见 nutstore_l3_usage 的流量额度。"))));
        }
        catch (Exception ex)
        {
            return Json.Obj(
                ("ok", false),
                ("reason", ex.GetType().Name + ": " + ex.Message),
                ("chain", Json.StringArray(Chain(ex))));
        }
    }

    /// <summary>
    /// 读某个同步文件夹的详情与权限。只读。
    /// </summary>
    /// <remarks>
    /// 合并两个调用：
    ///   c083e3da23c3bbf0c707145cd9efea279(SandboxIdentifier) → SyncFolderDetailBean
    ///   c3c96725a92962d165d900554f645b220(SandboxIdentifier) → PathPermsBean { pathPerms }
    /// 它们要同一个入参，分开调没有意义。
    /// </remarks>
    public string ReadSyncFolderDetail(long sandboxId, long magic)
    {
        try
        {
            if (sandboxId <= 0 || magic <= 0)
            {
                return Json.Obj(("ok", false), ("reason", "sandboxId / magic 必须为正数"));
            }

            var api = FindClientType(CloudOpsTypeName);
            var idType = FindClientType("Nutstore.Common.SandboxIdentifier");

            if (api is null || idType is null)
            {
                return Json.Obj(("ok", false), ("reason", "找不到云端操作层或 SandboxIdentifier（版本可能变了）"));
            }

            var identifier = Activator.CreateInstance(idType, sandboxId, magic);
            if (identifier is null)
            {
                return Json.Obj(("ok", false), ("reason", "SandboxIdentifier 构造失败"));
            }

            var call = new object[] { identifier };

            // ── 详情 ──
            var detailMethod = api.GetMethod(
                "c083e3da23c3bbf0c707145cd9efea279",
                BindingFlags.Public | BindingFlags.Static,
                binder: null, types: new[] { idType }, modifiers: null);

            string? detailJson = null;
            string? detailError = null;

            if (detailMethod is null)
            {
                detailError = "找不到详情方法（混淆名可能变了）";
            }
            else
            {
                try
                {
                    var d = detailMethod.Invoke(null, call);
                    if (d is null)
                    {
                        detailError = "客户端返回 null";
                    }
                    else
                    {
                        detailJson = Json.Obj(
                            ("name", Get(d, "Name")?.ToString()),
                            ("desc", Get(d, "Desc")?.ToString()),
                            ("owner", Get(d, "Owner")?.ToString()),
                            ("isOwner", BoolText(Get(d, "IsOwner"))),
                            ("isDefault", BoolText(Get(d, "IsDefault"))),
                            ("exclusive", BoolText(Get(d, "Exclusive"))),
                            ("doNotSync", BoolText(Get(d, "DoNotSync"))),
                            ("isPhotoBucket", BoolText(Get(d, "IsPhotoBucket"))),
                            ("permission", Get(d, "Permission")?.ToString()),
                            ("usedStorageBytes", Get(d, "UsedStorage")?.ToString()),
                            ("usedStorage", Human(AsLong(Get(d, "UsedStorage")))));
                    }
                }
                catch (Exception ex)
                {
                    detailError = (ex.InnerException ?? ex).Message;
                }
            }

            // ── 权限 ──
            var permsMethod = api.GetMethod(
                "c3c96725a92962d165d900554f645b220",
                BindingFlags.Public | BindingFlags.Static,
                binder: null, types: new[] { idType }, modifiers: null);

            var perms = new List<string>();
            string? permsError = null;

            if (permsMethod is null)
            {
                permsError = "找不到权限方法（混淆名可能变了）";
            }
            else
            {
                try
                {
                    var p = permsMethod.Invoke(null, call);

                    // PathPermsBean 也是字段：pathPerms : List<CapsOnPathBean>{ Path, Caps }
                    if (Get(p, "pathPerms") is System.Collections.IEnumerable list)
                    {
                        foreach (var item in list)
                        {
                            if (item is null)
                            {
                                continue;
                            }

                            perms.Add(Json.Obj(
                                ("path", Get(item, "Path")?.ToString()),
                                ("caps", Get(item, "Caps")?.ToString())));
                        }
                    }
                }
                catch (Exception ex)
                {
                    permsError = (ex.InnerException ?? ex).Message;
                }
            }

            return Json.Obj(
                ("ok", detailJson is not null || perms.Count > 0),
                ("domain", AppDomain.CurrentDomain.FriendlyName),
                ("sandboxId", sandboxId.ToString(CultureInfo.InvariantCulture)),
                ("detail", detailJson is null ? null : new Json.Raw(detailJson)),
                ("detailError", detailError),
                ("permCount", perms.Count.ToString(CultureInfo.InvariantCulture)),
                ("permPaths", new Json.Raw("[" + string.Join(",", perms) + "]")),
                ("permsError", permsError));
        }
        catch (Exception ex)
        {
            return Json.Obj(
                ("ok", false),
                ("reason", ex.GetType().Name + ": " + ex.Message),
                ("chain", Json.StringArray(Chain(ex))));
        }
    }

    /// <summary>
    /// 读某个本地文件的云端历史版本列表。只读。
    /// </summary>
    /// <remarks>
    /// 目标：c2654043f830c74ecf5925237786dccea(NutstorePath) → List&lt;FileVersionObjectInfo&gt;
    ///   { Version, OpType, Operation, EditorNickname, EditorAccount,
    ///     Time, FileSize, AltPath }
    ///
    /// 为什么要三个参数而不是一个本地路径：NutstorePath 的构造函数要求
    /// **绝对的本地路径**（Path.IsPathRooted），而且它要从 NSSandbox 推出沙箱身份。
    /// 沙箱列表的读取逻辑已经在 ReadClientSyncFolders 里了，
    /// 这里不重复一遍 —— 由调用方把已经拿到的身份传进来。
    /// </remarks>
    public string ListFileVersions(long sandboxId, long magic, string localRoot, string localFilePath)
    {
        try
        {
            if (sandboxId <= 0 || magic <= 0)
            {
                return Json.Obj(("ok", false), ("reason", "sandboxId / magic 必须为正数"));
            }

            if (string.IsNullOrWhiteSpace(localRoot) || !Path.IsPathRooted(localRoot))
            {
                return Json.Obj(("ok", false), ("reason", "localRoot 必须是绝对的本地路径"));
            }

            if (string.IsNullOrWhiteSpace(localFilePath) || !Path.IsPathRooted(localFilePath))
            {
                return Json.Obj(("ok", false), ("reason", "localFilePath 必须是绝对的本地路径"));
            }

            var api = FindClientType(CloudOpsTypeName);
            var nsSandboxType = FindClientType("NutstoreClient.Common.NSSandbox");
            var nsPathType = FindClientType("NutstoreClient.Common.NutstorePath");

            if (api is null || nsSandboxType is null || nsPathType is null)
            {
                return Json.Obj(("ok", false), ("reason", "找不到云端操作层 / NSSandbox / NutstorePath（版本可能变了）"));
            }

            // NSSandbox(long id, long magic, string localAbsPath, bool allowUncPath)
            var sandbox = Activator.CreateInstance(
                nsSandboxType,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                args: new object[] { sandboxId, magic, localRoot, false },
                culture: null);

            if (sandbox is null)
            {
                return Json.Obj(("ok", false), ("reason", "NSSandbox 构造失败"));
            }

            var fromAbsolute = nsPathType.GetMethod(
                "FromAbsolutePath",
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                types: new[] { nsSandboxType, typeof(string) },
                modifiers: null);

            if (fromAbsolute is null)
            {
                return Json.Obj(("ok", false), ("reason", "找不到 NutstorePath.FromAbsolutePath"));
            }

            var nsPath = fromAbsolute.Invoke(null, new[] { sandbox, localFilePath });
            if (nsPath is null)
            {
                return Json.Obj(("ok", false), ("reason", "构造 NutstorePath 返回 null"));
            }

            var method = api.GetMethod(
                "c2654043f830c74ecf5925237786dccea",
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                types: new[] { nsPathType },
                modifiers: null);

            if (method is null)
            {
                return Json.Obj(("ok", false), ("reason", "找不到版本历史方法（混淆名可能变了）"));
            }

            var versions = method.Invoke(null, new[] { nsPath }) as System.Collections.IEnumerable;
            var items = new List<string>();
            var count = 0;

            if (versions is not null)
            {
                foreach (var v in versions)
                {
                    if (v is null)
                    {
                        continue;
                    }

                    count++;
                    var rawTime = AsLong(Get(v, "Time"));

                    // Time 的单位没有文档。实测传进 FromUnixTimeSeconds 会抛
                    // ArgumentOutOfRangeException，说明它不是秒 —— 按数量级判断，
                    // 而不是猜死一个单位（猜错就是整个工具报错，而不是少一个字段）。
                    // 253402300799 = 9999-12-31T23:59:59Z 的秒数，超过它只可能是毫秒。
                    string? timeText = null;
                    if (rawTime > 0)
                    {
                        try
                        {
                            timeText = (rawTime > 253402300799L
                                    ? DateTimeOffset.FromUnixTimeMilliseconds(rawTime)
                                    : DateTimeOffset.FromUnixTimeSeconds(rawTime))
                                .LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                        }
                        catch (Exception)
                        {
                            // 换算不了就只给原始值，不让它拖垮整次调用
                            timeText = null;
                        }
                    }

                    items.Add(Json.Obj(
                        ("version", Get(v, "Version")?.ToString()),
                        ("operation", Get(v, "Operation")?.ToString()),
                        ("opType", Get(v, "OpType")?.ToString()),
                        ("editor", Get(v, "EditorNickname")?.ToString()),
                        ("editorAccount", Get(v, "EditorAccount")?.ToString()),
                        ("hasNickname", BoolText(Get(v, "HasNickname"))),
                        ("timeRaw", rawTime.ToString(CultureInfo.InvariantCulture)),
                        ("time", timeText),
                        ("fileSize", Human(AsLong(Get(v, "FileSize")))),
                        ("altPath", Get(v, "AltPath")?.ToString())));
                }
            }

            return Json.Obj(
                ("ok", true),
                ("domain", AppDomain.CurrentDomain.FriendlyName),
                ("localFilePath", localFilePath),
                ("cloudPath", nsPath.ToString()),
                ("count", count.ToString(CultureInfo.InvariantCulture)),
                ("versions", new Json.Raw("[" + string.Join(",", items) + "]")));
        }
        catch (Exception ex)
        {
            return Json.Obj(
                ("ok", false),
                ("reason", ex.GetType().Name + ": " + ex.Message),
                ("chain", Json.StringArray(Chain(ex))));
        }
    }

    /// <summary>
    /// 由本地文件路径构造 NutstorePath。构造不出来时返回原因。
    /// </summary>
    /// <remarks>
    /// A4（版本历史）和 A7（分享状态）都要这一步，抽出来避免两份拷贝漂移。
    /// </remarks>
    private static object? BuildNutstorePath(
        long sandboxId, long magic, string localRoot, string localFilePath, out string? error)
    {
        error = null;

        var nsSandboxType = FindClientType("NutstoreClient.Common.NSSandbox");
        var nsPathType = FindClientType("NutstoreClient.Common.NutstorePath");

        if (nsSandboxType is null || nsPathType is null)
        {
            error = "找不到 NSSandbox 或 NutstorePath（版本可能变了）";
            return null;
        }

        var sandbox = Activator.CreateInstance(
            nsSandboxType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: new object[] { sandboxId, magic, localRoot, false },
            culture: null);

        if (sandbox is null)
        {
            error = "NSSandbox 构造失败";
            return null;
        }

        var fromAbsolute = nsPathType.GetMethod(
            "FromAbsolutePath",
            BindingFlags.Public | BindingFlags.Static,
            binder: null,
            types: new[] { nsSandboxType, typeof(string) },
            modifiers: null);

        if (fromAbsolute is null)
        {
            error = "找不到 NutstorePath.FromAbsolutePath";
            return null;
        }

        var nsPath = fromAbsolute.Invoke(null, new[] { sandbox, localFilePath });
        if (nsPath is null)
        {
            error = "构造 NutstorePath 返回 null";
        }

        return nsPath;
    }

    /// <summary>校验 A4 / A7 共用的四个入参。</summary>
    private static string? CheckPathArgs(long sandboxId, long magic, string localRoot, string localFilePath)
    {
        if (sandboxId <= 0 || magic <= 0)
        {
            return "sandboxId / magic 必须为正数";
        }

        if (string.IsNullOrWhiteSpace(localRoot) || !Path.IsPathRooted(localRoot))
        {
            return "localRoot 必须是绝对的本地路径";
        }

        if (string.IsNullOrWhiteSpace(localFilePath) || !Path.IsPathRooted(localFilePath))
        {
            return "localFilePath 必须是绝对的本地路径";
        }

        return null;
    }

    /// <summary>
    /// 读某个本地文件在云端的分享/发布状态。只读。
    /// </summary>
    /// <remarks>
    /// 目标：c9a9142ba46c6a4cdb6c70a448962ca9e(NutstorePath) → PublishedObjectInfo
    ///   { Url, Version, ACL, Aclist, Groups, GroupIds, ShareOutOfTeamDisabled,
    ///     DownloadDisabled, EnableUpload, ExpireMillsSinceEpoch, Password }
    ///
    /// **Password 是分享密码，不导出值** —— 只报是否设了密码。
    /// 调用方能知道「这个分享有密码保护」，但拿不到密码本身。
    /// </remarks>
    public string ReadShareInfo(long sandboxId, long magic, string localRoot, string localFilePath)
    {
        try
        {
            var argError = CheckPathArgs(sandboxId, magic, localRoot, localFilePath);
            if (argError is not null)
            {
                return Json.Obj(("ok", false), ("reason", argError));
            }

            var api = FindClientType(CloudOpsTypeName);
            if (api is null)
            {
                return Json.Obj(("ok", false), ("reason", "找不到客户端的云端操作层（版本可能变了）"));
            }

            var nsPath = BuildNutstorePath(sandboxId, magic, localRoot, localFilePath, out var pathError);
            if (nsPath is null)
            {
                return Json.Obj(("ok", false), ("reason", pathError));
            }

            var nsPathType = nsPath.GetType();
            var method = api.GetMethod(
                "c9a9142ba46c6a4cdb6c70a448962ca9e",
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                types: new[] { nsPathType },
                modifiers: null);

            if (method is null)
            {
                return Json.Obj(("ok", false), ("reason", "找不到分享状态方法（混淆名可能变了）"));
            }

            var info = method.Invoke(null, new[] { nsPath });
            if (info is null)
            {
                // 没有分享过 —— 这不是错误，是「未分享」
                return Json.Obj(
                    ("ok", true),
                    ("shared", "false"),
                    ("note", "客户端没有返回分享信息，通常表示这个文件没有分享过。"));
            }

            var expireMills = AsLong(Get(info, "ExpireMillsSinceEpoch"));
            string? expireText = null;

            if (expireMills > 0)
            {
                try
                {
                    expireText = DateTimeOffset.FromUnixTimeMilliseconds(expireMills)
                        .LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                }
                catch (Exception)
                {
                    expireText = null;
                }
            }

            var password = Get(info, "Password") as string;

            return Json.Obj(
                ("ok", true),
                ("shared", "true"),
                ("domain", AppDomain.CurrentDomain.FriendlyName),
                ("localFilePath", localFilePath),
                ("url", Get(info, "Url")?.ToString()),
                ("version", Get(info, "Version")?.ToString()),

                // 密码只报有没有，不报值
                ("hasPassword", string.IsNullOrEmpty(password) ? "false" : "true"),

                ("expireMillsSinceEpoch", expireMills.ToString(CultureInfo.InvariantCulture)),
                ("expireAt", expireText),
                ("downloadDisabled", BoolText(Get(info, "DownloadDisabled"))),
                ("uploadEnabled", BoolText(Get(info, "EnableUpload"))),
                ("shareOutOfTeamDisabled", BoolText(Get(info, "ShareOutOfTeamDisabled"))),
                ("acl", Get(info, "ACL")?.ToString()),
                ("groupIds", Get(info, "GroupIds")?.ToString()),
                ("accountCount", (Get(info, "Aclist") as System.Collections.ICollection)?.Count.ToString(CultureInfo.InvariantCulture)));
        }
        catch (Exception ex)
        {
            // 「没有分享过」客户端是**抛异常**，不是返回 null。
            // 那是正常状态不是故障 —— 报成错误会让调用方以为是我们坏了。
            var text = Chain(ex);
            var joined = string.Join(" | ", text);

            if (joined.IndexOf("ObjectNotFound", StringComparison.OrdinalIgnoreCase) >= 0
                || joined.IndexOf("pub object can not be found", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return Json.Obj(
                    ("ok", true),
                    ("shared", "false"),
                    ("reason", "这个文件没有分享过。"),
                    ("errorCode", "ObjectNotFound"));
            }

            return Json.Obj(
                ("ok", false),
                ("reason", ex.GetType().Name + ": " + ex.Message),
                ("chain", Json.StringArray(text)));
        }
    }

    /// <summary>
    /// 读协作联系人列表。只读。
    /// </summary>
    /// <remarks>
    /// 目标：c915cc8f3fd71da2191b3d1dff9bbda1f() → ContactList
    ///   { InTeam, Invited, Others }，每项是 List&lt;Contact&gt; { NickName, Account }
    /// </remarks>
    public string ReadContacts()
    {
        try
        {
            var api = FindClientType(CloudOpsTypeName);
            if (api is null)
            {
                return Json.Obj(("ok", false), ("reason", "找不到客户端的云端操作层（版本可能变了）"));
            }

            var method = api.GetMethod(
                "c915cc8f3fd71da2191b3d1dff9bbda1f",
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                types: Type.EmptyTypes,
                modifiers: null);

            if (method is null)
            {
                return Json.Obj(("ok", false), ("reason", "找不到联系人方法（混淆名可能变了）"));
            }

            var list = method.Invoke(null, null);
            if (list is null)
            {
                return Json.Obj(("ok", false), ("reason", "客户端返回 null"));
            }

            (string Json, int Count) Render(string prop)
            {
                var items = new List<string>();

                if (Get(list, prop) is System.Collections.IEnumerable seq)
                {
                    foreach (var c in seq)
                    {
                        if (c is null)
                        {
                            continue;
                        }

                        items.Add(Json.Obj(
                            ("nickName", Get(c, "NickName")?.ToString()),
                            ("account", Get(c, "Account")?.ToString())));
                    }
                }

                return ("[" + string.Join(",", items) + "]", items.Count);
            }

            var (inTeam, inTeamCount) = Render("InTeam");
            var (invited, invitedCount) = Render("Invited");
            var (others, othersCount) = Render("Others");

            return Json.Obj(
                ("ok", true),
                ("domain", AppDomain.CurrentDomain.FriendlyName),
                ("inTeamCount", inTeamCount.ToString(CultureInfo.InvariantCulture)),
                ("invitedCount", invitedCount.ToString(CultureInfo.InvariantCulture)),
                ("othersCount", othersCount.ToString(CultureInfo.InvariantCulture)),
                ("inTeam", new Json.Raw(inTeam)),
                ("invited", new Json.Raw(invited)),
                ("others", new Json.Raw(others)));
        }
        catch (Exception ex)
        {
            return Json.Obj(
                ("ok", false),
                ("reason", ex.GetType().Name + ": " + ex.Message),
                ("chain", Json.StringArray(Chain(ex))));
        }
    }

    /// <summary>
    /// 通用反射桥的入口。见 <see cref="ReflectiveBridge"/> 的说明。
    /// </summary>
    public string InvokeBridge(string script)
    {
        try
        {
            var result = ReflectiveBridge.Run(script);

            var slots = new StringBuilder();
            slots.Append('[');

            for (var i = 0; i < result.Slots.Count; i++)
            {
                if (i > 0) slots.Append(',');

                var v = result.Slots[i];
                slots.Append(v is null
                    ? "null"
                    : Json.Obj(
                        ("slot", i.ToString(CultureInfo.InvariantCulture)),
                        ("type", v.GetType().Name),
                        ("value", v.ToString())));
            }

            slots.Append(']');

            return Json.Obj(
                ("ok", result.Error is null),
                ("domain", AppDomain.CurrentDomain.FriendlyName),
                ("error", result.Error),
                ("stepCount", result.Slots.Count.ToString(CultureInfo.InvariantCulture)),
                ("steps", Json.StringArray(result.Lines)),
                ("slots", new Json.Raw(slots.ToString())),
                ("refUpdates", new Json.Raw(
                    "{" + string.Join(",", result.RefUpdates.Select(
                        kv => Json.Quote(kv.Key) + ":" + Json.Quote(kv.Value))) + "}")));
        }
        catch (Exception ex)
        {
            return Json.Obj(
                ("ok", false),
                ("reason", ex.GetType().Name + ": " + ex.Message),
                ("chain", Json.StringArray(Chain(ex))));
        }
    }

    /// <summary>构造 NSSandbox。失败时返回原因。</summary>
    private static object? BuildNSSandbox(long sandboxId, long magic, string localRoot, out string? error)
    {
        error = null;

        var t = FindClientType("NutstoreClient.Common.NSSandbox");
        if (t is null)
        {
            error = "找不到 NSSandbox（版本可能变了）";
            return null;
        }

        var box = Activator.CreateInstance(
            t,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: new object[] { sandboxId, magic, localRoot, false },
            culture: null);

        if (box is null) error = "NSSandbox 构造失败";
        return box;
    }

    /// <summary>
    /// 读某个本地文件在云端的对象详情。只读。
    /// </summary>
    /// <remarks>
    /// 目标：c55ca68332481ffab4014efe97582ac93(NSSandbox, NutstorePath)
    ///   → DownstreamObjectEntityBean { id, path(byte[]), objectType, version,
    ///     size, hash, deleted, compressible, srcMachineName, tblId }
    /// 注意这是**字段**不是属性。
    /// </remarks>
    public string ReadObjectDetail(long sandboxId, long magic, string localRoot, string localFilePath)
    {
        try
        {
            var argError = CheckPathArgs(sandboxId, magic, localRoot, localFilePath);
            if (argError is not null) return Json.Obj(("ok", false), ("reason", argError));

            var api = FindClientType(CloudOpsTypeName);
            if (api is null) return Json.Obj(("ok", false), ("reason", "找不到云端操作层（版本可能变了）"));

            var box = BuildNSSandbox(sandboxId, magic, localRoot, out var boxError);
            if (box is null) return Json.Obj(("ok", false), ("reason", boxError));

            var nsPath = BuildNutstorePath(sandboxId, magic, localRoot, localFilePath, out var pathError);
            if (nsPath is null) return Json.Obj(("ok", false), ("reason", pathError));

            var method = api.GetMethod(
                "c55ca68332481ffab4014efe97582ac93",
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                types: new[] { box.GetType(), nsPath.GetType() },
                modifiers: null);

            if (method is null) return Json.Obj(("ok", false), ("reason", "找不到对象详情方法（混淆名可能变了）"));

            var bean = method.Invoke(null, new[] { box, nsPath });
            if (bean is null) return Json.Obj(("ok", false), ("reason", "客户端返回 null"));

            // path 是 byte[]，要按客户端自己的 UTF8 转换还原成字符串
            string? pathText = null;
            if (Get(bean, "path") is byte[] bytes)
            {
                try
                {
                    var helper = FindClientType("NutstoreLib.Utils.StringUtils");
                    var toStr = helper?.GetMethod("UTF8BytesToString", BindingFlags.Public | BindingFlags.Static,
                        binder: null, types: new[] { typeof(byte[]) }, modifiers: null);

                    pathText = toStr is not null
                        ? toStr.Invoke(null, new object[] { bytes }) as string
                        : Encoding.UTF8.GetString(bytes);
                }
                catch (Exception)
                {
                    pathText = null;
                }
            }

            return Json.Obj(
                ("ok", true),
                ("domain", AppDomain.CurrentDomain.FriendlyName),
                ("localFilePath", localFilePath),
                ("id", Get(bean, "id")?.ToString()),
                ("cloudPath", pathText),
                ("objectType", Get(bean, "objectType")?.ToString()),
                ("version", Get(bean, "version")?.ToString()),
                ("sizeBytes", Get(bean, "size")?.ToString()),
                ("size", Human(AsLong(Get(bean, "size")))),
                ("hash", Get(bean, "hash")?.ToString()),
                ("deleted", BoolText(Get(bean, "deleted"))),
                ("compressible", BoolText(Get(bean, "compressible"))),
                ("srcMachineName", Get(bean, "srcMachineName")?.ToString()),
                ("tblId", Get(bean, "tblId")?.ToString()));
        }
        catch (Exception ex)
        {
            return Json.Obj(("ok", false), ("reason", ex.GetType().Name + ": " + ex.Message),
                ("chain", Json.StringArray(Chain(ex))));
        }
    }

    /// <summary>
    /// 读某个沙箱的最新事件 ID。只读。
    /// </summary>
    /// <remarks>
    /// 目标：cbb28d79821068cd4efb5de6f9109afff(NSSandbox) → long
    /// 实测「我的坚果云」返回一个游标值。这个值是增量同步的游标 ——
    /// 拿到它才能谈「从某个时间点之后有什么变化」。
    /// </remarks>
    public string ReadLatestEventId(long sandboxId, long magic, string localRoot)
    {
        try
        {
            if (sandboxId <= 0 || magic <= 0) return Json.Obj(("ok", false), ("reason", "sandboxId / magic 必须为正数"));
            if (string.IsNullOrWhiteSpace(localRoot) || !Path.IsPathRooted(localRoot))
                return Json.Obj(("ok", false), ("reason", "localRoot 必须是绝对的本地路径"));

            var api = FindClientType(CloudOpsTypeName);
            if (api is null) return Json.Obj(("ok", false), ("reason", "找不到云端操作层（版本可能变了）"));

            var box = BuildNSSandbox(sandboxId, magic, localRoot, out var boxError);
            if (box is null) return Json.Obj(("ok", false), ("reason", boxError));

            var method = api.GetMethod(
                "cbb28d79821068cd4efb5de6f9109afff",
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                types: new[] { box.GetType() },
                modifiers: null);

            if (method is null) return Json.Obj(("ok", false), ("reason", "找不到事件 ID 方法（混淆名可能变了）"));

            var value = method.Invoke(null, new[] { box });

            return Json.Obj(
                ("ok", true),
                ("domain", AppDomain.CurrentDomain.FriendlyName),
                ("sandboxId", sandboxId.ToString(CultureInfo.InvariantCulture)),
                ("latestEventId", value?.ToString()));
        }
        catch (Exception ex)
        {
            return Json.Obj(("ok", false), ("reason", ex.GetType().Name + ": " + ex.Message),
                ("chain", Json.StringArray(Chain(ex))));
        }
    }

    /// <summary>
    /// 把云端文件的内容下载到本地路径。
    /// </summary>
    /// <remarks>
    /// 会写本地磁盘，云端不改。版本号必须显式给服务端 —— 实测传 0 会报
    /// ObjectNotFound、传 -1 会报 IllegalArgument，所以这里先查对象详情拿真实版本。
    ///
    /// 目标已存在时**拒绝**（调用方应先确认），并且用独占方式创建 ——
    /// 即使调用方判断和写入之间被人抢先建了文件，也不会覆盖。
    /// </remarks>
    public string DownloadCloudFile(
        long sandboxId, long magic, string localRoot, string cloudPath, string destPath)
    {
        try
        {
            if (sandboxId <= 0 || magic <= 0)
                return Json.Obj(("ok", false), ("reason", "sandboxId / magic 必须为正数"));

            if (string.IsNullOrWhiteSpace(localRoot) || !Path.IsPathRooted(localRoot))
                return Json.Obj(("ok", false), ("reason", "localRoot 必须是绝对的本地路径"));

            if (string.IsNullOrWhiteSpace(cloudPath) || !cloudPath.StartsWith("/", StringComparison.Ordinal))
                return Json.Obj(("ok", false), ("reason", "cloudPath 必须以 / 开头"));

            if (string.IsNullOrWhiteSpace(destPath) || !Path.IsPathRooted(destPath))
                return Json.Obj(("ok", false), ("reason", "destPath 必须是绝对的本地路径"));

            // 目标已存在就拒绝 —— 这是这个操作唯一可能造成数据丢失的地方
            if (File.Exists(destPath))
                return Json.Obj(("ok", false), ("reason", "目标文件已存在，拒绝覆盖: " + destPath));

            var parent = Path.GetDirectoryName(destPath);
            if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
                return Json.Obj(("ok", false), ("reason", "目标的上级目录不存在: " + parent));

            var api = FindClientType(CloudOpsTypeName);
            if (api is null) return Json.Obj(("ok", false), ("reason", "找不到云端操作层（版本可能变了）"));

            var box = BuildNSSandbox(sandboxId, magic, localRoot, out var boxError);
            if (box is null) return Json.Obj(("ok", false), ("reason", boxError));

            // NutstorePath.FromNutstorePath(NSSandbox, string) —— 这次是从云端路径构造
            var nsPathType = FindClientType("NutstoreClient.Common.NutstorePath");
            var fromCloud = nsPathType?.GetMethod(
                "FromNutstorePath", BindingFlags.Public | BindingFlags.Static,
                binder: null, types: new[] { box.GetType(), typeof(string) }, modifiers: null);

            if (fromCloud is null) return Json.Obj(("ok", false), ("reason", "找不到 NutstorePath.FromNutstorePath"));

            var nsPath = fromCloud.Invoke(null, new[] { box, cloudPath });
            if (nsPath is null) return Json.Obj(("ok", false), ("reason", "构造 NutstorePath 返回 null"));

            // ── 先查真实版本号 ──
            var detailMethod = api.GetMethod(
                "c55ca68332481ffab4014efe97582ac93",
                BindingFlags.Public | BindingFlags.Static,
                binder: null, types: new[] { box.GetType(), nsPathType! }, modifiers: null);

            if (detailMethod is null) return Json.Obj(("ok", false), ("reason", "找不到对象详情方法"));

            var bean = detailMethod.Invoke(null, new[] { box, nsPath });
            if (bean is null) return Json.Obj(("ok", false), ("reason", "云端没有这个对象"));

            var version = AsLong(Get(bean, "version"));
            if (version <= 0)
                return Json.Obj(("ok", false), ("reason", "拿到的版本号无效: " + version));

            // ── 下载 ──
            var downloadMethod = api.GetMethod(
                "c089b7574cb46db5e6d9bba4e0e0a6002",
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                types: new[]
                {
                    nsPathType!, typeof(long), typeof(long), typeof(bool).MakeByRefType(),
                    FindClientType("NutstoreClient.Connection.GetRange")!, typeof(bool),
                },
                modifiers: null);

            if (downloadMethod is null)
                return Json.Obj(("ok", false), ("reason", "找不到下载方法（混淆名可能变了）"));

            var args = new object?[] { nsPath, 0L, version, false, null, false };
            var stream = downloadMethod.Invoke(null, args) as Stream;

            if (stream is null)
                return Json.Obj(("ok", false), ("reason", "下载返回的不是流"));

            var fullDownload = args[3] as bool? ?? false;

            long written;

            try
            {
                // FileMode.CreateNew：即使上面判断和这里之间被人抢先建了文件，
                // 也只会抛异常，不会覆盖
                using (var output = new FileStream(destPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.CopyTo(output);
                    written = output.Length;
                }
            }
            finally
            {
                try { stream.Dispose(); } catch (Exception) { }
            }

            return Json.Obj(
                ("ok", true),
                ("domain", AppDomain.CurrentDomain.FriendlyName),
                ("cloudPath", cloudPath),
                ("destPath", destPath),
                ("version", version.ToString(CultureInfo.InvariantCulture)),
                ("bytes", written.ToString(CultureInfo.InvariantCulture)),
                ("size", Human(written)),
                ("fullDownload", fullDownload ? "true" : "false"));
        }
        catch (Exception ex)
        {
            return Json.Obj(("ok", false), ("reason", ex.GetType().Name + ": " + ex.Message),
                ("chain", Json.StringArray(Chain(ex))));
        }
    }

    /// <summary>
    /// 在云端移动 / 重命名一个对象。
    /// </summary>
    /// <remarks>
    /// 目标：ca22f644cc5eac05e5c099fb105c5b6f5(src, dest, metaUpdates)
    ///
    /// 前置条件（反编译确认，不满足会 CheckArgument 失败）：
    ///   src.Sandbox == dest.Sandbox、两者都不能是根。
    ///
    /// 第三参数是 IEnumerable&lt;WriteMetaDataParams&gt;，移动目录时用来给后代
    /// 更新路径。移动文件传**空集合**即可，但**不能传 null** ——
    /// 实测传 null 抛 ArgumentNullException（目标是 GetEnumerator()）。
    /// 空数组实现了 IEnumerable&lt;T&gt;，所以用 Array.CreateInstance 造一个。
    /// </remarks>
    public string MoveCloudObject(
        long sandboxId, long magic, string localRoot, string srcCloudPath, string destCloudPath)
    {
        try
        {
            if (sandboxId <= 0 || magic <= 0)
                return Json.Obj(("ok", false), ("reason", "sandboxId / magic 必须为正数"));

            if (string.IsNullOrWhiteSpace(localRoot) || !Path.IsPathRooted(localRoot))
                return Json.Obj(("ok", false), ("reason", "localRoot 必须是绝对的本地路径"));

            foreach (var (label, p) in new[] { ("srcCloudPath", srcCloudPath), ("destCloudPath", destCloudPath) })
            {
                if (string.IsNullOrWhiteSpace(p) || !p.StartsWith("/", StringComparison.Ordinal))
                    return Json.Obj(("ok", false), ("reason", label + " 必须以 / 开头"));
                if (p == "/")
                    return Json.Obj(("ok", false), ("reason", label + " 不能是根目录"));
            }

            if (string.Equals(srcCloudPath, destCloudPath, StringComparison.Ordinal))
                return Json.Obj(("ok", false), ("reason", "源和目标相同，无需移动"));

            var api = FindClientType(CloudOpsTypeName);
            var nsPathType = FindClientType("NutstoreClient.Common.NutstorePath");
            var metaType = FindClientType("NutstoreClient.Connection.WriteMetaDataParams");

            if (api is null || nsPathType is null || metaType is null)
                return Json.Obj(("ok", false), ("reason", "找不到云端操作层 / NutstorePath / WriteMetaDataParams"));

            var box = BuildNSSandbox(sandboxId, magic, localRoot, out var boxError);
            if (box is null) return Json.Obj(("ok", false), ("reason", boxError));

            var fromCloud = nsPathType.GetMethod(
                "FromNutstorePath", BindingFlags.Public | BindingFlags.Static,
                binder: null, types: new[] { box.GetType(), typeof(string) }, modifiers: null);

            if (fromCloud is null) return Json.Obj(("ok", false), ("reason", "找不到 NutstorePath.FromNutstorePath"));

            var src = fromCloud.Invoke(null, new[] { box, srcCloudPath });
            var dest = fromCloud.Invoke(null, new[] { box, destCloudPath });

            if (src is null || dest is null)
                return Json.Obj(("ok", false), ("reason", "构造 NutstorePath 失败"));

            // 空数组实现了 IEnumerable<WriteMetaDataParams> —— 移动文件够用
            var emptyMeta = Array.CreateInstance(metaType, 0);

            var method = api.GetMethod(
                "ca22f644cc5eac05e5c099fb105c5b6f5",
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                types: new[] { nsPathType, nsPathType, typeof(System.Collections.Generic.IEnumerable<>)
                    .MakeGenericType(metaType) },
                modifiers: null);

            if (method is null)
                return Json.Obj(("ok", false), ("reason", "找不到移动方法（混淆名可能变了）"));

            var results = method.Invoke(null, new[] { src, dest, emptyMeta }) as System.Collections.IEnumerable;

            var moved = 0;
            if (results is not null)
            {
                foreach (var _ in results) moved++;
            }

            return Json.Obj(
                ("ok", true),
                ("domain", AppDomain.CurrentDomain.FriendlyName),
                ("srcCloudPath", srcCloudPath),
                ("destCloudPath", destCloudPath),
                ("resultCount", moved.ToString(CultureInfo.InvariantCulture)),
                ("note", "移动已提交。最终是否生效由客户端与服务端决定 —— 用 nutstore_l3_list_cloud_dir 复查。"));
        }
        catch (Exception ex)
        {
            return Json.Obj(("ok", false), ("reason", ex.GetType().Name + ": " + ex.Message),
                ("chain", Json.StringArray(Chain(ex))));
        }
    }

    private static string BoolText(object? v) => (v as bool? ?? false) ? "true" : "false";

    /// <summary>
    /// 按名字取对象的成员：先找属性，再找字段。
    /// </summary>
    /// <remarks>
    /// **必须两者都找** —— 客户端里有两种风格：SyncFolderDetailBean 用属性，
    /// UserInfoBean / PathPermsBean 用小写字段。只按属性找会静默拿到 null，
    /// 输出里就是一片空白，看不出是「没有这个字段」还是「读失败」。
    /// </remarks>
    private static object? Get(object? target, string name)
    {
        if (target is null)
        {
            return null;
        }

        try
        {
            var t = target.GetType();

            var prop = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (prop is not null)
            {
                return prop.GetValue(target);
            }

            var field = t.GetField(name, BindingFlags.Public | BindingFlags.Instance);
            return field?.GetValue(target);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static List<string> Chain(Exception ex)
    {
        var result = new List<string>();
        var depth = 0;

        while (ex is not null && depth++ < 8)
        {
            result.Add($"{ex.GetType().Name}: {ex.Message}");
            ex = ex.InnerException!;
        }

        return result;
    }

    /// <summary>在默认域已加载的程序集里找类型。</summary>
    /// <summary>
    /// 按全名找客户端类型。找不到返回 null（不抛）。
    /// </summary>
    /// <remarks>internal 而非 private：反射桥也要用它做类型解析。</remarks>
    internal static Type? FindClientType(string fullName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                var t = asm.GetType(fullName, throwOnError: false);
                if (t is not null)
                {
                    return t;
                }
            }
            catch (Exception)
            {
                // 跳过
            }
        }

        return null;
    }

    // ── 内部实现 ──

    /// <summary>
    /// 遍历核心，独立出来是为了能**脱离客户端**测试。
    ///
    /// <para>
    /// 遍历这块已经出过两次 bug（逻辑树剪掉视觉子树、局部去重导致成环指数展开），
    /// 而两次都只能在真客户端里才暴露 —— 一次重启才换一个 bug 的教训太贵了。
    /// 把核心逻辑暴露出来，就可以在本地造树验证。
    /// </para>
    /// </summary>
    public static List<string> WalkTree(DependencyObject root, int maxDepth, out bool truncated)
    {
        var nodes = new List<string>();
        truncated = false;
        var budget = 12000;
        var visited = new HashSet<DependencyObject>(RefComparer.Instance);
        Walk(root, 0, maxDepth, nodes, ref budget, ref truncated, visited);
        return nodes;
    }

    // net48 没有 Math.Clamp 和 String.Contains(string, StringComparison) ——
    // 那些是 .NET Core / netstandard2.1 的 API，这里自己补两个。
    private static int Clamp(int value, int min, int max)
        => value < min ? min : (value > max ? max : value);

    private static bool Has(string? haystack, string? needle)
        => !string.IsNullOrEmpty(haystack)
           && !string.IsNullOrEmpty(needle)
           && haystack!.IndexOf(needle!, StringComparison.OrdinalIgnoreCase) >= 0;

    private static Window? FindWindow(Application app, string typeFilter)
    {
        var candidates = app.Windows.Cast<Window>().ToList();
        if (candidates.Count == 0)
        {
            return null;
        }

        var matched = string.IsNullOrWhiteSpace(typeFilter)
            ? candidates
            : candidates.Where(w => Has(w.GetType().FullName, typeFilter)).ToList();

        if (matched.Count == 0)
        {
            return null;
        }

        // 同名窗口有多个时，挑用户最可能正在看的那个
        return matched.FirstOrDefault(w => w.IsActive)
               ?? matched.FirstOrDefault(w => w.IsVisible)
               ?? matched[0];
    }

    private static void Walk(DependencyObject node, int depth, int maxDepth, List<string> output, ref int budget, ref bool truncated, HashSet<DependencyObject> visited)
    {
        // 全局访问集：逻辑树与视觉树并集 + Popup.Child 之后，同一个对象会经由
        // 多条路径到达，菜单的弹出子菜单甚至会指回菜单结构本身。
        // 用局部去重会指数级重复展开 —— 实测 多文件夹的窗口炸出 141k 节点，
        // 其中 38k 是 MenuItem 的重复。
        if (!visited.Add(node))
        {
            return;
        }

        if (budget <= 0)
        {
            truncated = true;
            return;
        }

        budget--;

        var childCount = CountChildren(node);
        output.Add(Json.Obj(
            ("depth", depth),
            ("type", node.GetType().Name),
            ("name", (node as FrameworkElement)?.Name is { Length: > 0 } n ? n : null),
            ("text", TryGetText(node)),
            ("childCount", childCount)));

        if (depth >= maxDepth || budget <= 0)
        {
            if (childCount > 0)
            {
                truncated = true;
            }

            return;
        }

        foreach (var child in Children(node))
        {
            Walk(child, depth + 1, maxDepth, output, ref budget, ref truncated, visited);
        }
    }

    private static void Search(DependencyObject node, int depth, string query, List<string> hits, int cap, HashSet<DependencyObject> visited)
    {
        // 同 Walk：必须全局去重，否则重复展开既慢又会让 hits 被同一批控件塞满
        if (!visited.Add(node))
        {
            return;
        }

        if (hits.Count >= cap || depth > 60)
        {
            return;
        }

        var type = node.GetType();
        var typeName = type.Name;
        var fullName = type.FullName ?? typeName;
        var name = (node as FrameworkElement)?.Name;
        var text = TryGetText(node);
        var toggle = node as System.Windows.Controls.Primitives.ToggleButton;

        var hit = Has(fullName, query)
                  || Has(typeName, query)
                  || Has(name, query)
                  || Has(text, query);

        if (hit)
        {
            hits.Add(Json.Obj(
                ("depth", depth),
                ("type", typeName),
                ("fullType", fullName),
                ("name", name),
                ("text", text),
                ("isToggle", toggle is not null),
                ("isChecked", toggle?.IsChecked)));
        }

        foreach (var child in Children(node))
        {
            Search(child, depth + 1, query, hits, cap, visited);
        }
    }

    /// <summary>
    /// 取子节点：逻辑树 **并上** 视觉树。
    ///
    /// <para>
    /// 之前的实现是「逻辑子非空就用逻辑子，否则退到视觉子」—— 这是错的：
    /// 只要某节点有逻辑子节点，它的**整棵视觉子树就被剪掉**了，而模板生成的
    /// 控件、adorner、滑出面板统统只在视觉树里。设置界面就是这么被漏掉的。
    /// </para>
    /// <para>
    /// 另外 <see cref="Popup"/> 的内容不在窗口的视觉树里（它挂在独立的
    /// PopupRoot 上），必须显式取 <see cref="Popup.Child"/>，否则菜单、
    /// 气泡、弹出面板一个都看不到。
    /// </para>
    /// </summary>
    private static IEnumerable<DependencyObject> Children(DependencyObject node)
    {
        var seen = new HashSet<DependencyObject>(RefComparer.Instance);
        var result = new List<DependencyObject>();

        // Popup：内容在独立视觉树里，得单独接上
        if (node is System.Windows.Controls.Primitives.Popup popup && popup.Child is not null && seen.Add(popup.Child))
        {
            result.Add(popup.Child);
        }

        // ContextMenu / ToolTip 挂在**属性**上，既不在逻辑树也不在视觉树里 ——
        // 不显式接上，右键菜单和气泡的内容遍历永远看不到（实测 MenuItem 数为 0）。
        if (node is FrameworkElement fe)
        {
            if (fe.ContextMenu is not null && seen.Add(fe.ContextMenu))
            {
                result.Add(fe.ContextMenu);
            }

            if (fe.ToolTip is DependencyObject tip && seen.Add(tip))
            {
                result.Add(tip);
            }
        }

        // C# 不允许在 try-catch 里 yield，所以先收集再产出
        try
        {
            foreach (var child in LogicalTreeHelper.GetChildren(node).Cast<object>().OfType<DependencyObject>())
            {
                if (seen.Add(child))
                {
                    result.Add(child);
                }
            }
        }
        catch (Exception)
        {
            // 个别节点的逻辑子不可枚举
        }

        foreach (var child in VisualChildren(node))
        {
            if (seen.Add(child))
            {
                result.Add(child);
            }
        }

        return result;
    }

    /// <summary>按引用比较 —— WPF 里有些类型重写了 Equals，用默认比较器会误判重复。</summary>
    private sealed class RefComparer : IEqualityComparer<DependencyObject>
    {
        public static readonly RefComparer Instance = new();

        public bool Equals(DependencyObject? a, DependencyObject? b) => ReferenceEquals(a, b);

        public int GetHashCode(DependencyObject o)
            => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o);
    }

    private static List<DependencyObject> VisualChildren(DependencyObject node)
    {
        var result = new List<DependencyObject>();
        try
        {
            var count = VisualTreeHelper.GetChildrenCount(node);
            for (var i = 0; i < count; i++)
            {
                result.Add(VisualTreeHelper.GetChild(node, i));
            }
        }
        catch (Exception)
        {
            // 有些节点不支持视觉树遍历
        }

        return result;
    }

    private static int CountChildren(DependencyObject node)
    {
        try
        {
            return VisualTreeHelper.GetChildrenCount(node);
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static string? TryGetText(DependencyObject node)
    {
        try
        {
            var text = node switch
            {
                System.Windows.Controls.TextBlock tb => tb.Text,
                System.Windows.Controls.ContentControl cc when cc.Content is string s => s,
                _ => null,
            };

            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            // net48 的参考程序集里 IsNullOrWhiteSpace 没有 [NotNullWhen]，
            // 编译器不会据此收窄，所以显式取一次非空引用
            var trimmed = text!.Trim();
            return trimmed.Length > 80 ? trimmed.Substring(0, 80) + "…" : trimmed;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>把操作挪到 UI 线程。已经在 UI 线程上就直接执行，避免自死锁。</summary>
    private static string OnUiThread(Func<string> action)
    {
        try
        {
            var app = Application.Current;
            if (app?.Dispatcher is null)
            {
                return Json.Obj(("available", false), ("reason", "Application.Current 或 Dispatcher 为空"));
            }

            return app.Dispatcher.CheckAccess() ? action() : app.Dispatcher.Invoke(action);
        }
        catch (Exception ex)
        {
            return Fail(ex);
        }
    }

    private static string Fail(Exception ex) => Json.Obj(
        ("ok", false),
        ("error", ex.GetType().Name),
        ("message", ex.Message));

    private static string SafeLocation()
    {
        try
        {
            var loc = Assembly.GetExecutingAssembly().Location;
            return string.IsNullOrEmpty(loc) ? "(内存加载)" : loc;
        }
        catch (Exception)
        {
            return "(不可读)";
        }
    }
}

/// <summary>
/// 极简 JSON 写出器。
///
/// 为什么不用 System.Text.Json：本程序集跑在客户端默认域里，引入任何
/// 版本可能冲突的依赖都会危及客户端本身。JSON 的形状完全由我们控制，
/// 手写反而最安全 —— 十几行代码换掉一个真实的崩溃风险。
/// </summary>
internal static class Json
{
    /// <summary>
    /// 已经是合法 JSON 的片段，嵌入时**不加引号也不转义**。
    ///
    /// 没有这个类型的话，Obj 会把 Json.Array(...) 返回的字符串当成普通字符串
    /// 再转义一层，产出 "windows":"[{\"type\":...}]" 这种「JSON 里套 JSON 字符串」——
    /// 解析端拿到的是一坨字符串而不是数组。
    /// </summary>
    public readonly struct Raw(string json)
    {
        public string Json { get; } = json;

        public override string ToString() => Json;
    }

    public static string Obj(params (string Key, object? Value)[] pairs)
    {
        var sb = new StringBuilder("{");
        for (var i = 0; i < pairs.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append('"').Append(Escape(pairs[i].Key)).Append("\":");
            sb.Append(Value(pairs[i].Value));
        }

        return sb.Append('}').ToString();
    }

    public static Raw Array(IEnumerable<string> rawJsonItems)
        => new("[" + string.Join(",", rawJsonItems) + "]");

    public static Raw StringArray(IEnumerable<string> values)
        => new("[" + string.Join(",", values.Select(v => Quote(v))) + "]");

    public static string Quote(string? s) => s is null ? "null" : "\"" + Escape(s) + "\"";

    private static string Value(object? v) => v switch
    {
        null => "null",
        Raw raw => raw.Json,
        bool b => b ? "true" : "false",
        string s => Quote(s),
        int i => i.ToString(CultureInfo.InvariantCulture),
        long l => l.ToString(CultureInfo.InvariantCulture),
        double d => double.IsNaN(d) || double.IsInfinity(d) ? "0" : d.ToString("R", CultureInfo.InvariantCulture),
        _ => Quote(v.ToString() ?? string.Empty),
    };

    private static string Escape(string s)
    {
        var sb = new StringBuilder(s.Length + 8);
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    // 控制字符必须转义，否则不是合法 JSON
                    if (c < 0x20)
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }

        return sb.ToString();
    }
}
