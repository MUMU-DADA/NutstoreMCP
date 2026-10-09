// 离线验证界面遍历与客户端安全守卫。
using NutstoreMcp.ClientBindings;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Reflection;
using System.Reflection.Emit;
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
        failures += Check("反射桥：删除哈希与请求构造入口在执行前被拦", Bridge_DeleteEntrypointsNeverInvoked);
        failures += Check("反射桥：实例、属性链与反射结果不能越过客户端边界", Bridge_ObjectBoundariesNeverBypassed);
        failures += Check("反射桥：BUFFER 生命周期与 VALUE 新结构体字段守卫", Bridge_BufferAndValueAreBounded);
        failures += Check("反射桥：反射签名与嵌套数组在实现执行前被拦", Bridge_MetadataSignaturesAndArraysAreRejected);

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
            ClientSymbols.DeleteSandbox,
            "MakeDeleteSandboxRequest",
            ClientSymbols.DeleteObject,
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

    private static (bool, string) Bridge_DeleteEntrypointsNeverInvoked()
    {
        // 只在内存构造假客户端。方法执行时加计数器，绝不加载或调用真正的删除实现。
        var dangerous = ClientSymbols.ForbiddenMethodNames;

        var assembly = AppDomain.CurrentDomain.DefineDynamicAssembly(
            new AssemblyName("NutstoreClient.BridgeSafetyProbe"), AssemblyBuilderAccess.Run);
        var module = assembly.DefineDynamicModule("MemoryOnly");
        var hook = new UiHook();
        var checkedCases = 0;
        var failures = new List<string>();

        foreach (var isStatic in new[] { true, false })
        {
            var type = DefineBridgeProbeType(module,
                isStatic ? "NutstoreClient.StaticDeleteProbe" : "NutstoreClient.InstanceDeleteProbe",
                dangerous, isStatic);
            var counter = type.GetField("InvocationCount")!;

            string Script(string name) => isStatic
                ? $"CALL|{type.FullName}|{name}"
                : $"NEW|{type.FullName}\nINSTANCE|0|{name}";

            // 先证明类型解析与调用链可用，避免「找不到类型」让拒绝测试假绿。
            var safe = hook.InvokeBridge(Script("ReadProbe"));
            if (safe.IndexOf("\"ok\":true", StringComparison.Ordinal) < 0
                || (int)counter.GetValue(null)! != 1)
            {
                failures.Add(type.Name + " 正常调用未到达假实现");
                continue;
            }

            counter.SetValue(null, 0);
            foreach (var method in dangerous)
            {
                foreach (var variant in new[] { method, method.ToUpperInvariant(), "  " + method + "  " })
                {
                    var result = hook.InvokeBridge(Script(variant));
                    checkedCases++;
                    if (result.IndexOf("\"ok\":false", StringComparison.Ordinal) < 0
                        || result.IndexOf("命中禁止清单", StringComparison.Ordinal) < 0
                        || (int)counter.GetValue(null)! != 0)
                    {
                        failures.Add(type.Name + "." + variant + " 未在调用前被拒绝");
                    }
                }
            }
        }

        return (failures.Count == 0,
            failures.Count == 0
                ? $"CALL / INSTANCE 共 {checkedCases} 个拒绝用例；假实现执行次数均为 0"
                : string.Join("; ", failures));
    }

    private static Type DefineBridgeProbeType(
        ModuleBuilder module, string name, string[] dangerous, bool isStatic)
    {
        var type = module.DefineType(name, TypeAttributes.Public | TypeAttributes.Class);
        type.DefineDefaultConstructor(MethodAttributes.Public);
        var counter = type.DefineField("InvocationCount", typeof(int), FieldAttributes.Public | FieldAttributes.Static);

        foreach (var method in dangerous.Concat(new[] { "ReadProbe" }))
        {
            var attributes = MethodAttributes.Public | (isStatic ? MethodAttributes.Static : 0);
            var builder = type.DefineMethod(method, attributes, typeof(void), Type.EmptyTypes);
            var il = builder.GetILGenerator();
            il.Emit(OpCodes.Ldsfld, counter);
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Add);
            il.Emit(OpCodes.Stsfld, counter);
            il.Emit(OpCodes.Ret);
        }

        return type.CreateType()!;
    }

    private static (bool, string) Bridge_ObjectBoundariesNeverBypassed()
    {
        // 两个程序集只存在于内存。外部 base 验证继承来的成员仍须检查声明程序集。
        var externalAssembly = AppDomain.CurrentDomain.DefineDynamicAssembly(
            new AssemblyName("BridgeExternalBoundaryProbe"), AssemblyBuilderAccess.Run);
        var externalModule = externalAssembly.DefineDynamicModule("MemoryOnly");
        var externalBuilder = externalModule.DefineType("BridgeExternal.NormalObject", TypeAttributes.Public | TypeAttributes.Class);
        externalBuilder.DefineDefaultConstructor(MethodAttributes.Public);
        var externalCounterBuilder = externalBuilder.DefineField("InvocationCount", typeof(int), FieldAttributes.Public | FieldAttributes.Static);
        var externalField = externalBuilder.DefineField("ExternalField", typeof(int), FieldAttributes.Public);
        var touch = externalBuilder.DefineMethod("TouchExternal", MethodAttributes.Public, typeof(void), Type.EmptyTypes);
        EmitBoundaryCounter(touch.GetILGenerator(), externalCounterBuilder, returnValue: null);
        var externalGetter = externalBuilder.DefineMethod("get_ExternalValue", MethodAttributes.Public | MethodAttributes.SpecialName,
            typeof(int), Type.EmptyTypes);
        EmitBoundaryCounter(externalGetter.GetILGenerator(), externalCounterBuilder, returnValue: 23);
        var externalProperty = externalBuilder.DefineProperty("ExternalValue", PropertyAttributes.None, typeof(int), Type.EmptyTypes);
        externalProperty.SetGetMethod(externalGetter);
        var externalType = externalBuilder.CreateType()!;
        var externalCounter = externalType.GetField("InvocationCount")!;
        var externalObject = Activator.CreateInstance(externalType)!;
        externalType.GetField(externalField.Name)!.SetValue(externalObject, 23);

        var assembly = AppDomain.CurrentDomain.DefineDynamicAssembly(
            new AssemblyName("NutstoreClient.BridgeBoundaryProbe"), AssemblyBuilderAccess.Run);
        var module = assembly.DefineDynamicModule("MemoryOnly");
        var builder = module.DefineType("NutstoreClient.BridgeBoundaryObject", TypeAttributes.Public | TypeAttributes.Class, externalType);
        var forbiddenCountBuilder = builder.DefineField("ForbiddenInvocationCount", typeof(int), FieldAttributes.Public | FieldAttributes.Static);
        var safeCountBuilder = builder.DefineField("SafeInvocationCount", typeof(int), FieldAttributes.Public | FieldAttributes.Static);
        var sources = new Dictionary<string, FieldBuilder>();
        foreach (var pair in new[]
                 {
                     (Name: "Type", Type: typeof(Type)), (Name: "MethodInfo", Type: typeof(MethodInfo)),
                     (Name: "Delegate", Type: typeof(Delegate)), (Name: "Assembly", Type: typeof(Assembly)),
                     (Name: "Module", Type: typeof(Module)), (Name: "External", Type: typeof(object)),
                 })
        {
            var source = builder.DefineField(pair.Name + "Source", pair.Type, FieldAttributes.Public | FieldAttributes.Static);
            sources.Add(pair.Name, source);
            foreach (var isStatic in new[] { true, false })
            {
                var method = builder.DefineMethod("Return" + pair.Name + (isStatic ? "" : "Instance"),
                    MethodAttributes.Public | (isStatic ? MethodAttributes.Static : 0), pair.Type, Type.EmptyTypes);
                var il = method.GetILGenerator();
                il.Emit(OpCodes.Ldsfld, source);
                il.Emit(OpCodes.Ret);
            }
            var propertyGetter = builder.DefineMethod("get_" + pair.Name + "Property",
                MethodAttributes.Public | MethodAttributes.SpecialName, pair.Type, Type.EmptyTypes);
            var getterIl = propertyGetter.GetILGenerator();
            getterIl.Emit(OpCodes.Ldsfld, source);
            getterIl.Emit(OpCodes.Ret);
            var property = builder.DefineProperty(pair.Name + "Property", PropertyAttributes.None, pair.Type, Type.EmptyTypes);
            property.SetGetMethod(propertyGetter);
        }

        var typeField = builder.DefineField("TypeField", typeof(Type), FieldAttributes.Public);
        var methodField = builder.DefineField("MethodInfoField", typeof(MethodInfo), FieldAttributes.Public);
        var safeField = builder.DefineField("SafeValue", typeof(int), FieldAttributes.Public);
        var childField = builder.DefineField("ChildField", builder, FieldAttributes.Public);
        var constructor = builder.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, Type.EmptyTypes);
        var ctorIl = constructor.GetILGenerator();
        ctorIl.Emit(OpCodes.Ldarg_0);
        ctorIl.Emit(OpCodes.Call, externalType.GetConstructor(Type.EmptyTypes)!);
        ctorIl.Emit(OpCodes.Ldarg_0);
        ctorIl.Emit(OpCodes.Ldsfld, sources["Type"]);
        ctorIl.Emit(OpCodes.Stfld, typeField);
        ctorIl.Emit(OpCodes.Ldarg_0);
        ctorIl.Emit(OpCodes.Ldsfld, sources["MethodInfo"]);
        ctorIl.Emit(OpCodes.Stfld, methodField);
        ctorIl.Emit(OpCodes.Ldarg_0);
        ctorIl.Emit(OpCodes.Ldc_I4, 17);
        ctorIl.Emit(OpCodes.Stfld, safeField);
        ctorIl.Emit(OpCodes.Ldarg_0);
        ctorIl.Emit(OpCodes.Ldarg_0);
        ctorIl.Emit(OpCodes.Stfld, childField);
        ctorIl.Emit(OpCodes.Ret);

        var childGetter = builder.DefineMethod("get_Child", MethodAttributes.Public | MethodAttributes.SpecialName, builder, Type.EmptyTypes);
        var childIl = childGetter.GetILGenerator();
        childIl.Emit(OpCodes.Ldarg_0);
        childIl.Emit(OpCodes.Ret);
        var childProperty = builder.DefineProperty("Child", PropertyAttributes.None, builder, Type.EmptyTypes);
        childProperty.SetGetMethod(childGetter);
        var safeGetter = builder.DefineMethod("get_Value", MethodAttributes.Public | MethodAttributes.SpecialName, typeof(int), Type.EmptyTypes);
        var safeGetterIl = safeGetter.GetILGenerator();
        safeGetterIl.Emit(OpCodes.Ldarg_0);
        safeGetterIl.Emit(OpCodes.Ldfld, safeField);
        safeGetterIl.Emit(OpCodes.Ret);
        var safeProperty = builder.DefineProperty("Value", PropertyAttributes.None, typeof(int), Type.EmptyTypes);
        safeProperty.SetGetMethod(safeGetter);
        var safeMethod = builder.DefineMethod("ReadProbe", MethodAttributes.Public, typeof(int), Type.EmptyTypes);
        EmitBoundaryCounter(safeMethod.GetILGenerator(), safeCountBuilder, returnValue: 17);
        var forbidden = builder.DefineMethod(ClientSymbols.DeleteSandbox, MethodAttributes.Public | MethodAttributes.Static,
            typeof(void), Type.EmptyTypes);
        EmitBoundaryCounter(forbidden.GetILGenerator(), forbiddenCountBuilder, returnValue: null);
        var clientType = builder.CreateType()!;
        var dangerousMethod = clientType.GetMethod(ClientSymbols.DeleteSandbox)!;
        var forbiddenCount = clientType.GetField("ForbiddenInvocationCount")!;
        var safeCount = clientType.GetField("SafeInvocationCount")!;
        clientType.GetField("TypeSource")!.SetValue(null, clientType);
        clientType.GetField("MethodInfoSource")!.SetValue(null, dangerousMethod);
        clientType.GetField("DelegateSource")!.SetValue(null, Delegate.CreateDelegate(typeof(Action), dangerousMethod));
        clientType.GetField("AssemblySource")!.SetValue(null, assembly);
        clientType.GetField("ModuleSource")!.SetValue(null, module);
        clientType.GetField("ExternalSource")!.SetValue(null, externalObject);

        var hook = new UiHook();
        var failures = new List<string>();
        var cases = 0;
        var newClient = "NEW|" + clientType.FullName;
        void Allow(string label, string script, string expectedValue = "17")
        {
            var result = hook.InvokeBridge(script);
            cases++;
            if (result.IndexOf("\"ok\":true", StringComparison.Ordinal) < 0
                || result.IndexOf("\"value\":\"" + expectedValue + "\"", StringComparison.Ordinal) < 0)
                failures.Add(label + "合法调用未通过：" + result);
        }
        void Deny(string label, string script, int completedLines)
        {
            var result = hook.InvokeBridge(script);
            cases++;
            if (result.IndexOf("\"ok\":false", StringComparison.Ordinal) < 0
                || result.IndexOf("\"stepCount\":\"" + completedLines + "\"", StringComparison.Ordinal) < 0
                || (int)forbiddenCount.GetValue(null)! != 0 || (int)externalCounter.GetValue(null)! != 0)
                failures.Add(label + "没有在预期边界拒绝：" + result);
        }

        // 先证明创建、实例方法与客户端成员链确实可达，不能靠类型解析失败假绿。
        Allow("客户端实例", newClient + "\nINSTANCE|0|ReadProbe");
        Allow("客户端字段", newClient + "\nGET|0|SafeValue");
        Allow("客户端getter", newClient + "\nGET|0|Value");
        Allow("客户端属性链", newClient + "\nCHAIN|0|Child|Child|Value");
        Allow("客户端字段链", newClient + "\nCHAIN|0|ChildField|SafeValue");
        if ((int)safeCount.GetValue(null)! != 1) failures.Add("合法实例方法没有到达假实现");

        Deny("钩子自身不能被NEW", "NEW|NutstoreMcp.UiHook.UiHook", 0);
        foreach (var prefix in new[] { "NutstoreMcpFake", "NutstorePretend" })
        {
            var collisionAssembly = AppDomain.CurrentDomain.DefineDynamicAssembly(
                new AssemblyName(prefix), AssemblyBuilderAccess.Run);
            var collisionModule = collisionAssembly.DefineDynamicModule("MemoryOnly");
            var collisionBuilder = collisionModule.DefineType(prefix + ".BoundaryObject", TypeAttributes.Public | TypeAttributes.Class);
            var collisionCountBuilder = collisionBuilder.DefineField("CreationCount", typeof(int), FieldAttributes.Public | FieldAttributes.Static);
            var collisionConstructor = collisionBuilder.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, Type.EmptyTypes);
            var collisionIl = collisionConstructor.GetILGenerator();
            collisionIl.Emit(OpCodes.Ldarg_0);
            collisionIl.Emit(OpCodes.Call, typeof(object).GetConstructor(Type.EmptyTypes)!);
            EmitBoundaryCounter(collisionIl, collisionCountBuilder, returnValue: null);
            var collisionType = collisionBuilder.CreateType()!;
            Deny("程序集前缀碰撞" + prefix, "NEW|" + collisionType.FullName, 0);
            if ((int)collisionType.GetField("CreationCount")!.GetValue(null)! != 0)
                failures.Add(prefix + "的构造函数越过程序集边界执行");
        }

        Deny("原GetType/GetMethod/Invoke绕过", newClient + "\nINSTANCE|0|GetType\nINSTANCE|1|GetMethod|"
            + ClientSymbols.DeleteSandbox + "\nINSTANCE|2|Invoke|-|-", 1);
        Deny("继承Object方法的声明归属", newClient + "\nINSTANCE|0|ToString", 1);
        Deny("继承外部方法的声明归属", newClient + "\nINSTANCE|0|TouchExternal", 1);
        Deny("继承外部getter的声明归属", newClient + "\nGET|0|ExternalValue", 1);
        Deny("继承外部field的声明归属", newClient + "\nGET|0|ExternalField", 1);

        foreach (var name in new[] { "Type", "MethodInfo", "Delegate", "Assembly", "Module" })
        {
            Deny("CALL返回" + name, "CALL|" + clientType.FullName + "|Return" + name, 0);
            Deny("INSTANCE返回" + name, newClient + "\nINSTANCE|0|Return" + name + "Instance", 1);
            Deny("GET属性返回" + name, newClient + "\nGET|0|" + name + "Property", 1);
            Deny("CHAIN属性返回" + name, newClient + "\nCHAIN|0|Child|" + name + "Property", 1);
        }
        foreach (var field in new[] { "TypeField", "MethodInfoField" })
        {
            Deny("GET反射字段" + field, newClient + "\nGET|0|" + field, 1);
            Deny("CHAIN反射字段" + field, newClient + "\nCHAIN|0|ChildField|" + field, 1);
        }

        var externalSlot = "CALL|" + clientType.FullName + "|ReturnExternal";
        Allow("客户端可返回外部普通对象作为数据", externalSlot, externalType.FullName!);
        Deny("外部slot INSTANCE", externalSlot + "\nINSTANCE|0|TouchExternal", 1);
        Deny("外部slot GET getter", externalSlot + "\nGET|0|ExternalValue", 1);
        Deny("外部slot GET field", externalSlot + "\nGET|0|ExternalField", 1);
        Deny("外部slot CHAIN", externalSlot + "\nCHAIN|0|ExternalValue", 1);
        Deny("CHAIN中间外部对象", newClient + "\nCHAIN|0|ExternalProperty|ExternalValue", 1);

        return (failures.Count == 0, failures.Count == 0
            ? $"{cases} 个内存假客户端边界用例通过；禁止实现与外部方法/getter调用次数均为0"
            : string.Join("; ", failures));
    }

    private static void EmitBoundaryCounter(ILGenerator il, FieldBuilder counter, int? returnValue)
    {
        il.Emit(OpCodes.Ldsfld, counter);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stsfld, counter);
        if (returnValue is int value) il.Emit(OpCodes.Ldc_I4, value);
        il.Emit(OpCodes.Ret);
    }

    private static (bool, string) Bridge_BufferAndValueAreBounded()
    {
        // 用与登记 DTO 同名的内存假结构体，当前进程绝不加载真正客户端程序集。
        var assembly = AppDomain.CurrentDomain.DefineDynamicAssembly(
            new AssemblyName("NutstoreClient.BufferValueProbe"), AssemblyBuilderAccess.Run);
        var module = assembly.DefineDynamicModule("MemoryOnly");
        var pathBuilder = module.DefineType("NutstoreClient.BufferValuePath", TypeAttributes.Public | TypeAttributes.Class);
        pathBuilder.DefineDefaultConstructor(MethodAttributes.Public);
        var pathType = pathBuilder.CreateType()!;
        var dtoBuilder = module.DefineType(ClientSymbols.CreateFileParamType,
            TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.SequentialLayout, typeof(ValueType));
        dtoBuilder.DefineField("path", pathType, FieldAttributes.Public);
        dtoBuilder.DefineField("size", typeof(long), FieldAttributes.Public);
        dtoBuilder.DefineField("hashBase64", typeof(string), FieldAttributes.Public);
        dtoBuilder.DefineField("fullObjectNameBase64", typeof(string), FieldAttributes.Public);
        dtoBuilder.DefineField("blockListNameBase64", typeof(string), FieldAttributes.Public);
        dtoBuilder.DefineField("compressible", typeof(bool), FieldAttributes.Public);
        var constructorTouched = dtoBuilder.DefineField("constructorTouched", typeof(bool), FieldAttributes.Public);
        dtoBuilder.DefineField("ReadOnlyField", typeof(long), FieldAttributes.Public | FieldAttributes.InitOnly);
        dtoBuilder.DefineField("StaticField", typeof(long), FieldAttributes.Public | FieldAttributes.Static);
        dtoBuilder.DefineField("PrivateField", typeof(long), FieldAttributes.Private);
        var property = dtoBuilder.DefineProperty("PropertyOnly", PropertyAttributes.None, typeof(long), Type.EmptyTypes);
        var propertyGetter = dtoBuilder.DefineMethod("get_PropertyOnly", MethodAttributes.Public | MethodAttributes.SpecialName,
            typeof(long), Type.EmptyTypes);
        var getterIl = propertyGetter.GetILGenerator();
        getterIl.Emit(OpCodes.Ldc_I8, 42L);
        getterIl.Emit(OpCodes.Ret);
        property.SetGetMethod(propertyGetter);
        var constructor = dtoBuilder.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, Type.EmptyTypes);
        var constructorIl = constructor.GetILGenerator();
        constructorIl.Emit(OpCodes.Ldarg_0);
        constructorIl.Emit(OpCodes.Initobj, dtoBuilder);
        constructorIl.Emit(OpCodes.Ldarg_0);
        constructorIl.Emit(OpCodes.Ldc_I4_1);
        constructorIl.Emit(OpCodes.Stfld, constructorTouched);
        constructorIl.Emit(OpCodes.Ret);
        var dtoType = dtoBuilder.CreateType()!;

        var clientBuilder = module.DefineType("NutstoreClient.BufferValueClient", TypeAttributes.Public | TypeAttributes.Class);
        clientBuilder.DefineDefaultConstructor(MethodAttributes.Public);
        var capturedBuilder = clientBuilder.DefineField("CapturedStream", typeof(Stream), FieldAttributes.Public | FieldAttributes.Static);
        var borrowBuilder = clientBuilder.DefineField("BorrowedStream", typeof(Stream), FieldAttributes.Public | FieldAttributes.Static);
        var capture = clientBuilder.DefineMethod("CaptureBuffer", MethodAttributes.Public | MethodAttributes.Static,
            typeof(bool), new[] { typeof(Stream) });
        var captureIl = capture.GetILGenerator();
        captureIl.Emit(OpCodes.Ldarg_0);
        captureIl.Emit(OpCodes.Stsfld, capturedBuilder);
        captureIl.Emit(OpCodes.Ldarg_0);
        captureIl.Emit(OpCodes.Callvirt, typeof(Stream).GetProperty("CanWrite")!.GetGetMethod()!);
        captureIl.Emit(OpCodes.Ldc_I4_0);
        captureIl.Emit(OpCodes.Ceq);
        captureIl.Emit(OpCodes.Ret);
        var captureAndThrow = clientBuilder.DefineMethod("CaptureBufferAndThrow", MethodAttributes.Public | MethodAttributes.Static,
            typeof(void), new[] { typeof(Stream) });
        var throwIl = captureAndThrow.GetILGenerator();
        throwIl.Emit(OpCodes.Ldarg_0);
        throwIl.Emit(OpCodes.Stsfld, capturedBuilder);
        throwIl.Emit(OpCodes.Ldstr, "bridge-sensitive-message-marker");
        throwIl.Emit(OpCodes.Newobj, typeof(InvalidOperationException).GetConstructor(new[] { typeof(string) })!);
        throwIl.Emit(OpCodes.Throw);
        var returnBorrowed = clientBuilder.DefineMethod("ReturnBorrowedStream", MethodAttributes.Public | MethodAttributes.Static,
            typeof(Stream), Type.EmptyTypes);
        var returnBorrowedIl = returnBorrowed.GetILGenerator();
        returnBorrowedIl.Emit(OpCodes.Ldsfld, borrowBuilder);
        returnBorrowedIl.Emit(OpCodes.Ret);
        var clientType = clientBuilder.CreateType()!;

        var hook = new UiHook();
        var failures = new List<string>();
        var cases = 0;
        var bridgeType = typeof(UiHook).Assembly.GetType("NutstoreMcp.UiHook.ReflectiveBridge", throwOnError: true)!;
        var rawRun = bridgeType.GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!;
        object RunRaw(string script) => rawRun.Invoke(null, new object[] { script })!;
        System.Collections.IList Slots(object result) => (System.Collections.IList)result.GetType().GetProperty("Slots")!.GetValue(result)!;
        string? Error(object result) => (string?)result.GetType().GetProperty("Error")!.GetValue(result);
        void Assert(bool condition, string label)
        {
            cases++;
            if (!condition) failures.Add(label);
        }
        void Deny(string label, string script, int completedLines = 0)
        {
            var result = hook.InvokeBridge(script);
            Assert(result.IndexOf("\"ok\":false", StringComparison.Ordinal) >= 0
                && result.IndexOf("\"stepCount\":\"" + completedLines + "\"", StringComparison.Ordinal) >= 0,
                label + "未在预期行拒绝：" + result);
        }
        string Value(params string[] fields) => "VALUE|" + ClientSymbols.CreateFileParamType
            + (fields.Length == 0 ? string.Empty : "|" + string.Join("|", fields));
        var valid = RunRaw("CALL|" + clientType.FullName + "|ReturnBorrowedStream\n"
            + Value("size", "l:3", "hashBase64", "s:YWJj", "fullObjectNameBase64", "-",
                "blockListNameBase64", "-", "compressible", "b:false"));
        using ((IDisposable)valid)
        {
            var dto = Slots(valid)[1]!;
            Assert(Error(valid) is null && dto.GetType() == dtoType
                && (long)dtoType.GetField("size")!.GetValue(dto)! == 3L
                && (string)dtoType.GetField("hashBase64")!.GetValue(dto)! == "abc"
                && dtoType.GetField("fullObjectNameBase64")!.GetValue(dto) is null
                && !(bool)dtoType.GetField("constructorTouched")!.GetValue(dto)!, "VALUE 精确字段、null与零初始化不运行ctor");
        }

        var reference = RunRaw(Value("hashBase64", "s:YWJj") + "\nGET|0|hashBase64\n" + Value("hashBase64", "#1"));
        using ((IDisposable)reference)
            Assert(Error(reference) is null && (string)dtoType.GetField("hashBase64")!.GetValue(Slots(reference)[2])! == "abc",
                "VALUE 引用前序slot的精确类型值");
        var pathReference = RunRaw("NEW|" + pathType.FullName + "\n" + Value("path", "#0"));
        using ((IDisposable)pathReference)
            Assert(Error(pathReference) is null && ReferenceEquals(Slots(pathReference)[0], dtoType.GetField("path")!.GetValue(Slots(pathReference)[1])),
                "VALUE客户端引用字段仅写入新DTO，保持精确引用");
        foreach (var item in new[]
                 {
                     (Label: "数值类型不匹配", Script: Value("size", "i:3")),
                     (Label: "bool类型不匹配", Script: Value("compressible", "l:0")),
                     (Label: "非可空字段null", Script: Value("size", "-")),
                     (Label: "引用字段类型不匹配", Script: Value("path", "s:YWJj")),
                     (Label: "重复字段", Script: Value("size", "l:1", "size", "l:2")),
                     (Label: "static字段", Script: Value("StaticField", "l:1")),
                     (Label: "readonly字段", Script: Value("ReadOnlyField", "l:1")),
                     (Label: "private字段", Script: Value("PrivateField", "l:1")),
                     (Label: "property不是field", Script: Value("PropertyOnly", "l:1")),
                     (Label: "未知字段", Script: Value("Unknown", "l:1")),
                     (Label: "未登记public可写字段", Script: Value("constructorTouched", "b:true")),
                     (Label: "未登记public字段", Script: Value("constructorTouched", "b:true")),
                     (Label: "字段参数不成对", Script: Value("size")),
                     (Label: "非登记类型", Script: "VALUE|" + clientType.FullName),
                 }) Deny("VALUE " + item.Label, item.Script);
        Assert((long)dtoType.GetField("StaticField")!.GetValue(null)! == 0L, "VALUE拒绝static且原值未修改");
        Deny("BUFFER额外参数", "BUFFER|YWJj|extra");
        Deny("BUFFER无效base64", "BUFFER|!");
        Deny("BUFFER base64空白", "BUFFER|YW Jj");

        var small = RunRaw("BUFFER|YWJj");
        var smallStream = (MemoryStream)Slots(small)[0]!;
        using ((IDisposable)small)
        {
            Assert(Error(small) is null && smallStream.Length == 3 && smallStream.Position == 0
                && smallStream.CanRead && !smallStream.CanWrite, "BUFFER只读流长度与初始位置");
            var writeRefused = false;
            try { smallStream.WriteByte(0); } catch (NotSupportedException) { writeRefused = true; }
            var bufferHidden = false;
            try { smallStream.GetBuffer(); } catch (UnauthorizedAccessException) { bufferHidden = true; }
            Assert(writeRefused && bufferHidden, "BUFFER不允许写入且不暴露可变字节数组");
        }
        Assert(!smallStream.CanRead, "Result Dispose成功路径关闭BUFFER");
        ((IDisposable)small).Dispose();
        var empty = RunRaw("BUFFER|");
        using ((IDisposable)empty)
            Assert(Error(empty) is null && ((MemoryStream)Slots(empty)[0]!).Length == 0, "BUFFER空流支持");
        Deny("BUFFER禁止INSTANCE .NET流", "BUFFER|YWJj\nINSTANCE|0|ReadByte", 1);
        Deny("BUFFER禁止GET .NET流", "BUFFER|YWJj\nGET|0|Length", 1);
        Deny("BUFFER禁止CHAIN .NET流", "BUFFER|YWJj\nCHAIN|0|Length", 1);

        const int diagnosticBudget = 8 * 1024 * 1024;
        var atLimit = RunRaw("BUFFER|" + Convert.ToBase64String(new byte[diagnosticBudget]));
        using ((IDisposable)atLimit)
            Assert(Error(atLimit) is null && ((MemoryStream)Slots(atLimit)[0]!).Length == diagnosticBudget, "BUFFER恰好8MiB支持");
        Deny("BUFFER超过8MiB", "BUFFER|" + Convert.ToBase64String(new byte[diagnosticBudget + 1]));
        var cumulative = RunRaw("BUFFER|" + Convert.ToBase64String(new byte[diagnosticBudget]) + "\nBUFFER|AA==");
        var firstStream = (MemoryStream)Slots(cumulative)[0]!;
        using ((IDisposable)cumulative)
            Assert(Error(cumulative) is not null && Slots(cumulative).Count == 1, "BUFFER总预算也限制多条脚本");
        Assert(!firstStream.CanRead, "Result Dispose失败路径关闭已创建BUFFER");

        var nearLimit = "BUFFER|" + Convert.ToBase64String(new byte[diagnosticBudget - 1]);
        Deny("BUFFER剩余1byte时拒绝同编码长度3byte数据", nearLimit + "\nBUFFER|AAAA", 1);
        var fillLastByte = RunRaw(nearLimit + "\nBUFFER|AA==");
        using ((IDisposable)fillLastByte)
            Assert(Error(fillLastByte) is null && Slots(fillLastByte).Count == 2
                && ((MemoryStream)Slots(fillLastByte)[1]!).Length == 1,
                "BUFFER剩余1byte时接受合法1byte数据");

        var captureScript = "BUFFER|YWJj\nCALL|" + clientType.FullName + "|CaptureBuffer|#0";
        var success = hook.InvokeBridge(captureScript);
        var captured = (Stream)clientType.GetField("CapturedStream")!.GetValue(null)!;
        Assert(success.IndexOf("\"ok\":true", StringComparison.Ordinal) >= 0 && !captured.CanRead,
            "UiHook成功JSON输出后释放BUFFER，client可作为Stream参数读取");
        var failure = hook.InvokeBridge(captureScript + "\n" + Value("Unknown", "l:1"));
        captured = (Stream)clientType.GetField("CapturedStream")!.GetValue(null)!;
        Assert(failure.IndexOf("\"ok\":false", StringComparison.Ordinal) >= 0 && !captured.CanRead,
            "UiHook脚本后续失败仍释放已传给client的BUFFER");
        var clientFailure = hook.InvokeBridge("BUFFER|YWJj\nCALL|" + clientType.FullName + "|CaptureBufferAndThrow|#0");
        captured = (Stream)clientType.GetField("CapturedStream")!.GetValue(null)!;
        Assert(clientFailure.IndexOf("\"ok\":false", StringComparison.Ordinal) >= 0
            && clientFailure.IndexOf("InvalidOperationException", StringComparison.Ordinal) >= 0
            && clientFailure.IndexOf("bridge-sensitive-message-marker", StringComparison.Ordinal) < 0
            && !captured.CanRead, "客户端异常不泄漏原始message且释放已传入BUFFER");
        using (var borrowed = new MemoryStream(new byte[] { 1 }, writable: false))
        {
            clientType.GetField("BorrowedStream")!.SetValue(null, borrowed);
            var borrowedResult = hook.InvokeBridge("CALL|" + clientType.FullName + "|ReturnBorrowedStream");
            Assert(borrowedResult.IndexOf("\"ok\":true", StringComparison.Ordinal) >= 0 && borrowed.CanRead,
                "Result不Dispose客户端返回的其他流对象");
        }
        return (failures.Count == 0, failures.Count == 0
            ? $"{cases} 个内存DTO/流用例通过；成功与失败都释放自有流，无通用SET"
            : string.Join("; ", failures));
    }

    private static (bool, string) Bridge_MetadataSignaturesAndArraysAreRejected()
    {
        // 所有类型均由内存 fake 生成，不加载真实客户端，不调用真实网络实现。
        var assembly = AppDomain.CurrentDomain.DefineDynamicAssembly(
            new AssemblyName("NutstoreClient.MetadataSignatureProbe"), AssemblyBuilderAccess.Run);
        var module = assembly.DefineDynamicModule("MemoryOnly");
        var builder = module.DefineType("NutstoreClient.MetadataSignatureClient", TypeAttributes.Public | TypeAttributes.Class);
        builder.DefineDefaultConstructor(MethodAttributes.Public);
        var unsafeCount = builder.DefineField("UnsafeInvocationCount", typeof(int), FieldAttributes.Public | FieldAttributes.Static);
        var safeCount = builder.DefineField("SafeInvocationCount", typeof(int), FieldAttributes.Public | FieldAttributes.Static);
        var runtimeCount = builder.DefineField("RuntimeReturnCount", typeof(int), FieldAttributes.Public | FieldAttributes.Static);
        var runtimeSource = builder.DefineField("RuntimeSource", typeof(object[]), FieldAttributes.Public | FieldAttributes.Static);

        void Count(ILGenerator il, FieldBuilder counter)
        {
            il.Emit(OpCodes.Ldsfld, counter);
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Add);
            il.Emit(OpCodes.Stsfld, counter);
        }

        foreach (var pair in new[]
                 {
                     (Name: "Type", Type: typeof(Type)),
                     (Name: "TypeArray", Type: typeof(Type[])),
                     (Name: "TypeMatrix", Type: typeof(Type[,])),
                     (Name: "TypeList", Type: typeof(List<Type>)),
                     (Name: "TypeMap", Type: typeof(Dictionary<string, Type>)),
                     (Name: "RefType", Type: typeof(Type).MakeByRefType()),
                     (Name: "OutType", Type: typeof(Type).MakeByRefType()),
                 })
        {
            var method = builder.DefineMethod("Accept" + pair.Name, MethodAttributes.Public | MethodAttributes.Static,
                typeof(void), new[] { pair.Type });
            if (pair.Name == "OutType") method.DefineParameter(1, ParameterAttributes.Out, "metadata");
            var il = method.GetILGenerator();
            Count(il, unsafeCount);
            il.Emit(OpCodes.Ret);
        }

        foreach (var pair in new[]
                 {
                     (Name: "TypeArray", Type: typeof(Type[])),
                     (Name: "TypeList", Type: typeof(List<Type>)),
                 })
        {
            foreach (var isStatic in new[] { true, false })
            {
                var method = builder.DefineMethod("Return" + pair.Name + (isStatic ? "" : "Instance"),
                    MethodAttributes.Public | (isStatic ? MethodAttributes.Static : 0), pair.Type, Type.EmptyTypes);
                var il = method.GetILGenerator();
                Count(il, unsafeCount);
                il.Emit(OpCodes.Ldnull);
                il.Emit(OpCodes.Ret);
            }
            var getter = builder.DefineMethod("get_" + pair.Name, MethodAttributes.Public | MethodAttributes.SpecialName,
                pair.Type, Type.EmptyTypes);
            var getterIl = getter.GetILGenerator();
            Count(getterIl, unsafeCount);
            getterIl.Emit(OpCodes.Ldnull);
            getterIl.Emit(OpCodes.Ret);
            var property = builder.DefineProperty(pair.Name, PropertyAttributes.None, pair.Type, Type.EmptyTypes);
            property.SetGetMethod(getter);
        }

        var consume = builder.DefineMethod("AcceptObjects", MethodAttributes.Public | MethodAttributes.Static,
            typeof(void), new[] { typeof(object[]) });
        var consumeIl = consume.GetILGenerator();
        Count(consumeIl, unsafeCount);
        consumeIl.Emit(OpCodes.Ret);
        var runtimeReturn = builder.DefineMethod("ReturnObjects", MethodAttributes.Public | MethodAttributes.Static,
            typeof(object[]), Type.EmptyTypes);
        var runtimeIl = runtimeReturn.GetILGenerator();
        Count(runtimeIl, runtimeCount);
        runtimeIl.Emit(OpCodes.Ldsfld, runtimeSource);
        runtimeIl.Emit(OpCodes.Ret);

        foreach (var pair in new[]
                 {
                     (Name: "String", Type: typeof(string)),
                     (Name: "Bytes", Type: typeof(byte[])),
                     (Name: "Stream", Type: typeof(Stream)),
                 })
        {
            var method = builder.DefineMethod("Echo" + pair.Name, MethodAttributes.Public | MethodAttributes.Static,
                pair.Type, new[] { pair.Type });
            var il = method.GetILGenerator();
            Count(il, safeCount);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ret);
        }
        var dtoType = AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetType(ClientSymbols.CreateFileParamType, throwOnError: false)).First(t => t != null)!;
        var acceptDto = builder.DefineMethod("AcceptDto", MethodAttributes.Public | MethodAttributes.Static,
            typeof(long), new[] { dtoType });
        var dtoIl = acceptDto.GetILGenerator();
        Count(dtoIl, safeCount);
        dtoIl.Emit(OpCodes.Ldarga_S, (byte)0);
        dtoIl.Emit(OpCodes.Ldfld, dtoType.GetField("size")!);
        dtoIl.Emit(OpCodes.Ret);
        var refBool = builder.DefineMethod("FlipBool", MethodAttributes.Public | MethodAttributes.Static,
            typeof(void), new[] { typeof(bool).MakeByRefType() });
        refBool.DefineParameter(1, ParameterAttributes.None, "full");
        var refIl = refBool.GetILGenerator();
        Count(refIl, safeCount);
        refIl.Emit(OpCodes.Ldarg_0);
        refIl.Emit(OpCodes.Ldc_I4_1);
        refIl.Emit(OpCodes.Stind_I1);
        refIl.Emit(OpCodes.Ret);
        var clientType = builder.CreateType()!;

        var ctorBuilder = module.DefineType("NutstoreClient.MetadataSignatureConstructor", TypeAttributes.Public | TypeAttributes.Class);
        var ctorCountBuilder = ctorBuilder.DefineField("CreationCount", typeof(int), FieldAttributes.Public | FieldAttributes.Static);
        foreach (var parameterType in new[] { typeof(Type), typeof(Type[]), typeof(List<Type>) })
        {
            var ctor = ctorBuilder.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, new[] { parameterType });
            var il = ctor.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, typeof(object).GetConstructor(Type.EmptyTypes)!);
            Count(il, ctorCountBuilder);
            il.Emit(OpCodes.Ret);
        }
        var ctorType = ctorBuilder.CreateType()!;
        var overloadBuilder = module.DefineType("NutstoreClient.MetadataOverloadConstructor", TypeAttributes.Public | TypeAttributes.Class);
        var safeCtorCountBuilder = overloadBuilder.DefineField("SafeCreationCount", typeof(int), FieldAttributes.Public | FieldAttributes.Static);
        var unsafeCtorCountBuilder = overloadBuilder.DefineField("UnsafeCreationCount", typeof(int), FieldAttributes.Public | FieldAttributes.Static);
        var stringField = overloadBuilder.DefineField("StringValue", typeof(string), FieldAttributes.Public);
        foreach (var parameterType in new[] { typeof(string), typeof(Type) })
        {
            var ctor = overloadBuilder.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, new[] { parameterType });
            var il = ctor.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, typeof(object).GetConstructor(Type.EmptyTypes)!);
            Count(il, parameterType == typeof(string) ? safeCtorCountBuilder : unsafeCtorCountBuilder);
            if (parameterType == typeof(string))
            {
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Stfld, stringField);
            }
            il.Emit(OpCodes.Ret);
        }
        var overloadType = overloadBuilder.CreateType()!;
        var safeCtorCounter = overloadType.GetField(safeCtorCountBuilder.Name)!;
        var unsafeCtorCounter = overloadType.GetField(unsafeCtorCountBuilder.Name)!;
        var unsafeCounter = clientType.GetField(unsafeCount.Name)!;
        var safeCounter = clientType.GetField(safeCount.Name)!;
        var runtimeCounter = clientType.GetField(runtimeCount.Name)!;
        var ctorCounter = ctorType.GetField(ctorCountBuilder.Name)!;
        var hook = new UiHook();
        var bridgeType = typeof(UiHook).Assembly.GetType("NutstoreMcp.UiHook.ReflectiveBridge", throwOnError: true)!;
        var rawStatic = bridgeType.GetMethod("InvokeStatic", BindingFlags.NonPublic | BindingFlags.Static)!;
        var rawConstruct = bridgeType.GetMethod("Construct", BindingFlags.NonPublic | BindingFlags.Static)!;
        var failures = new List<string>();
        var cases = 0;
        void Assert(bool condition, string label)
        {
            cases++;
            if (!condition) failures.Add(label);
        }
        void Deny(string label, string script, int completedLines = 0)
        {
            var result = hook.InvokeBridge(script);
            Assert(result.IndexOf("\"ok\":false", StringComparison.Ordinal) >= 0
                && result.IndexOf("\"stepCount\":\"" + completedLines + "\"", StringComparison.Ordinal) >= 0
                && (int)unsafeCounter.GetValue(null)! == 0 && (int)ctorCounter.GetValue(null)! == 0,
                label + "未在实现执行前拒绝：" + result);
        }
        void RejectRaw(string label, Action action)
        {
            var refused = false;
            try { action(); }
            catch (TargetInvocationException ex) when (ex.InnerException is ArgumentException) { refused = true; }
            Assert(refused && (int)unsafeCounter.GetValue(null)! == 0 && (int)ctorCounter.GetValue(null)! == 0,
                label + "未在实现执行前拒绝");
        }
        object? InvokeRaw(string method, params object?[] args) => rawStatic.Invoke(null,
            new object[] { clientType, method, args.ToList(), new Dictionary<string, string>(), 0 });
        var call = "CALL|" + clientType.FullName + "|";
        var newClient = "NEW|" + clientType.FullName;
        foreach (var name in new[] { "Type", "TypeArray", "TypeMatrix", "TypeList", "TypeMap", "RefType", "OutType" })
            Deny("null不能绕过" + name + "参数签名", call + "Accept" + name + "|-");
        foreach (var name in new[] { "TypeArray", "TypeList" })
        {
            Deny("CALL反射容器返回签名" + name, call + "Return" + name);
            Deny("INSTANCE反射容器返回签名" + name, newClient + "\nINSTANCE|0|Return" + name + "Instance", 1);
            Deny("GET反射容器getter签名" + name, newClient + "\nGET|0|" + name, 1);
            Deny("CHAIN反射容器getter签名" + name, newClient + "\nCHAIN|0|" + name, 1);
        }
        Deny("null不能绕过构造反射参数签名", "NEW|" + ctorType.FullName + "|-");
        foreach (var value in new object[] { typeof(string), new Type[] { typeof(string) }, new List<Type> { typeof(string) } })
            RejectRaw("构造反射参数" + value.GetType().Name,
                () => rawConstruct.Invoke(null, new object[] { ctorType, new List<object?> { value } }));
        var stringCtor = hook.InvokeBridge("NEW|" + overloadType.FullName + "|s:YWJj\nGET|0|StringValue");
        Assert(stringCtor.IndexOf("\"ok\":true", StringComparison.Ordinal) >= 0
            && stringCtor.IndexOf("\"value\":\"abc\"", StringComparison.Ordinal) >= 0
            && (int)safeCtorCounter.GetValue(null)! == 1 && (int)unsafeCtorCounter.GetValue(null)! == 0,
            "同参数数量存在Type重载时合法string构造仍执行");
        var nullCtor = hook.InvokeBridge("NEW|" + overloadType.FullName + "|-");
        Assert(nullCtor.IndexOf("\"ok\":false", StringComparison.Ordinal) >= 0
            && nullCtor.IndexOf("AmbiguousMatchException", StringComparison.Ordinal) >= 0
            && (int)safeCtorCounter.GetValue(null)! == 1 && (int)unsafeCtorCounter.GetValue(null)! == 0,
            "string/Type构造null歧义拒绝且不执行任一构造");

        foreach (var value in new object[]
                 {
                     typeof(string), typeof(string).GetMethod("ToString", Type.EmptyTypes)!, assembly, module,
                     (Action)(() => { }),
                 })
        {
            var nested = new object[] { new object[] { value } };
            RejectRaw("嵌套object[]反射参数" + value.GetType().Name, () => InvokeRaw("AcceptObjects", nested));
            clientType.GetField(runtimeSource.Name)!.SetValue(null, nested);
            var before = (int)runtimeCounter.GetValue(null)!;
            var result = hook.InvokeBridge(call + "ReturnObjects");
            Assert(result.IndexOf("\"ok\":false", StringComparison.Ordinal) >= 0
                && (int)runtimeCounter.GetValue(null)! == before + 1,
                "object[]运行时反射返回值未拒绝");
        }
        var cycle = new object[1];
        cycle[0] = cycle;
        Assert(InvokeRaw("EchoString", "abc") as string == "abc", "合法string签名可调用");
        var bytes = new byte[] { 1, 2, 3 };
        Assert(ReferenceEquals(bytes, InvokeRaw("EchoBytes", bytes)), "合法byte[]签名与参数可调用");
        using (var stream = new MemoryStream(bytes, writable: false))
            Assert(ReferenceEquals(stream, InvokeRaw("EchoStream", stream)) && stream.CanRead, "合法只读Stream参数可调用");
        var dtoResult = hook.InvokeBridge("VALUE|" + ClientSymbols.CreateFileParamType + "|size|l:7\n" + call + "AcceptDto|#0");
        Assert(dtoResult.IndexOf("\"ok\":true", StringComparison.Ordinal) >= 0
            && dtoResult.IndexOf("\"value\":\"7\"", StringComparison.Ordinal) >= 0, "合法VALUE DTO参数可调用");
        var refResult = hook.InvokeBridge(call + "FlipBool|b:false");
        Assert(refResult.IndexOf("\"ok\":true", StringComparison.Ordinal) >= 0
            && refResult.IndexOf("full => True", StringComparison.Ordinal) >= 0, "合法ref bool保留写回");
        Assert((int)safeCounter.GetValue(null)! == 5, "五种合法签名均真正执行fake实现");
        // 返回循环数组应有界完成；不能为扫描容器而无限递归。
        clientType.GetField(runtimeSource.Name)!.SetValue(null, cycle);
        var cycleResult = hook.InvokeBridge(call + "ReturnObjects");
        Assert(cycleResult.IndexOf("\"ok\":true", StringComparison.Ordinal) >= 0, "普通循环object[]扫描有界完成");
        return (failures.Count == 0, failures.Count == 0
            ? $"{cases} 个签名/嵌套数组用例通过；危险实现与构造执行次数为0，合法参数与ref bool仍可用"
            : string.Join("; ", failures));
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
