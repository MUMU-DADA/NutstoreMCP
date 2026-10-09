using System.Reflection;
using System.Text.Json;

namespace NutstoreMcp.Plugin.L3;

/// <summary>
/// 在插件子域里加载坚果云客户端程序集，用于调用它的内部 API。
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

            // 按**版本号**倒序，不是按字符串。
            //
            // 字符串倒序是错的：'bin-7.2.9' > 'bin-7.2.13'（因为 '9' > '1'），
            // 于是客户端从 7.2.9 升级到 7.2.13 之后，这里会选中**旧**目录 ——
            // 表现是插件突然读不到正确的客户端程序集，而报错完全指不到原因。
            var root = @"C:\Program Files\Nutstore";
            var best = Directory.Exists(root)
                ? Directory.GetDirectories(root, "bin-*")
                    .Where(d => File.Exists(Path.Combine(d, "NutstoreClient.exe")))
                    .OrderByDescending(VersionKey)
                    .FirstOrDefault()
                : null;

            _clientDir = best ?? string.Empty;
            return _clientDir;
        }
    }

    /// <summary>
    /// 从 <c>bin-7.2.12</c> 这样的目录名里取出可比较的版本号。
    /// 解析不出来时返回最小版本，让它在排序里沉底 —— 宁可漏选也不要选错。
    /// </summary>
    private static Version VersionKey(string directory)
    {
        var name = Path.GetFileName(directory);
        var match = System.Text.RegularExpressions.Regex.Match(name, @"(\d+(?:\.\d+)+)");

        if (!match.Success)
        {
            return new Version(0, 0);
        }

        return Version.TryParse(match.Groups[1].Value, out var v) ? v : new Version(0, 0);
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
        foreach (var dll in ClientAssemblyFiles())
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

    /// <summary>
    /// 报告子域里那些「客户端静态初始化会用到」的基础路径。
    ///
    /// <para>
    /// 客户端代码（<c>NutstoreLib.Utils.DirectoryUtils</c>）的静态构造会拿
    /// 用户 AppData 之类的路径，然后在子域里得到空串，最后炸在
    /// <c>Path.NormalizeLongPath("")</c>。但空的是哪一个，从异常里看不出来 ——
    /// 所以把候选逐个列出来对比。
    /// </para>
    /// </summary>
    public static string ProbeEnvironment()
    {
        object Grab(string name, Func<object?> f)
        {
            try
            {
                var v = f();
                return new { name, ok = true, value = v?.ToString() ?? "(null)" };
            }
            catch (Exception ex)
            {
                return new { name, ok = false, value = $"{ex.GetType().Name}: {ex.Message}" };
            }
        }

        var probes = new[]
        {
            Grab("Environment.ApplicationData", () => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)),
            Grab("Environment.LocalApplicationData", () => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)),
            Grab("Environment.UserProfile", () => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
            Grab("Environment.CommonApplicationData", () => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)),
            Grab("Environment.MyDocuments", () => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)),
            Grab("Environment.Desktop", () => Environment.GetFolderPath(Environment.SpecialFolder.Desktop)),
            Grab("Environment.CurrentDirectory", () => Environment.CurrentDirectory),
            Grab("Environment.UserName", () => Environment.UserName),
            Grab("AppDomain.BaseDirectory", () => AppDomain.CurrentDomain.BaseDirectory),
            Grab("AppDomain.RelativeSearchPath", () => AppDomain.CurrentDomain.RelativeSearchPath),
            Grab("AppDomain.FriendlyName", () => AppDomain.CurrentDomain.FriendlyName),
            Grab("AppDomain.ConfigurationFile", () => AppDomain.CurrentDomain.SetupInformation.ConfigurationFile),
            Grab("Assembly.GetEntryAssembly", () => Assembly.GetEntryAssembly()?.Location),
            Grab("Assembly.GetExecutingAssembly", () => Assembly.GetExecutingAssembly().Location),
            Grab("Env.NUTSTORE_HOME", () => Environment.GetEnvironmentVariable("NUTSTORE_HOME")),
            Grab("Env.NUTSTORE_INSTALL_DIR", () => Environment.GetEnvironmentVariable("NUTSTORE_INSTALL_DIR")),
            Grab("Env.APPDATA", () => Environment.GetEnvironmentVariable("APPDATA")),
            Grab("Env.USERPROFILE", () => Environment.GetEnvironmentVariable("USERPROFILE")),
        };

        return Json(new
        {
            domain = AppDomain.CurrentDomain.FriendlyName,
            isDefaultDomain = AppDomain.CurrentDomain.IsDefaultAppDomain(),
            probes,
            entryAssemblyExperiment = ExperimentOnEntryAssembly(),
        });
    }

    /// <summary>
    /// 验证「<c>GetEntryAssembly()</c> 为空是因为入口程序集没被加载进这个域」这个假设。
    ///
    /// <para>
    /// 若假设成立，客户端那些依赖它的静态初始化就能在子域里跑起来 ——
    /// L3 的读写路径都不再需要重启客户端。若假设不成立，就得老老实实走默认域。
    /// 这个实验决定了后续整个架构方向，所以值得单独做一次。
    /// </para>
    /// </summary>
    private static object ExperimentOnEntryAssembly()
    {
        string Entry() => Assembly.GetEntryAssembly()?.Location ?? "(null)";
        int Loaded() => AppDomain.CurrentDomain.GetAssemblies().Length;

        var beforeEntry = Entry();
        var beforeCount = Loaded();

        var exePath = Path.Combine(ClientDirectory, "NutstoreClient.exe");
        string loadResult;

        try
        {
            if (!File.Exists(exePath))
            {
                loadResult = $"客户端 exe 不存在: {exePath}";
            }
            else
            {
                var asm = Assembly.Load(File.ReadAllBytes(exePath));
                loadResult = $"已加载 {asm.GetName().Name}";
            }
        }
        catch (Exception ex)
        {
            loadResult = $"{ex.GetType().Name}: {ex.Message}";
        }

        var afterEntry = Entry();
        var afterCount = Loaded();

        return new
        {
            hypothesis = "GetEntryAssembly 为空是因为入口程序集没被加载进子域",
            exePath,
            loadResult,
            before = new { entryAssembly = beforeEntry, loadedCount = beforeCount },
            after = new { entryAssembly = afterEntry, loadedCount = afterCount },
            hypothesisHolds = !string.Equals(afterEntry, "(null)", StringComparison.Ordinal),
        };
    }

    /// <summary>
    /// 按名字模式搜索客户端所有类型。
    ///
    /// <para>
    /// 没有这个就只能靠猜类型名 —— 而客户端的类型散在十几个程序集里、
    /// 命名空间还经常和直觉不符（<c>NSSandbox</c> 不在 <c>NutstoreClient.DAO</c>，
    /// 实际落在 <c>NutstoreClient.Legacy.dll</c>）。猜错的成本是每猜一次一轮往返。
    /// </para>
    /// </summary>
    public static string SearchTypes(string pattern, int limit)
    {
        if (!Available)
        {
            return Json(new { ok = false, reason = "找不到客户端目录" });
        }

        var cap = Math.Max(1, Math.Min(limit, 300));
        var hits = new List<string>();
        var scanned = 0;
        var failed = new List<string>();

        foreach (var asm in LoadAllClientAssemblies(failed))
        {
            Type[] types;
            try
            {
                types = asm.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                // 缺依赖时部分类型加载不出来，用能拿到的那部分
                types = ex.Types.Where(t => t is not null).ToArray()!;
            }
            catch (Exception)
            {
                continue;
            }

            foreach (var t in types)
            {
                scanned++;
                var name = t.FullName ?? t.Name;
                if (name.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                if (hits.Count >= cap)
                {
                    break;
                }

                var kind = t.IsInterface ? "interface"
                    : t.IsEnum ? "enum"
                    : t.IsValueType ? "struct"
                    : (t.IsAbstract && t.IsSealed) ? "static class"
                    : "class";

                hits.Add($"{kind} {name}  [{asm.GetName().Name}]");
            }
        }

        return Json(new
        {
            ok = true,
            pattern,
            scannedTypes = scanned,
            matched = hits.Count,
            truncated = hits.Count >= cap,
            types = hits,
            assembliesThatFailedToLoad = failed,
        });
    }

    private static readonly List<Assembly> ClientAssemblyCache = [];
    private static readonly HashSet<string> ClientAssemblyFailures = [];

    /// <summary>
    /// 加载客户端目录下的所有托管程序集（带缓存）。
    /// 加载不了的记下来 —— 排查时「哪些没加载」本身就是线索。
    /// </summary>
    private static List<Assembly> LoadAllClientAssemblies(List<string> failures)
    {
        if (ClientAssemblyCache.Count == 0)
        {
            foreach (var dll in ClientAssemblyFiles())
            {
                var name = Path.GetFileNameWithoutExtension(dll);
                try
                {
                    var asm = Load(name) ?? Assembly.Load(File.ReadAllBytes(dll));

                    // 只收托管程序集：原生 DLL 会在 Load 时抛 BadImageFormatException
                    ClientAssemblyCache.Add(asm);
                }
                catch (Exception)
                {
                    ClientAssemblyFailures.Add(name);
                }
            }
        }

        failures.AddRange(ClientAssemblyFailures);
        return ClientAssemblyCache;
    }

    /// <summary>
    /// 客户端目录下所有可能含类型的托管文件：全部 dll **加上主 exe**。
    /// </summary>
    /// <remarks>
    /// 只扫 *.dll 是个真实的坑：Nutstore.Client.Wpf.Services.* 这些类型
    /// （设置相关的服务）住在 NutstoreClient.exe 里，按名字搜会一直落空 ——
    /// 落空的表现是「看起来没这个能力」，而不是报错。
    /// </remarks>
    private static IEnumerable<string> ClientAssemblyFiles()
    {
        foreach (var dll in Directory.GetFiles(ClientDirectory, "*.dll"))
        {
            yield return dll;
        }

        var exe = Path.Combine(ClientDirectory, "NutstoreClient.exe");
        if (File.Exists(exe))
        {
            yield return exe;
        }
    }

    private static string Json(object o) => System.Text.Json.JsonSerializer.Serialize(o, new JsonSerializerOptions
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });
}
