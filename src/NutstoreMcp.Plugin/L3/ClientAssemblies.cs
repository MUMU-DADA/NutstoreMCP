using System.Reflection;
using System.Text.Json;

namespace NutstoreMcp.Plugin.L3;

/// <summary>
/// 在**插件的独立子域**里加载坚果云客户端自己的程序集，从而调用它的内部 API。
///
/// <para>
/// 为什么不去默认域调：默认域有 UI 钩子，但钩子的改动要重启客户端才生效。
/// 而插件在子域里、可以热重载 —— 把客户端 API 的调用放在这里，迭代成本低得多。
/// </para>
///
/// <para><b>为什么这样是安全的</b>：子域和客户端默认域是隔离的。即使客户端程序集
/// 在子域里加载失败、类型初始化炸掉，**崩的也只是子域**，MCP 连接断开而已，
/// 客户端的同步线程完全不受影响。这正是当初坚持 AppDomain 隔离换来的余量。</para>
///
/// <para>客户端的依赖（Prism / Unity / log4net 等）都在它自己的安装目录里，
/// 用一个 AssemblyResolve 兜住。</para>
/// </summary>
internal static class ClientAssemblies
{
    private static readonly object Gate = new();
    private static string? _clientDir;
    private static bool _resolveHooked;

    /// <summary>客户端安装目录。写死版本号是有意的 —— 升级后目录会变，需要重新探测。</summary>
    public static string ClientDirectory
    {
        get
        {
            if (_clientDir is not null)
            {
                return _clientDir;
            }

            // 按版本号倒序找最新的 bin-* 目录
            var root = @"C:\Program Files\Nutstore";
            var best = Directory.Exists(root)
                ? Directory.GetDirectories(root, "bin-*")
                    .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault(d => File.Exists(Path.Combine(d, "NutstoreClient.exe")))
                : null;

            _clientDir = best ?? string.Empty;
            return _clientDir;
        }
    }

    public static bool Available => ClientDirectory.Length > 0;

    /// <summary>把客户端程序集加载进当前（插件）子域。</summary>
    public static Assembly? Load(string assemblySimpleName)
    {
        if (!Available)
        {
            return null;
        }

        lock (Gate)
        {
            if (!_resolveHooked)
            {
                AppDomain.CurrentDomain.AssemblyResolve += ResolveFromClientDir;
                _resolveHooked = true;
            }

            var already = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => string.Equals(a.GetName().Name, assemblySimpleName, StringComparison.OrdinalIgnoreCase));

            if (already is not null)
            {
                return already;
            }

            var path = Path.Combine(ClientDirectory, assemblySimpleName + ".dll");
            if (!File.Exists(path))
            {
                return null;
            }

            try
            {
                // 按字节加载：不锁文件，客户端升级/修复时不会撞上占用
                return Assembly.Load(File.ReadAllBytes(path));
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    /// <summary>客户端的依赖一律从它的安装目录解析。</summary>
    private static Assembly? ResolveFromClientDir(object? sender, ResolveEventArgs e)
    {
        try
        {
            var simpleName = new AssemblyName(e.Name).Name;
            if (simpleName is null || !Available)
            {
                return null;
            }

            var candidate = Path.Combine(ClientDirectory, simpleName + ".dll");
            if (!File.Exists(candidate))
            {
                // 有些依赖是 exe
                candidate = Path.Combine(ClientDirectory, simpleName + ".exe");
                if (!File.Exists(candidate))
                {
                    return null;
                }
            }

            return Assembly.Load(File.ReadAllBytes(candidate));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// 反射探查一个客户端类型的成员。用来在没有重启的情况下搞清楚 API 长什么样 ——
    /// 区分静态/实例、参数类型，这些从元数据清单里看不出来。
    /// </summary>
    public static string Reflect(string typeName, string memberFilter, int limit)
    {
        var type = FindType(typeName);
        if (type is null)
        {
            return Json(new
            {
                ok = false,
                reason = $"在客户端目录里找不到类型 {typeName}",
                clientDirectory = ClientDirectory,
            });
        }

        var cap = Math.Max(1, Math.Min(limit, 300));
        var filter = memberFilter ?? string.Empty;
        bool Match(string name) => filter.Length == 0
            || name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;

        var ctors = type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(c => Match(".ctor"))
            .Take(cap)
            .Select(c => $"{(c.IsPublic ? "public" : "non-public")} .ctor({Sig(c.GetParameters())})")
            .ToList();

        var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName && Match(m.Name))
            .Take(cap)
            .Select(m => $"{(m.IsStatic ? "static" : "instance")} {Pretty(m.ReturnType)} {m.Name}({Sig(m.GetParameters())})")
            .ToList();

        var props = type.GetProperties(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(p => Match(p.Name))
            .Take(cap)
            .Select(p => $"{(p.GetMethod?.IsStatic == true ? "static" : "instance")} {Pretty(p.PropertyType)} {p.Name}")
            .ToList();

        var fields = type.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(f => Match(f.Name))
            .Take(cap)
            .Select(f => $"{(f.IsStatic ? "static" : "instance")} {Pretty(f.FieldType)} {f.Name}")
            .ToList();

        return Json(new
        {
            ok = true,
            type = type.FullName,
            assembly = type.Assembly.GetName().Name,
            baseType = type.BaseType?.FullName,
            isStatic = type.IsAbstract && type.IsSealed,
            hasDefaultCtor = type.GetConstructor(Type.EmptyTypes) is not null,
            ctors,
            methods,
            props,
            fields,
        });
    }

    /// <summary>
    /// 跨客户端所有程序集找一个类型。
    ///
    /// 不能假定它在哪个程序集里 —— <c>NSSandboxDAO</c> 在 NutstoreClient.Legacy，
    /// 而它操作的模型类型 <c>NSSandbox</c> 在别的程序集。按名字猜会一直猜错。
    /// </summary>
    public static Type? FindType(string fullName)
    {
        if (!Available)
        {
            return null;
        }

        // 先看已加载的
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
                // 跳过
            }
        }

        // 再逐个加载客户端目录里的托管程序集
        foreach (var dll in Directory.GetFiles(ClientDirectory, "*.dll"))
        {
            Assembly asm;
            try
            {
                var name = Path.GetFileNameWithoutExtension(dll);
                asm = Load(name) ?? Assembly.Load(File.ReadAllBytes(dll));
            }
            catch (Exception)
            {
                continue;
            }

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

    private static string Sig(ParameterInfo[] ps) => string.Join(", ", ps.Select(p => $"{Pretty(p.ParameterType)} {p.Name}"));

    private static string Pretty(Type t)
    {
        if (t.IsGenericType)
        {
            var name = t.Name;
            var tick = name.IndexOf('`');
            if (tick > 0)
            {
                name = name.Substring(0, tick);
            }

            return name + "<" + string.Join(",", t.GetGenericArguments().Select(Pretty)) + ">";
        }

        return t.Name;
    }

    private static string Json(object o) => System.Text.Json.JsonSerializer.Serialize(o, new JsonSerializerOptions
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });
}
