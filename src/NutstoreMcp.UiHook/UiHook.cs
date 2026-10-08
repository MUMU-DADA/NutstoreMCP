using System.Globalization;
using System.Reflection;
using System.Text;
using System.Windows;
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
            var budget = 4000; // 防止控件树过大把响应撑爆

            if (window.Content is DependencyObject content)
            {
                Walk(content, 0, Clamp(maxDepth, 1, 40), nodes, ref budget, ref truncated);
            }

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

            if (window.Content is DependencyObject content)
            {
                Search(content, 0, elementNameOrType ?? string.Empty, hits, cap);
            }

            return Json.Obj(
                ("available", true),
                ("query", elementNameOrType),
                ("count", hits.Count),
                ("truncated", hits.Count >= cap),
                ("matches", Json.Array(hits)));
        });
    }

    // ── 内部实现 ──

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

    private static void Walk(DependencyObject node, int depth, int maxDepth, List<string> output, ref int budget, ref bool truncated)
    {
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
            Walk(child, depth + 1, maxDepth, output, ref budget, ref truncated);
        }
    }

    private static void Search(DependencyObject node, int depth, string query, List<string> hits, int cap)
    {
        if (hits.Count >= cap || depth > 40)
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
            Search(child, depth + 1, query, hits, cap);
        }
    }

    /// <summary>
    /// 逻辑树优先，空的再退到视觉树。
    /// x:Name 命名过的控件在逻辑树上更全，模板展开出来的在视觉树上。
    /// 两边都走会重复，所以取其一。
    /// </summary>
    private static IEnumerable<DependencyObject> Children(DependencyObject node)
    {
        List<DependencyObject> logical;
        try
        {
            logical = LogicalTreeHelper.GetChildren(node).Cast<object>().OfType<DependencyObject>().ToList();
        }
        catch (Exception)
        {
            logical = [];
        }

        return logical.Count > 0 ? logical : VisualChildren(node);
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
