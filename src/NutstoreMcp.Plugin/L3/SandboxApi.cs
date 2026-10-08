using System.Reflection;
using System.Text.Json;

namespace NutstoreMcp.Plugin.L3;

/// <summary>
/// 用**坚果云客户端自己的 DAO** 读取同步配置。
///
/// <para>
/// 这是真正的 L3：数据来自客户端的模型（<c>NSSandbox</c>），而不是我猜的表结构。
/// L2 那套直接读 sqlite 的做法依赖我对表名和字段的理解，客户端一升级就可能错；
/// 走它自己的解析器则天然跟着版本走。
/// </para>
///
/// <para>
/// <b>数据安全（本类的核心设计）</b>：<c>NSSandboxDAO.ReadAllSandboxesFromDb</c>
/// 接受一个数据库路径，但它内部用什么连接模式打开我控制不了 —— 万一是可写的，
/// 它就可能动到真实数据库。
/// 所以这里**先把数据库复制一份**，把副本路径交给它。
/// 副本用完即删，真实数据库从头到尾没被这个进程打开过。
/// </para>
///
/// <para>
/// 复制时必须连 <c>-wal</c> / <c>-shm</c> 一起 —— 坚果云用 WAL 模式，
/// 只拷主库会读到旧快照甚至损坏。
/// </para>
/// </summary>
internal static class SandboxApi
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>坚果云本地数据目录。</summary>
    public static string DataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Nutstore", "db1");

    public static string LiveDbPath => Path.Combine(DataDirectory, "nutstore.db");

    /// <summary>
    /// 读所有沙箱绑定（只读，读的是副本）。
    /// </summary>
    public static string ReadSyncFolders()
    {
        if (!ClientAssemblies.Available)
        {
            return Fail($"找不到客户端目录（{ClientAssemblies.ClientDirectory}）");
        }

        if (!File.Exists(LiveDbPath))
        {
            return Fail($"本地数据库不存在: {LiveDbPath}");
        }

        var scratch = Path.Combine(
            Path.GetTempPath(), "NutstoreMcp-l3-" + Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(scratch);

            // ── 复制数据库（含 WAL 与 shm）──
            // 坚果云是 WAL 模式：只拷主库会拿到旧快照，甚至读到不一致的状态。
            var copyPath = Path.Combine(scratch, "nutstore.db");
            var copied = new List<string>();

            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                var src = LiveDbPath + suffix;
                if (!File.Exists(src))
                {
                    continue;
                }

                // FileShare.ReadWrite：坚果云正开着这个库，独占打开会失败
                using var input = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var output = new FileStream(copyPath + suffix, FileMode.Create, FileAccess.Write, FileShare.None);
                input.CopyTo(output);
                copied.Add(Path.GetFileName(src));
            }

            if (!copied.Contains("nutstore.db"))
            {
                return Fail("数据库复制失败");
            }

            // ── 让客户端的 DAO 读副本 ──
            var dao = ClientAssemblies.FindType("NutstoreClient.DAO.NSSandboxDAO");
            var sandboxType = ClientAssemblies.FindType("NutstoreClient.Common.NSSandbox");
            if (dao is null || sandboxType is null)
            {
                return Fail("找不到 NSSandboxDAO 或 NSSandbox 类型");
            }

            var read = dao.GetMethod("ReadAllSandboxesFromDb", BindingFlags.Public | BindingFlags.Static)
                       ?? dao.GetMethod("ReadAllSandboxesFromDb");
            if (read is null)
            {
                return Fail("NSSandboxDAO.ReadAllSandboxesFromDb 不存在");
            }

            object? result;
            try
            {
                result = read.Invoke(null, [copyPath]);
            }
            catch (Exception ex)
            {
                // 把整条内部异常链摊开：TypeInitializationException 只是外壳，
                // 真正的原因（缺文件？缺路径？解密失败？）在最里面那层。
                return Fail("调用客户端 DAO 失败", DescribeChain(ex));
            }

            if (result is not System.Collections.IEnumerable list)
            {
                return Fail("DAO 返回了非集合结果");
            }

            var items = new List<object>();
            foreach (var item in list)
            {
                if (item is null)
                {
                    continue;
                }

                items.Add(DescribeSandbox(item));
            }

            return JsonSerializer.Serialize(new
            {
                ok = true,
                source = "客户端自己的 NSSandboxDAO（读的是数据库副本，真库未被打开）",
                database = LiveDbPath,
                copiedFiles = copied,
                count = items.Count,
                sandboxes = items,
            }, Json);
        }
        catch (Exception ex)
        {
            return Fail($"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            // 副本里含同步元数据，用完必须删干净
            try
            {
                if (Directory.Exists(scratch))
                {
                    Directory.Delete(scratch, recursive: true);
                }
            }
            catch (Exception)
            {
                // 删不掉就留着，不值得为它抛异常
            }
        }
    }

    private static object DescribeSandbox(object sandbox)
    {
        var t = sandbox.GetType();

        T? Get<T>(string name)
        {
            try
            {
                var v = t.GetProperty(name)?.GetValue(sandbox);
                return v is null ? default : (T)v;
            }
            catch (Exception)
            {
                return default;
            }
        }

        var localPath = Get<string>("LocalAbsPath");

        return new
        {
            id = Get<long?>("ID"),
            magic = Get<long?>("Magic"),
            name = Get<string>("Name"),
            localPath,
            isPlaceholder = Get<bool?>("IsPlaceholder"),
            localExists = !string.IsNullOrEmpty(localPath) && Directory.Exists(localPath),
            identifier = Get<string>("Identifier"),
        };
    }

    private static string Fail(string reason) => JsonSerializer.Serialize(new
    {
        ok = false,
        reason,
    }, Json);

    private static string Fail(string reason, object detail) => JsonSerializer.Serialize(new
    {
        ok = false,
        reason,
        detail,
    }, Json);

    /// <summary>把异常链摊平成可读的结构 —— 外层异常几乎从不说明真实原因。</summary>
    public static List<object> DescribeChain(Exception ex)
    {
        var chain = new List<object>();
        var depth = 0;

        while (ex is not null && depth++ < 8)
        {
            chain.Add(new
            {
                level = depth,
                type = ex.GetType().FullName,
                message = ex.Message,
                // 第一帧堆栈往往就指向真正的失败点
                topFrame = (ex.StackTrace ?? string.Empty)
                    .Split('\n')
                    .Select(s => s.Trim())
                    .FirstOrDefault(s => s.Length > 0),
            });

            ex = ex.InnerException!;
        }

        return chain;
    }
}
