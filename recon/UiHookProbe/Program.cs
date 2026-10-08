using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using NutstoreMcp.UiHook;

namespace UiHookProbe;

/// <summary>
/// 遍历逻辑的本地验证。
///
/// <para>
/// 存在的理由：控件树遍历已经出过两次 bug —— 一次是「逻辑树优先」把整棵视觉
/// 子树剪掉（设置界面因此完全看不见），一次是局部去重导致成环指数展开
/// （多文件夹的窗口炸出 141k 节点、38k 个重复 MenuItem）。
/// 两次都只能在真客户端里暴露，一次重启换一个 bug 的代价太高。
/// </para>
/// <para>
/// 这个探针造出那两类结构，离线验证遍历结果。
/// </para>
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var failures = 0;

        failures += Check("模板生成的控件能被走到（视觉树不能被子树剪掉）", TestTemplateContent);
        failures += Check("逻辑与视觉并集不产生重复", TestLogicalVisualOverlap);
        failures += Check("Popup 内容能被走到且不成环", TestPopupNoCycle);
        failures += Check("共享子树只访问一次（全局去重）", TestLogicalVisualOverlapVisitedOnce);
        failures += Check("从 Window 走能到达窗口模板（adorner 层在这里）", TestWindowTemplateReachable);
        failures += Check("L3 写入守卫：默认全拒", Guard_DefaultsRefuseEverything);
        failures += Check("L3 写入守卫：非空目录必拒（核心数据安全）", Guard_NonEmptyDirectoryAlwaysRefused);
        failures += Check("L3 写入守卫：嵌套同步根必拒", Guard_NestedUnderExistingBindingRefused);
        failures += Check("L3 写入守卫：全部满足也只预演", Guard_FullySatisfiedStillDryRun);
        failures += Check("L3 写入守卫：缺沙箱状态 fail-closed", Guard_MissingSyncStateRefused);
        failures += Check("L3 写入守卫：已绑定的沙箱拒绝重绑", Guard_AlreadySyncedSandboxRefused);
        failures += Check("L3 禁止清单：危险客户端方法全被拦下", Forbidden_KnownDangerousMethodsBlocked);
        failures += Check("L3 禁止清单：正常方法不被误伤", Forbidden_SafeMethodsNotBlocked);

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "══ 全部通过 ══" : $"══ {failures} 项失败 ══");
        return failures == 0 ? 0 : 1;
    }

    private static int Check(string name, Func<(bool Ok, string Detail)> test)
    {
        try
        {
            var (ok, detail) = test();
            Console.WriteLine($"  {(ok ? "✅" : "❌")} {name}");
            if (!string.IsNullOrEmpty(detail))
            {
                Console.WriteLine($"       {detail}");
            }

            return ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ❌ {name}");
            Console.WriteLine($"       {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// 模板生成的控件只在视觉树里。
    /// 老的「逻辑子非空就用逻辑子」实现会把它们整棵剪掉。
    /// </summary>
    private static (bool, string) TestTemplateContent()
    {
        // 一个带模板的 Button：它的 Border/ContentPresenter 都是模板产物，
        // 只在视觉树里，逻辑子只有它的 Content
        var button = new Button { Content = "点我", Width = 100, Height = 30 };
        var root = new StackPanel();
        root.Children.Add(button);

        // 强迫模板展开
        root.Measure(new Size(400, 400));
        root.Arrange(new Rect(0, 0, 400, 400));
        root.UpdateLayout();

        var nodes = UiHook.WalkTree(root, 20, out _);
        var types = nodes.Select(ExtractType).ToList();

        var hasTemplatePart = types.Any(t => t is "ContentPresenter" or "Border" or "ButtonChrome");
        return (hasTemplatePart,
            hasTemplatePart
                ? $"走到 {nodes.Count} 个节点，含模板产物"
                : $"只走到 {nodes.Count} 个节点：{string.Join(", ", types.Distinct())}");
    }

    /// <summary>逻辑树和视觉树是同一批对象时必须去重。</summary>
    private static (bool, string) TestLogicalVisualOverlap()
    {
        // Grid 的子元素在逻辑树和视觉树上都会出现 —— 并集后必须只算一次
        var grid = new Grid();
        for (var i = 0; i < 3; i++)
        {
            grid.Children.Add(new TextBlock { Text = $"item{i}" });
        }

        var nodes = UiHook.WalkTree(grid, 20, out _);
        var textBlocks = nodes.Count(n => ExtractType(n) == "TextBlock");

        return (textBlocks == 3, $"TextBlock 出现 {textBlocks} 次（应为 3）");
    }

    /// <summary>Popup 内容挂在独立的 PopupRoot 上，且菜单结构容易成环。</summary>
    private static (bool, string) TestPopupNoCycle()
    {
        var menu = new ContextMenu();
        for (var i = 0; i < 3; i++)
        {
            var item = new MenuItem { Header = $"menu{i}" };
            // 子菜单：MenuItem 内嵌子项，这是真客户端里 MenuItem 爆炸的结构来源
            item.Items.Add(new MenuItem { Header = $"sub{i}" });
            menu.Items.Add(item);
        }

        var root = new StackPanel();
        root.Children.Add(new TextBlock { Text = "host" });
        root.ContextMenu = menu;

        menu.PlacementTarget = root;

        // 必须**保持打开**再遍历：关掉后 WPF 会断开 Popup，那就测不到真实路径了
        menu.IsOpen = true;
        try
        {
            root.Measure(new Size(400, 400));
            root.Arrange(new Rect(0, 0, 400, 400));
            root.UpdateLayout();

            var nodes = UiHook.WalkTree(root, 25, out var truncated);
            var menuItems = nodes.Count(n => ExtractType(n) == "MenuItem");

            // 关键断言：不能指数爆炸。真客户端里 12 项列表炸出 38k 个 MenuItem。
            var ok = menuItems is > 0 and < 100;
            return (ok, $"MenuItem {menuItems} 个（应 >0 且远小于 100），总节点 {nodes.Count}，截断={truncated}");
        }
        finally
        {
            menu.IsOpen = false;
        }
    }

    /// <summary>
    /// 同一对象经**不同父节点**可达时只应产出一次。
    ///
    /// <para>
    /// 真凶是 ContentControl：它的 Content 既在逻辑树上（ContentControl → Content），
    /// 也在视觉树上（ContentControl → ContentPresenter → Content）。
    /// 两个父不同，所以「每个节点各自去重」根本挡不住 —— 真客户端里就是这样
    /// 从 721 个节点炸成 141k 的。
    /// </para>
    /// </summary>
    private static (bool, string) TestLogicalVisualOverlapVisitedOnce()
    {
        var content = new TextBlock { Text = "唯一的内容" };
        var host = new ContentControl { Content = content };

        var root = new StackPanel();
        root.Children.Add(host);

        root.Measure(new Size(400, 400));
        root.Arrange(new Rect(0, 0, 400, 400));
        root.UpdateLayout();

        var nodes = UiHook.WalkTree(root, 25, out _);
        // net48 没有 Contains(string, StringComparison)
        var contentCount = nodes.Count(n => n.IndexOf("唯一的内容", StringComparison.Ordinal) >= 0);

        return (contentCount == 1, $"内容块在树里出现 {contentCount} 次（应为 1）");
    }

    /// <summary>
    /// 从 Window 本身遍历必须能到达**窗口模板**里的东西。
    ///
    /// <para>
    /// 这是真客户端里设置面板一直看不见的真凶：遍历从 window.Content 开始，
    /// 而窗口模板（标题栏、AdornerDecorator）是 Content 的**兄弟节点**，
    /// 整块被跳过。MahApps 的 Flyout 就挂在 AdornerLayer 上 —— 设置面板住那儿。
    /// </para>
    /// </summary>
    private static (bool, string) TestWindowTemplateReachable()
    {
        var win = new Window
        {
            Title = "probe",
            Width = 240,
            Height = 160,
            // 屏幕外 + 无边框 + 不进任务栏，避免测试时闪窗
            Left = -4000,
            Top = -4000,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None,
            Content = new TextBlock { Text = "content" },
        };

        try
        {
            win.Show();
            win.UpdateLayout();

            var fromWindow = UiHook.WalkTree(win, 20, out _).Select(ExtractType).ToList();
            var fromContent = win.Content is DependencyObject c
                ? UiHook.WalkTree(c, 20, out _).Select(ExtractType).ToList()
                : [];

            var windowSeesAdorner = fromWindow.Contains("AdornerDecorator");
            var contentSeesAdorner = fromContent.Contains("AdornerDecorator");

            return (windowSeesAdorner && !contentSeesAdorner,
                $"从 Window：{fromWindow.Count} 节点，AdornerDecorator={windowSeesAdorner}；"
                + $"从 Content：{fromContent.Count} 节点，AdornerDecorator={contentSeesAdorner}");
        }
        finally
        {
            win.Close();
        }
    }

    // ── L3 写入守卫 ──
    //
    // 这些是这一层最该被测的东西：L3 唯一可能丢数据的地方就是「把本地目录绑到云端沙箱」，
    // 而放行判断是纯逻辑，可以离线穷举，不必拿用户的数据当验证环境。

    private static NutstoreMcp.Plugin.L3.WriteGuards.Request Req(
        bool unlock = true,
        bool confirm = true,
        bool dryRun = false,
        string path = @"C:\tmp\newsync",
        bool exists = true,
        bool isDir = true,
        bool isEmpty = true,
        bool bound = false,
        string[]? existing = null,
        long sandboxId = 42,
        string? syncState = "NotSynced")
        => new(unlock, confirm, dryRun, path, exists, isDir, isEmpty, bound, existing ?? [], sandboxId, syncState);

    private static (bool, string) Guard_DefaultsRefuseEverything()
    {
        var v = NutstoreMcp.Plugin.L3.WriteGuards.Evaluate(
            Req(unlock: false, confirm: false, dryRun: true));

        var ok = !v.Allowed && !v.WouldWrite && v.Refusals.Count >= 2;
        return (ok, $"拒绝 {v.Refusals.Count} 条，wouldWrite={v.WouldWrite}");
    }

    private static (bool, string) Guard_NonEmptyDirectoryAlwaysRefused()
    {
        // 即使解锁、确认、非预演，非空目录也必须被拒 —— 这是本层存在的全部理由
        var v = NutstoreMcp.Plugin.L3.WriteGuards.Evaluate(
            Req(unlock: true, confirm: true, dryRun: false, isEmpty: false));

        var mentionsNonEmpty = v.Refusals.Any(r => r.IndexOf("非空", StringComparison.Ordinal) >= 0);
        return (!v.Allowed && !v.WouldWrite && mentionsNonEmpty,
            $"allowed={v.Allowed} wouldWrite={v.WouldWrite} 提到非空={mentionsNonEmpty}");
    }

    private static (bool, string) Guard_NestedUnderExistingBindingRefused()
    {
        var cases = new (string Candidate, string Existing, bool ShouldRefuse)[]
        {
            (@"C:\Users\me\sync", @"C:\Users\me\sync", true),             // 完全相同
            (@"C:\Users\me\sync\sub", @"C:\Users\me\sync", true),         // 现有根的子目录
            (@"C:\Users\me", @"C:\Users\me\sync", true),                  // 现有根的父目录
            (@"C:\Users\me\sync", @"C:\Users\me\sync2", false),           // 前缀相同但不是祖孙
            (@"C:\Users\me\other", @"C:\Users\me\sync", false),           // 无关
            (@"C:\Users\me\SYNC\sub", @"c:\users\me\sync", true),         // 大小写不同仍算重叠
        };

        var bad = new List<string>();
        foreach (var c in cases)
        {
            var v = NutstoreMcp.Plugin.L3.WriteGuards.Evaluate(
                Req(path: c.Candidate, existing: [c.Existing]));

            var refused = !v.Allowed;
            if (refused != c.ShouldRefuse)
            {
                bad.Add($"{c.Candidate} vs {c.Existing}: 期望{(c.ShouldRefuse ? "拒绝" : "放行")}，实际{(refused ? "拒绝" : "放行")}");
            }
        }

        return (bad.Count == 0, bad.Count == 0 ? $"{cases.Length} 个边界用例全对" : string.Join("; ", bad));
    }

    private static (bool, string) Guard_FullySatisfiedStillDryRun()
    {
        // 全部条件满足 + dryRun=true → 允许，但不写
        var dry = NutstoreMcp.Plugin.L3.WriteGuards.Evaluate(
            Req(unlock: true, confirm: true, dryRun: true));

        // 全部满足 + dryRun=false → 允许且会写（真正的执行还要看通道是否可用）
        var wet = NutstoreMcp.Plugin.L3.WriteGuards.Evaluate(
            Req(unlock: true, confirm: true, dryRun: false));

        var ok = dry.Allowed && !dry.WouldWrite && wet.Allowed && wet.WouldWrite;
        return (ok, $"dryRun: allowed={dry.Allowed} wouldWrite={dry.WouldWrite}; "
                    + $"非dryRun: allowed={wet.Allowed} wouldWrite={wet.WouldWrite}");
    }

    private static (bool, string) Guard_MissingSyncStateRefused()
    {
        // 留空不等于「无所谓」：没提供状态就等于没证明它尚未绑定，必须拒绝
        var v = NutstoreMcp.Plugin.L3.WriteGuards.Evaluate(Req(syncState: null));
        var sandboxZero = NutstoreMcp.Plugin.L3.WriteGuards.Evaluate(Req(sandboxId: 0));

        var ok = !v.Allowed && !sandboxZero.Allowed;
        return (ok, $"缺状态: allowed={v.Allowed}; 缺沙箱ID: allowed={sandboxZero.Allowed}");
    }

    private static (bool, string) Guard_AlreadySyncedSandboxRefused()
    {
        var bad = new List<string>();
        foreach (var state in new[] { "SyncedToDisk", "SyncPlaceholder" })
        {
            var v = NutstoreMcp.Plugin.L3.WriteGuards.Evaluate(Req(syncState: state));
            if (v.Allowed)
            {
                bad.Add(state);
            }
        }

        return (bad.Count == 0, bad.Count == 0 ? "SyncedToDisk / SyncPlaceholder 均被拒" : string.Join(",", bad));
    }

    private static (bool, string) Forbidden_KnownDangerousMethodsBlocked()
    {
        // 这些方法一旦被通用调用通道暴露出去，后果不可逆
        string[] mustBlock =
        [
            "RemoveSandbox",
            "MigrateSandboxPath",
            "MigrateSandboxObjectTableToUnicodeNoCaseCollation",
            "UnregisterAllNutstoreSyncRoots",
            "UnregisterSyncRootsOfAccount",
            "UnregisterSyncRoot",
            "DisconnectSyncRoot",
            "DisableSyncRootOnDemandPopulation",
        ];

        var missed = mustBlock
            .Where(m => NutstoreMcp.Plugin.L3.ForbiddenClientMethods.Check("Some.Type", m) is null)
            .ToList();

        // 大小写不该成为绕过手段
        var caseMissed = NutstoreMcp.Plugin.L3.ForbiddenClientMethods.Check(null, "removesandbox") is null;

        if (caseMissed)
        {
            missed.Add("removesandbox（小写变体）");
        }

        return (missed.Count == 0,
            missed.Count == 0 ? $"{mustBlock.Length} 个危险方法 + 大小写变体全部拦下" : "漏掉: " + string.Join(", ", missed));
    }

    private static (bool, string) Forbidden_SafeMethodsNotBlocked()
    {
        // 误伤会让闸门形同虚设 —— 实现者会干脆绕过它
        string[] mustPass =
        [
            "ReadAllSandboxesFromDb",
            "ReadAllSandboxes",
            "GetSandboxObjectsCount",
            "RegisterAsync",
            "ConnectSyncRootCallbacks",
            "GetSyncRootStandardInfo",
            "SetSyncLocation",
        ];

        var wrongly = mustPass
            .Where(m => NutstoreMcp.Plugin.L3.ForbiddenClientMethods.Check("Some.Type", m) is not null)
            .ToList();

        return (wrongly.Count == 0,
            wrongly.Count == 0 ? $"{mustPass.Length} 个正常方法未被误伤" : "误伤: " + string.Join(", ", wrongly));
    }

    /// <summary>从 Json.Obj 产出的 JSON 里抠出 type 字段。</summary>
    private static string ExtractType(string json)
    {
        const string key = "\"type\":\"";
        var i = json.IndexOf(key, StringComparison.Ordinal);
        if (i < 0)
        {
            return string.Empty;
        }

        var start = i + key.Length;
        var end = json.IndexOf('"', start);
        return end < 0 ? string.Empty : json.Substring(start, end - start);
    }
}
