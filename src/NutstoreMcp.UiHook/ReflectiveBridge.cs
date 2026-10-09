// 在客户端域内执行受限反射脚本。
using NutstoreMcp.ClientBindings;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;

namespace NutstoreMcp.UiHook
{
    /// <summary>
    /// 通用反射桥：让插件侧在**不修改钩子**的前提下调用客户端的任意实现。
    /// </summary>
    /// <remarks>
    /// **为什么需要它**：钩子跑在客户端默认域，而 .NET Framework 卸载不了默认域
    /// 已加载的程序集。所以每加一个客户端能力都要改钩子 → 重启客户端 →
    /// 打断用户正在做的事。这个桥把「加能力」的成本从「重启」降到「改插件」。
    ///
    /// **脚本格式**：一行一个操作，`|` 分隔字段。行号即槽位号（从 0 开始），
    /// 每行的结果存进对应槽位，后续行可以用 `#N` 引用。
    ///
    ///   NEW|&lt;类型全名&gt;|参数...        构造实例
    ///   CALL|&lt;类型全名&gt;|方法名|参数...  静态调用
    ///   GET|&lt;槽位&gt;|成员名             取属性或字段（结果存本行槽位）
    ///
    /// 参数编码：
    ///   `s:&lt;base64&gt;` 字符串（base64 避免分隔符和转义问题）
    ///   `l:&lt;数字&gt;`   long
    ///   `i:&lt;数字&gt;`   int
    ///   `b:true/false`  布尔
    ///   `#&lt;槽位&gt;`     引用前面某行的结果
    ///   `-`             null
    ///
    /// **安全边界**（两道，都在这一层，不依赖调用方自觉）：
    ///   1. 只允许操作**客户端自己的程序集**里的类型 —— 不能拿它当通用 .NET 执行器
    ///   2. 危险方法名一律拒绝（删除、迁移、注销同步根等），即使调用方要求
    ///
    /// 写操作的策略（解锁令牌、dryRun、白名单）仍然在插件侧 ——
    /// 这里是机制，那里是策略。
    /// </remarks>
    internal static class ReflectiveBridge
    {
        /// <summary>允许操作的程序集名前缀。只认客户端自己的程序集。</summary>
        private static readonly string[] AllowedAssemblyPrefixes =
        [
            "NutstoreClient",
            "Nutstore",
            "NutstoreLib",
        ];

        /// <summary>
        /// 危险方法名 —— 出现即拒绝，无论调用方是谁。
        /// </summary>
        /// <remarks>
        /// 这是**兜底**，不是唯一防线。插件侧的 ForbiddenClientMethods 是主防线，
        /// 但通用桥的存在意味着绕过插件直接调桥是可能的，所以这里必须再挡一道。
        /// </remarks>
        private static readonly string[] ForbiddenMethods = ClientSymbols.ForbiddenMethodNames;

        public sealed class Result
        {
            public List<string> Lines { get; } = new List<string>();
            public List<object?> Slots { get; } = new List<object?>();
            public string? Error { get; set; }

        /// <summary>
        /// 被方法改写的 ref / out 参数。键是 "行号.参数序号"。
        /// </summary>
        /// <remarks>
        /// MethodInfo.Invoke 会把 ref/out 的写回放进传入的 object[] ——
        /// 不把这一步暴露出来，调用方就看不到方法**输出**了什么，
        /// 只能看到返回值。而有些客户端方法的输出恰恰在 ref 参数上
        /// （例如下载方法用 ref bool 告诉你拿到的是不是全量）。
        /// </remarks>
        public Dictionary<string, string> RefUpdates { get; } = new Dictionary<string, string>();
        }

        public static Result Run(string script)
        {
            var result = new Result();

            if (string.IsNullOrWhiteSpace(script))
            {
                result.Error = "脚本为空";
                return result;
            }

            var lines = script.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();

                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                {
                    result.Slots.Add(null);
                    continue;
                }

                try
                {
                    result.Slots.Add(ExecuteLine(line, result.Slots, i, result.RefUpdates, i));
                    result.Lines.Add("ok");
                }
                catch (Exception ex)
                {
                    var inner = ex is TargetInvocationException tie && tie.InnerException != null
                        ? tie.InnerException
                        : ex;

                    result.Error = $"第 {i} 行失败: {inner.GetType().Name}: {inner.Message}";
                    result.Lines.Add("fail");
                    return result;
                }
            }

            return result;
        }

        private static object? ExecuteLine(
            string line, List<object?> slots, int lineIndex,
            Dictionary<string, string> refUpdates, int scriptLine)
        {
            var parts = line.Split('|');
            var op = parts[0].Trim().ToUpperInvariant();

            switch (op)
            {
                case "NEW":
                {
                    if (parts.Length < 2) throw new ArgumentException("NEW 需要类型名");
                    var type = ResolveClientType(parts[1]);
                    var args = DecodeArgs(parts, 2, slots);
                    return Construct(type, args);
                }

                case "CALL":
                {
                    if (parts.Length < 3) throw new ArgumentException("CALL 需要类型名和方法名");
                    var type = ResolveClientType(parts[1]);
                    var methodName = parts[2].Trim();
                    GuardMethod(methodName);
                    var args = DecodeArgs(parts, 3, slots);
                    return InvokeStatic(type, methodName, args, refUpdates, scriptLine);
                }

                case "INSTANCE":
                {
                    // INSTANCE|<槽位>|<方法名>|参数...
                    if (parts.Length < 3) throw new ArgumentException("INSTANCE 需要槽位和方法名");
                    var target = ReadSlot(parts[1], slots);
                    if (target is null) throw new ArgumentException($"槽位 {parts[1]} 是 null");

                    var methodName = parts[2].Trim();
                    GuardMethod(methodName);
                    var args = DecodeArgs(parts, 3, slots);
                    return InvokeInstance(target, methodName, args, refUpdates, scriptLine);
                }

                case "GET":
                {
                    if (parts.Length < 3) throw new ArgumentException("GET 需要槽位和成员名");
                    var target = ReadSlot(parts[1], slots);
                    return target is null ? null : ReadMember(target, parts[2].Trim());
                }

                case "ENUM":
                {
                    if (parts.Length < 3) throw new ArgumentException("ENUM 需要类型名和成员名");
                    var type = ResolveClientType(parts[1]);
                    return Enum.Parse(type, parts[2].Trim(), ignoreCase: true);
                }

                case "CHAIN":
                {
                    // CHAIN|起始槽位|成员名|成员名|...   沿属性/字段链一路取下去
                    if (parts.Length < 3) throw new ArgumentException("CHAIN 需要起始槽位和至少一个成员名");
                    var current = ReadSlot(parts[1], slots);

                    for (var k = 2; k < parts.Length && current != null; k++)
                    {
                        current = ReadMember(current, parts[k].Trim());
                    }

                    return current;
                }

                default:
                    throw new ArgumentException($"未知操作 {op}（支持 NEW / CALL / INSTANCE / GET / CHAIN / ENUM）");
            }
        }

        private static List<object?> DecodeArgs(string[] parts, int from, List<object?> slots)
        {
            var args = new List<object?>();

            for (var i = from; i < parts.Length; i++)
            {
                var token = parts[i];
                if (token.Length == 0) { args.Add(null); continue; }

                switch (token[0])
                {
                    case '#':
                        args.Add(ReadSlot(token.Substring(1), slots));
                        break;

                    case 's':
                        args.Add(token.Length > 2 && token[1] == ':'
                            ? Encoding.UTF8.GetString(Convert.FromBase64String(token.Substring(2)))
                            : string.Empty);
                        break;

                    case 'l':
                        args.Add(long.Parse(token.Substring(2), CultureInfo.InvariantCulture));
                        break;

                    case 'i':
                        args.Add(int.Parse(token.Substring(2), CultureInfo.InvariantCulture));
                        break;

                    case 'b':
                        args.Add(token.Substring(2).Equals("true", StringComparison.OrdinalIgnoreCase));
                        break;

                    case '-':
                        args.Add(null);
                        break;

                    default:
                        // 没带前缀就当字符串，省得调用方每次都要手动 base64
                        args.Add(token);
                        break;
                }
            }

            return args;
        }

        private static object? ReadSlot(string text, List<object?> slots)
        {
            var n = int.Parse(text.TrimStart('#').Trim(), CultureInfo.InvariantCulture);
            if (n < 0 || n >= slots.Count)
            {
                throw new ArgumentException($"槽位 {n} 还没有值");
            }

            return slots[n];
        }

        private static object? ReadMember(object target, string name)
        {
            var t = target.GetType();
            GuardClientObject(target);
            GuardMethod(name);

            var prop = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (prop is not null)
            {
                var getter = prop.GetGetMethod(nonPublic: false)
                    ?? throw new ArgumentException("成员没有公开 getter");
                GuardClientType(getter.DeclaringType!);
                GuardMethod(getter.Name);
                return GuardResult(prop.GetValue(target));
            }

            var field = t.GetField(name, BindingFlags.Public | BindingFlags.Instance);
            if (field is not null)
            {
                GuardClientType(field.DeclaringType!);
                return GuardResult(field.GetValue(target));
            }

            throw new ArgumentException($"{t.Name} 上没有公开成员 {name}");
        }

        /// <summary>
        /// 解析类型，并限制在客户端自己的程序集里。
        /// </summary>
        /// <remarks>
        /// 这一道**必须**有：否则这个桥就成了通用 .NET 执行器，
        /// 能读写任意文件、启动任意进程 —— 远超「调用坚果云的实现」这个目的。
        /// </remarks>
        private static Type ResolveClientType(string typeName)
        {
            var name = typeName.Trim();

            if (name.Length == 0)
            {
                throw new ArgumentException("类型名为空");
            }

            var type = UiHook.FindClientType(name);

            if (type is null)
            {
                throw new ArgumentException($"找不到类型 {name}");
            }

            GuardClientType(type);
            return type;
        }

        private static void GuardClientType(Type type)
        {
            var assembly = type.Assembly.GetName().Name ?? string.Empty;
            if (!AllowedAssemblyPrefixes.Any(prefix =>
                assembly.Equals(prefix, StringComparison.OrdinalIgnoreCase)
                || assembly.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("类型 " + type.FullName + " 不是客户端自己的类型，拒绝执行。");
            if (typeof(MemberInfo).IsAssignableFrom(type) || typeof(Assembly).IsAssignableFrom(type)
                || typeof(Module).IsAssignableFrom(type) || typeof(Delegate).IsAssignableFrom(type))
                throw new ArgumentException("拒绝操作反射或委托对象。");
        }

        private static void GuardClientObject(object target)
        {
            GuardResult(target);
            GuardClientType(target.GetType());
        }

        private static object? GuardResult(object? value)
        {
            if (value is MemberInfo || value is Assembly || value is Module || value is Delegate)
                throw new ArgumentException("客户端返回反射或委托对象，拒绝继续执行。");
            return value;
        }

        private static void GuardMethod(string methodName)
        {
            foreach (var bad in ForbiddenMethods)
            {
                if (methodName.IndexOf(bad, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    throw new ArgumentException(
                        $"方法 {methodName} 命中禁止清单（{bad}）—— 拒绝执行。");
                }
            }
        }

        private static object? Construct(Type type, List<object?> args)
        {
            var instance = Activator.CreateInstance(
                type,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                args: args.ToArray(),
                culture: null);

            return instance ?? throw new ArgumentException($"{type.Name} 构造返回 null");
        }

        private static object? InvokeStatic(
            Type type, string methodName, List<object?> args,
            Dictionary<string, string> refUpdates, int scriptLine)
        {
            var method = FindMethod(type, methodName, args, requireStatic: true);
            return InvokeAndCollect(method, null, args, refUpdates, scriptLine);
        }

        private static object? InvokeInstance(
            object target, string methodName, List<object?> args,
            Dictionary<string, string> refUpdates, int scriptLine)
        {
            GuardClientObject(target);
            var method = FindMethod(target.GetType(), methodName, args, requireStatic: false);
            return InvokeAndCollect(method, target, args, refUpdates, scriptLine);
        }

        /// <summary>调用，并把 ref/out 的写回收集起来。</summary>
        private static object? InvokeAndCollect(
            MethodInfo method, object? target, List<object?> args,
            Dictionary<string, string> refUpdates, int scriptLine)
        {
            GuardClientType(method.DeclaringType!);
            GuardMethod(method.Name);
            var boxed = args.ToArray();
            var result = method.Invoke(target, boxed);

            var ps = method.GetParameters();
            for (var i = 0; i < ps.Length && i < boxed.Length; i++)
            {
                // 只看真正会写回的参数类型，避免把普通参数也算成变更
                if (!ps[i].ParameterType.IsByRef && !ps[i].IsOut)
                {
                    continue;
                }

                refUpdates[$"{scriptLine}.{i}"] =
                    $"{ps[i].Name ?? ("arg" + i)} => {boxed[i] ?? "null"}";
            }

            return GuardResult(result);
        }

        /// <summary>
        /// 按名字 + **参数个数**找方法。
        /// </summary>
        /// <remarks>
        /// 不能只用名字：混淆类里大量重载，不带参数类型查会抛
        /// AmbiguousMatchException（这个坑已经踩过两次）。
        /// 参数个数是现有信息里最可靠的区分依据。
        /// </remarks>
        private static MethodInfo FindMethod(Type type, string methodName, List<object?> args, bool requireStatic)
        {
            var flags = (requireStatic ? BindingFlags.Static : BindingFlags.Instance)
                        | BindingFlags.Public | BindingFlags.NonPublic;

            var candidates = type.GetMethods(flags)
                .Where(m => m.Name.Equals(methodName, StringComparison.Ordinal))
                .Where(m => m.GetParameters().Length == args.Count)
                .ToList();

            if (candidates.Count == 0)
            {
                throw new ArgumentException($"{type.Name} 上没有参数个数为 {args.Count} 的方法 {methodName}");
            }

            // 有多个时优先参数类型能接住的
            foreach (var m in candidates)
            {
                var ps = m.GetParameters();
                var ok = true;

                for (var i = 0; i < ps.Length; i++)
                {
                    var a = args[i];
                    if (a is null) continue;

                    var pt = ps[i].ParameterType;
                    if (!pt.IsInstanceOfType(a) && pt != a.GetType())
                    {
                        ok = false;
                        break;
                    }
                }

                if (ok) return m;
            }

            // 类型对不上就交给 CLR 自己转换（它会给出清楚的错误）
            return candidates[0];
        }
    }
}
