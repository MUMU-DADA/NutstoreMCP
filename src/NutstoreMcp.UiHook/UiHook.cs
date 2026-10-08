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
    private static Type? FindClientType(string fullName)
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
