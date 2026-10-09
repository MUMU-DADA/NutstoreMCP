// 在客户端域内执行受限反射脚本。
using NutstoreMcp.ClientBindings;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
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
    ///   BUFFER|&lt;base64&gt;              创建桥拥有的只读诊断流
    ///   VALUE|&lt;登记结构体&gt;|字段|参数...  新建零初始化值，不修改已有对象
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

        private const int BufferBudgetBytes = 8 * 1024 * 1024;
        private static readonly HashSet<string> ValueFields = new HashSet<string>(StringComparer.Ordinal)
        {
            "path", "size", "hashBase64", "fullObjectNameBase64", "blockListNameBase64", "compressible", "exAttr",
        };

        public sealed class Result : IDisposable
        {
            public List<string> Lines { get; } = new List<string>();
            public List<object?> Slots { get; } = new List<object?>();
            public string? Error { get; set; }
            public string? ErrorCode { get; private set; }
            public int? HttpStatus { get; private set; }
            private readonly List<MemoryStream> _buffers = new List<MemoryStream>();
            private long _bufferBytes;

            internal MemoryStream CreateBuffer(string base64)
            {
                // 先限制编码长度，避免在检查预算之前分配任意大的字节数组。
                var remaining = BufferBudgetBytes - _bufferBytes;
                if (base64.Length > 4L * ((remaining + 2L) / 3L)
                    || base64.Length % 4 != 0 || base64.Any(char.IsWhiteSpace))
                    throw new ArgumentException("BUFFER 超过 8 MiB 诊断预算或含非规范 Base64 空白。");
                var padding = base64.EndsWith("==", StringComparison.Ordinal) ? 2
                    : base64.EndsWith("=", StringComparison.Ordinal) ? 1 : 0;
                if (3L * (base64.Length / 4) - padding > remaining)
                    throw new ArgumentException("BUFFER 总长度超过 8 MiB 诊断预算。");
                var bytes = Convert.FromBase64String(base64);
                if (bytes.Length > BufferBudgetBytes || _bufferBytes + bytes.Length > BufferBudgetBytes)
                    throw new ArgumentException("BUFFER 总长度超过 8 MiB 诊断预算。");
                var stream = new MemoryStream(bytes, 0, bytes.Length, writable: false, publiclyVisible: false);
                _buffers.Add(stream);
                _bufferBytes += bytes.Length;
                return stream;
            }

            public void Dispose()
            {
                // 只关闭本结果创建的流，绝不 Dispose 客户端返回的对象。
                foreach (var stream in _buffers) stream.Dispose();
                _buffers.Clear();
                _bufferBytes = 0;
            }

            internal void SetFailure(int line, Exception exception)
            {
                var invoked = exception is TargetInvocationException;
                while (exception is TargetInvocationException wrapper && wrapper.InnerException is not null)
                    exception = wrapper.InnerException;
                // 客户端异常消息可带请求、内容标识或会话信息，只读固定错误字段。
                if (exception.GetType().FullName == ClientSymbols.RequestExceptionType)
                {
                    var code = exception.GetType().GetProperty("ErrorCode")?.GetValue(exception) as string;
                    if (code is not null && code.Length is > 0 and <= 64 && code.All(c => char.IsLetterOrDigit(c) && c <= 127 || c == '_'))
                        ErrorCode = code;
                    var status = exception.GetType().GetProperty("ReturnCode")?.GetValue(exception);
                    if (status is System.Net.HttpStatusCode httpStatus) HttpStatus = (int)httpStatus;
                }
                Error = $"第 {line} 行失败: {exception.GetType().Name}"
                    + (ErrorCode is null ? string.Empty : " (" + ErrorCode + ")")
                    + (HttpStatus is null ? string.Empty : " [" + HttpStatus + "]")
                    + (invoked ? string.Empty : ": " + exception.Message);
            }

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
                    result.Slots.Add(ExecuteLine(line, result, i));
                    result.Lines.Add("ok");
                }
                catch (Exception ex)
                {
                    result.SetFailure(i, ex);
                    result.Lines.Add("fail");
                    return result;
                }
            }

            return result;
        }

        private static object? ExecuteLine(
            string line, Result result, int scriptLine)
        {
            var slots = result.Slots;
            var refUpdates = result.RefUpdates;
            var parts = line.Split('|');
            var op = parts[0].Trim().ToUpperInvariant();

            switch (op)
            {
                case "BUFFER":
                {
                    if (parts.Length != 2) throw new ArgumentException("BUFFER 需要且仅需要 Base64 参数");
                    return result.CreateBuffer(parts[1]);
                }

                case "VALUE":
                {
                    if (parts.Length < 2 || (parts.Length - 2) % 2 != 0)
                        throw new ArgumentException("VALUE 需要结构体类型与字段/参数对");
                    return ConstructValue(parts, slots);
                }

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
                    throw new ArgumentException($"未知操作 {op}（支持 NEW / CALL / INSTANCE / GET / CHAIN / ENUM / BUFFER / VALUE）");
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
                GuardSignature(getter);
                return GuardResult(prop.GetValue(target));
            }

            var field = t.GetField(name, BindingFlags.Public | BindingFlags.Instance);
            if (field is not null)
            {
                GuardClientType(field.DeclaringType!);
                GuardDataType(field.FieldType);
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
            GuardDataValue(value, new HashSet<Array>(), 0);
            return value;
        }

        private static void GuardDataValue(object? value, HashSet<Array> seen, int depth)
        {
            if (value is null) return;
            GuardDataType(value.GetType());
            // 只遍历 CLR 数组，不执行客户端 getter 或未知枚举器。
            if (value is Array array && !array.GetType().GetElementType()!.IsValueType
                && array.GetType().GetElementType() != typeof(string) && seen.Add(array))
            {
                if (depth >= 32 || seen.Count > 1024) throw new ArgumentException("参数数组嵌套超过桥检查范围。");
                foreach (var item in array) GuardDataValue(item, seen, depth + 1);
            }
        }

        private static void GuardDataType(Type type)
        {
            if (typeof(MemberInfo).IsAssignableFrom(type) || typeof(Assembly).IsAssignableFrom(type)
                || typeof(Module).IsAssignableFrom(type) || typeof(Delegate).IsAssignableFrom(type))
                throw new ArgumentException("拒绝反射或委托参数、返回值及其容器。");
            if (type.HasElementType) GuardDataType(type.GetElementType()!);
            if (type.IsGenericType)
                foreach (var argument in type.GetGenericArguments()) GuardDataType(argument);
        }

        private static void GuardSignature(MethodBase member)
        {
            foreach (var parameter in member.GetParameters()) GuardDataType(parameter.ParameterType);
            if (member is MethodInfo method) GuardDataType(method.ReturnType);
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
            foreach (var arg in args) GuardResult(arg);
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var constructors = type.GetConstructors(flags).Cast<MethodBase>().ToArray();
            if (constructors.Length == 0 && type.IsValueType && args.Count == 0)
                return GuardResult(Activator.CreateInstance(type));
            var values = args.ToArray();
            // 先按 CLR 绑定规则选中实际构造器；null 也不能进入反射签名。
            var constructor = (ConstructorInfo)Type.DefaultBinder.BindToMethod(flags, constructors,
                ref values, modifiers: null, culture: null, names: null, state: out _);
            GuardSignature(constructor);
            var instance = constructor.Invoke(values);

            return GuardResult(instance) ?? throw new ArgumentException($"{type.Name} 构造返回 null");
        }

        private static object ConstructValue(string[] parts, List<object?> slots)
        {
            if (!string.Equals(parts[1].Trim(), ClientSymbols.CreateFileParamType, StringComparison.Ordinal))
                throw new ArgumentException("VALUE 仅允许已登记的 CreateFileParam 结构体。");
            var type = ResolveClientType(parts[1]);
            if (!type.IsValueType || type.IsEnum || Nullable.GetUnderlyingType(type) is not null)
                throw new ArgumentException("VALUE 只允许非枚举的已登记结构体。");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var assignments = new List<(FieldInfo Field, object? Value)>();
            for (var i = 2; i < parts.Length; i += 2)
            {
                var name = parts[i].Trim();
                if (name.Length == 0 || !seen.Add(name)) throw new ArgumentException("VALUE 字段为空或重复。");
                if (!ValueFields.Contains(name)) throw new ArgumentException("VALUE 字段未登记：" + name);
                GuardMethod(name);
                var field = type.GetField(name, BindingFlags.Public | BindingFlags.Instance)
                    ?? throw new ArgumentException("VALUE 缺少公开实例字段 " + name);
                if (field.IsStatic || field.IsInitOnly || field.IsLiteral)
                    throw new ArgumentException("VALUE 字段不能是 static / readonly / const。");
                GuardClientType(field.DeclaringType!);
                GuardDataType(field.FieldType);
                var value = GuardResult(DecodeArgs(new[] { parts[i + 1] }, 0, slots)[0]);
                if (value is null)
                {
                    if (field.FieldType.IsValueType && Nullable.GetUnderlyingType(field.FieldType) is null)
                        throw new ArgumentException("VALUE 非可空字段不能接收 null。");
                }
                else if (value.GetType() != field.FieldType)
                    throw new ArgumentException("VALUE 字段参数类型必须精确匹配。");
                assignments.Add((field, value));
            }
            // 不运行实例构造函数，也不对桥中已有的对象执行 SET。
            var instance = FormatterServices.GetUninitializedObject(type);
            foreach (var assignment in assignments) assignment.Field.SetValue(instance, assignment.Value);
            return instance;
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
            GuardSignature(method);
            foreach (var arg in args) GuardResult(arg);
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
