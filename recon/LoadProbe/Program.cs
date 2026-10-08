using System.Reflection;
using System.Security.Principal;

// 验证能不能把坚果云客户端自己的程序集加载起来、并用它自带的 gRPC 桩。
//
// 思路：客户端里既有服务端实现（NutstoreClient.NamedPipeServerImpl.GrpcBadgeNamedPipeService），
// 也有配套的客户端桩（.NutstoreBadge/NutstoreBadgeClient）。直接复用后者，
// 就不需要自己复刻 GrpcDotNetNamedPipes 的帧协议 —— 版本一定对得上。
//
// 客户端程序集是 .NET Framework 4.6.2；本探针用 net48（Windows 内置）加载它。

var clientDir = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal))
                ?? @"C:\Program Files\Nutstore\bin-7.2.12";

Console.WriteLine($"客户端目录: {clientDir}");
Console.WriteLine($"当前运行时: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
Console.WriteLine();

// 客户端的一堆依赖都在同一个目录里，用 AssemblyResolve 兜住
AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
{
    var simpleName = new AssemblyName(e.Name).Name;
    var candidate = Path.Combine(clientDir, simpleName + ".dll");
    if (File.Exists(candidate))
    {
        return Assembly.LoadFrom(candidate);
    }

    var exe = Path.Combine(clientDir, simpleName + ".exe");
    return File.Exists(exe) ? Assembly.LoadFrom(exe) : null;
};

foreach (var asmName in new[] { "NutstoreClient.Legacy.dll", "Nutstore.Client.Badge.exe" })
{
    var path = Path.Combine(clientDir, asmName);
    Console.WriteLine($"── {asmName} ──");
    if (!File.Exists(path))
    {
        Console.WriteLine("  文件不存在");
        continue;
    }

    try
    {
        var asm = Assembly.LoadFrom(path);
        Console.WriteLine($"  ✅ 已加载: {asm.FullName}");

        Type[] types;
        try
        {
            types = asm.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types.Where(t => t is not null).ToArray();
            Console.WriteLine($"  ⚠️ 部分类型加载失败（{ex.LoaderExceptions.Length} 个），继续分析能拿到的 {types.Length} 个");
            foreach (var le in ex.LoaderExceptions.Take(3))
            {
                Console.WriteLine($"       {le?.Message.Split('\n')[0]}");
            }
        }

        var badge = types.Where(t => t.Name.Contains("NutstoreBadge")).ToList();
        Console.WriteLine($"  含 NutstoreBadge 的类型（{badge.Count} 个）:");
        foreach (var t in badge)
        {
            Console.WriteLine($"     {t.FullName}{(t.IsNested ? "  [nested]" : string.Empty)}");
        }

        var svc = badge.FirstOrDefault(t => t.Name.Contains("Client") && t.IsNested);
        if (svc is not null)
        {
            Console.WriteLine($"  ── {svc.FullName} 的公开方法（去重后）──");
            var seen = new HashSet<string>();
            foreach (var m in svc.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (!m.Name.EndsWith("Async", StringComparison.Ordinal) || !seen.Add(m.Name))
                {
                    continue;
                }

                var ps = string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name));
                Console.WriteLine($"     {m.Name}({ps})");
            }
        }

        // gRPC 的 ServiceDescriptor 就是完整契约：方法名 + 请求/响应消息类型。
        // 消息类型的 MessageDescriptor 再给出完整字段表 —— 合起来等于还原出 .proto。
        DumpContract(badge.FirstOrDefault(t => t.Name == "NutstoreBadge" && !t.IsNested));
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  ❌ {ex.GetType().Name}: {ex.Message.Split('\n')[0]}");
    }

    Console.WriteLine();
}

// ── 隔离实验：不加载任何客户端程序集，纯 .NET Framework 直接连管道 ──
// 用来区分「库的参数问题」和「进程/上下文问题」
if (args.Contains("--raw"))
{
    RawConnect();
    return 0;
}

// ── 实际调用一次，验证「能连上」不等于「能对话」 ──
if (args.Contains("--call"))
{
    TryCall();
}

return 0;

static void RawConnect()
{
    const string pipeName = "BadgeGrpcNamedPipe.BadgeToNutstoreV3";
    Console.WriteLine($"══ 纯净连接实验（{System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}）══");

    foreach (var level in new[]
             {
                 TokenImpersonationLevel.None,
                 TokenImpersonationLevel.Anonymous,
                 TokenImpersonationLevel.Identification,
                 TokenImpersonationLevel.Impersonation,
                 TokenImpersonationLevel.Delegation,
             })
    {
        try
        {
            using var c = new System.IO.Pipes.NamedPipeClientStream(
                ".", pipeName,
                System.IO.Pipes.PipeDirection.InOut,
                System.IO.Pipes.PipeOptions.Asynchronous,
                level);
            c.Connect(800);
            Console.WriteLine($"  ✅ ImpersonationLevel={level,-14} CanRead={c.CanRead} CanWrite={c.CanWrite}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ❌ ImpersonationLevel={level,-14} {ex.GetType().Name}");
        }
    }
}

// 在已加载的程序集里按名字找类型（依赖被 ILMerge 进客户端程序集，没有独立 DLL）
static Type FindType(string fullName)
{
    foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
    {
        try
        {
            var t = a.GetType(fullName, throwOnError: false);
            if (t is not null)
            {
                return t;
            }
        }
        catch (Exception)
        {
            // 某些程序集在反射时会抛，跳过
        }
    }

    return null;
}

static void TryCall()
{
    const string clientDir = @"C:\Program Files\Nutstore\bin-7.2.12";
    const string pipeName = "BadgeGrpcNamedPipe.BadgeToNutstoreV3";
    Console.WriteLine("══ 实际调用测试 ══");
    try
    {
        var asm = Assembly.LoadFrom(Path.Combine(clientDir, "NutstoreClient.Legacy.dll"));

        // 用客户端自带的 GrpcDotNetNamedPipes —— 自己引 NuGet 版本会因为帧协议
        // 不匹配被服务端断开（实测 "connection was unexpectedly terminated"）
        var channelType = asm.GetType("GrpcDotNetNamedPipes.NamedPipeChannel")
            ?? throw new InvalidOperationException("找不到 NamedPipeChannel");

        Console.WriteLine("  ── NamedPipeChannel 的构造函数 ──");
        foreach (var c in channelType.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            var ps = string.Join(", ", c.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
            Console.WriteLine($"     .ctor({ps})");
        }

        var optType = asm.GetType("GrpcDotNetNamedPipes.NamedPipeChannelOptions");
        if (optType is not null)
        {
            Console.WriteLine("  ── NamedPipeChannelOptions 属性 ──");
            foreach (var p in optType.GetProperties())
            {
                Console.WriteLine($"     {p.PropertyType.Name} {p.Name}");
            }
        }

        // 注意：第一个参数是**机器名**，"." 表示本机（库会拼成 \\.\pipe\<name>）。
        // 传 "localhost" 会得到 "failed to connect to all addresses"。
        //
        // 另一个坑：NamedPipeChannelOptions.ImpersonationLevel 会影响 CreateFile 的
        // SECURITY_SQOS_PRESENT 标志。级别过高会被管道的 ACL 直接拒掉
        // （表现为 UnauthorizedAccessException）。逐个试。
        object channel = null;
        var optTypeForLevel = asm.GetType("GrpcDotNetNamedPipes.NamedPipeChannelOptions");
        foreach (var level in new[]
                 {
                     TokenImpersonationLevel.None,
                     TokenImpersonationLevel.Identification,
                     TokenImpersonationLevel.Impersonation,
                 })
        {
            try
            {
                var opts = Activator.CreateInstance(optTypeForLevel!);
                optTypeForLevel!.GetProperty("ImpersonationLevel")!.SetValue(opts, level);
                channel = Activator.CreateInstance(channelType, ".", pipeName, opts);
                Console.WriteLine($"  channel: NamedPipeChannel(\".\", \"{pipeName}\", ImpersonationLevel={level})");
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ImpersonationLevel={level} 构造失败: {ex.GetType().Name}");
            }
        }

        if (channel is null)
        {
            throw new InvalidOperationException("创建 channel 失败");
        }

        Console.WriteLine($"  channel: {channelType.FullName} → {pipeName}");

        var badgeType = asm.GetType("NutstoreBadge")
            ?? throw new InvalidOperationException("找不到 NutstoreBadge");
        var clientType = badgeType.GetNestedType("NutstoreBadgeClient")
            ?? throw new InvalidOperationException("找不到 NutstoreBadgeClient");
        var client = Activator.CreateInstance(clientType, channel)
            ?? throw new InvalidOperationException("创建 client 失败");
        Console.WriteLine($"  client : {clientType.FullName}");

        // Google.Protobuf 被 ILMerge 进客户端自己的程序集了，没有独立 DLL。
        // 所以在已加载的程序集里找 Empty。
        var emptyType = FindType("Google.Protobuf.WellKnownTypes.Empty")
            ?? throw new InvalidOperationException("找不到 Google.Protobuf.WellKnownTypes.Empty");
        var empty = Activator.CreateInstance(emptyType)!;

        var method = clientType.GetMethods()
            .First(m => m.Name == "GetBadgeServerTokenAsync")
            .GetParameters()[0].ParameterType == emptyType
            ? clientType.GetMethods().First(m => m.Name == "GetBadgeServerTokenAsync")
            : null;

        if (method is null)
        {
            Console.WriteLine("  找不到 GetBadgeServerTokenAsync(Empty)");
            return;
        }

        Console.WriteLine($"  调用   : GetBadgeServerToken({emptyType.Name}) …");

        // Metadata 也来自被合并的 Grpc.Core.Api，用反射造一个空实例
        var metadataType = FindType("Grpc.Core.Metadata")
            ?? throw new InvalidOperationException("找不到 Grpc.Core.Metadata");
        var metadata = Activator.CreateInstance(metadataType)!;

        var call = method.Invoke(client, [empty, metadata, null, CancellationToken.None]);
        if (call is null)
        {
            Console.WriteLine("  返回 null");
            return;
        }

        // ⚠️ 不要用 ToString()/JsonFormatter —— 它们会触发 protobuf 的描述符反射，
        // 而客户端的描述符元数据在客户端进程外初始化失败（混淆后的 .cctor 抛 NRE）。
        // 直接读生成的属性即可 —— 属性名被混淆了，但值是明文。
        var responseTask = call.GetType().GetProperty("ResponseAsync")!.GetValue(call);
        var result = responseTask!.GetType().GetProperty("Result")!.GetValue(responseTask);

        Console.WriteLine($"  ✅ 调用成功，返回类型: {result!.GetType().FullName}");

        // 逐个字段读（生成的属性名被混淆了，但值是可读的）
        foreach (var p in result.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (p.GetIndexParameters().Length > 0 || p.Name == "Descriptor" || p.Name == "Parser")
            {
                continue;
            }

            try
            {
                var v = p.GetValue(result);
                var s = v?.ToString() ?? "null";
                if (s.Length > 120)
                {
                    s = s.Substring(0, 120) + "…";
                }

                Console.WriteLine($"     {p.Name} = {s}");
            }
            catch (Exception)
            {
                // 个别属性可能依赖反射元数据，跳过
            }
        }
    }
    catch (TargetInvocationException ex)
    {
        PrintStack(ex.InnerException ?? ex);
    }
    catch (Exception ex)
    {
        PrintStack(ex);
    }
}

// 把 AggregateException / TargetInvocationException 层层剥开，露出真正的 RpcException
static void PrintException(Exception ex)
{
    var depth = 0;
    while (ex is not null && depth++ < 5)
    {
        Console.WriteLine($"  ❌ [{depth}] {ex.GetType().Name}: {ex.Message.Split('\n')[0]}");

        // RpcException 的 Status 里有关键信息（服务端到底说了什么）
        var status = ex.GetType().GetProperty("Status")?.GetValue(ex);
        if (status is not null)
        {
            var code = status.GetType().GetProperty("StatusCode")?.GetValue(status);
            var detail = status.GetType().GetProperty("Detail")?.GetValue(status);
            Console.WriteLine($"       StatusCode = {code}");
            Console.WriteLine($"       Detail     = {detail}");
        }

        ex = ex.InnerException;
    }
}

// 打印堆栈，定位到底哪一步被拒
static void PrintStack(Exception ex)
{
    var depth = 0;
    while (ex is not null && depth++ < 6)
    {
        Console.WriteLine($"  ── [{depth}] {ex.GetType().FullName}");
        Console.WriteLine($"     {ex.Message.Split('\n')[0]}");
        var frames = (ex.StackTrace ?? string.Empty).Split('\n');
        foreach (var f in frames.Take(4))
        {
            Console.WriteLine($"     {f.Trim()}");
        }

        ex = ex.InnerException;
    }
}

// 全部走反射：探针不引用（也无法引用）被混淆的客户端程序集
static void DumpContract(Type serviceType)
{
    if (serviceType is null)
    {
        return;
    }

    var descProp = serviceType.GetProperty("Descriptor", BindingFlags.Public | BindingFlags.Static);
    var descriptor = descProp?.GetValue(null);
    if (descriptor is null)
    {
        Console.WriteLine("  找不到 ServiceDescriptor");
        return;
    }

    var dType = descriptor.GetType();
    Console.WriteLine();
    Console.WriteLine($"══ 服务契约：{dType.GetProperty("FullName")?.GetValue(descriptor)} ══");

    var methods = ((System.Collections.IEnumerable)dType.GetProperty("Methods")!.GetValue(descriptor)!)
        .Cast<object>()
        .ToList();

    Console.WriteLine($"方法数: {methods.Count}");
    Console.WriteLine();

    var dumped = new HashSet<string>();

    foreach (var m in methods)
    {
        var mt = m.GetType();
        var name = mt.GetProperty("Name")!.GetValue(m);
        var kind = mt.GetProperty("Type")!.GetValue(m);
        var input = mt.GetProperty("InputType")!.GetValue(m);
        var output = mt.GetProperty("OutputType")!.GetValue(m);

        string InputName(object d) => d.GetType().GetProperty("Name")!.GetValue(d)!.ToString()!;

        Console.WriteLine($"  {kind,-12} {name}({InputName(input!)}) -> {InputName(output!)}");

        DumpMessage(input!, dumped);
        DumpMessage(output!, dumped);
    }
}

static void DumpMessage(object messageDescriptor, HashSet<string> dumped)
{
    var dt = messageDescriptor.GetType();
    var name = dt.GetProperty("Name")!.GetValue(messageDescriptor)!.ToString()!;
    if (!dumped.Add(name))
    {
        return;
    }

    var fields = dt.GetProperty("Fields")!.GetValue(messageDescriptor);
    Console.WriteLine($"      message {name} {{");
    foreach (var f in ((System.Collections.IEnumerable)fields!).Cast<object>())
    {
        var ft = f.GetType();
        var fname = ft.GetProperty("Name")!.GetValue(f);
        var number = ft.GetProperty("FieldNumber")!.GetValue(f);
        var ftype = ft.GetProperty("FieldType")!.GetValue(f);
        var isRepeated = ft.GetProperty("IsRepeated")!.GetValue(f);
        var rep = (bool)isRepeated! ? "repeated " : string.Empty;
        Console.WriteLine($"        {rep}{ftype} {fname} = {number};");
    }

    Console.WriteLine("      }");
}
