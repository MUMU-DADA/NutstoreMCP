using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace NutstoreMcp;

public sealed record SandboxStats(int Objects, int Directories, int Files, int CloudOnly, long TrackedBytes);

public sealed record SyncFolder(
    long? SandboxId,
    string Name,
    string CloudPath,
    bool CloudPathInferred,
    string? LocalPath,
    bool IsDefault,
    bool? IsOwner,
    string? OwnerNick,
    long? UsedSpace,
    bool? DoNotSync,
    bool? Decoupled,
    string? Description,
    SandboxStats? Stats);

public sealed record UnmountedSandbox(long? SandboxId, string Name, long? UsedSpace, bool? DoNotSync, string? Description);

public sealed record SyncFoldersResult(
    int Count,
    IReadOnlyList<SyncFolder> Folders,
    IReadOnlyList<UnmountedSandbox> UnmountedSandboxes,
    string? Note);

public sealed record AccountSettings(string? User, string? Nick, string? LoginDate);

public sealed record ClientSettings(
    string? AppVersion,
    string? CloudFileEnabled,
    long? CacheMaxUsagePercent,
    long? CacheRemainDays,
    string? PlaceholderAutoCleanup,
    string? LatestUploadDate,
    string? PinnedSandboxes);

public sealed record TrafficHistory(long? TotalDownloadSize, long? TotalUploadSize);

public sealed record SettingsSnapshot(
    AccountSettings Account,
    ClientSettings Client,
    TrafficHistory? Traffic,
    string Note);

public sealed record SyncErrorItem(string? Path, long? ReasonCode, string? ReasonName, long? Type);

public sealed record SyncErrorsResult(int Count, IReadOnlyList<SyncErrorItem> Errors, string Note);

public sealed record FileStatusResult(
    string Path,
    bool Tracked,
    object? Sandbox,
    object? Entry,
    string? MtimeIso,
    string? LocalPathHint,
    string? Reason,
    string? Note);

public sealed record LocalSummary(
    bool Available,
    string? DbPath,
    int SyncFolders,
    int UnmountedSandboxes,
    long TrackedObjects,
    long CloudOnlyObjects,
    long TrackedBytes,
    int SyncErrorCount,
    SettingsSnapshot? Settings,
    string? Note);

/// <summary>
/// L2 数据面 —— 坚果云本地库的只读视图。
///
/// ## 安全约定（见 ARCHITECTURE.md 红线 3）
///
/// 这个类**从不把敏感值读进内存**，而不是「读了再过滤」。
/// 做法是：config 表只按白名单 <c>WHERE key IN (...)</c> 查询，
/// <c>encrypted_token</c> / <c>device_token</c> / <c>bcrypt_pwd_hash</c> 这类键
/// 根本不会出现在 SQL 里。白名单之外再加一层黑名单正则兜底。
///
/// ## 只读
///
/// 连接串用 <c>Mode=ReadOnly</c>，物理上无法写入。
/// 客户端正在运行时也能安全并发读（WAL 模式支持多读者）。
///
/// ## 已知的格式风险
///
/// <c>nutstore.db</c> 是未公开格式，随版本可能变。所以这里所有查询都写成
/// 「读不到就降级」，不假设表一定存在、字段一定齐全。
/// </summary>
public sealed class LocalNutstoreData : IDisposable
{
    /// <summary>
    /// 允许读取的 config 键白名单。
    /// **新增键必须显式加到这里** —— 这是默认拒绝，不是默认允许。
    /// 特别注意不要加入任何 token / hash / secret 类字段。
    /// </summary>
    public static readonly string[] SafeConfigKeys =
    [
        "app_version",
        "user_name",
        "user_nick",
        "login_date",
        "cloud_file_enabled",
        "cache_max_usage_percent",
        "cache_remain_days",
        "is_placeholder_auto_cleanup",
        "latest_upload_file_date",
        "never_prompt",
        "prompt_times",
        "traffic_history",
        "ignore_traffic_exhausted_popup",
        "ignore_traffic_usage_near_quota_popup",
        "has_new_app_recommend",
        "pinned_sandbox_id_set",
        "notified_unreadable_sandbox_id_list",
    ];

    /// <summary>兜底黑名单：即使有人误把敏感键加进白名单，也不会被输出。</summary>
    private static readonly string[] SecretFragments =
        ["token", "pwd", "passw", "secret", "hash", "salt", "cipher", "credential"];

    /// <summary>sandbox 条目里允许输出的字段。<c>lanSyncCipher</c> 是局域网同步密钥，必须剔除。</summary>
    private static readonly string[] SafeSandboxFields =
        ["sandboxId", "name", "desc", "isDefault", "isOwner", "ownerNick", "usedSpace", "canSync", "doNotSync", "decoupled", "caps"];

    /// <summary>
    /// 同步错误原因码 → 符号名。
    /// 这些符号名来自客户端程序集里的常量（<c>SYNC_ERROR_REASON_*</c>），
    /// 但**数值映射未经官方确认** —— 所以输出里保留原始 code，符号名标注为推测。
    /// </summary>
    private static readonly Dictionary<long, string> SyncErrorReasonHints = new()
    {
        [0] = "UNKNOWN",
        [1] = "LIMIT_UPLOAD_SIZE",
        [2] = "MAYBE_VIRUS",
        [3] = "RESERVED_NAME",
        [4] = "SPECIAL_CHARACTER",
    };

    /// <summary>.NET DateTime ticks（0001 纪元）→ Unix 纪元的毫秒偏移。</summary>
    private const long DotNetEpochOffsetMs = 62135596800000L;

    private readonly string? _dbPath;
    private SqliteConnection? _connection;
    private bool _disposed;

    public LocalNutstoreData(string? dataDir)
    {
        DataDir = dataDir;
        _dbPath = string.IsNullOrEmpty(dataDir) ? null : Path.Combine(dataDir, "db1", "nutstore.db");
    }

    public string? DataDir { get; }

    public string? DbPath => _dbPath;

    public bool Available => _dbPath is not null && File.Exists(_dbPath);

    private SqliteConnection Open()
    {
        if (_connection is not null)
        {
            return _connection;
        }

        if (string.IsNullOrEmpty(DataDir))
        {
            throw new LocalDataException(
                "无法定位坚果云数据目录",
                "请设置 NUTSTORE_DATA_DIR 指向坚果云的配置目录（Windows 下通常是 %APPDATA%\\Nutstore）。");
        }

        if (!File.Exists(_dbPath))
        {
            throw new LocalDataException(
                $"本地数据库不存在：{_dbPath}",
                "确认坚果云客户端已安装并至少登录过一次。若只使用 L1 文件面，可忽略此错误。");
        }

        try
        {
            var cs = new SqliteConnectionStringBuilder
            {
                DataSource = _dbPath,
                Mode = SqliteOpenMode.ReadOnly,
            }.ToString();

            var conn = new SqliteConnection(cs);
            conn.Open();
            _connection = conn;
            return conn;
        }
        catch (SqliteException ex)
        {
            throw new LocalDataException(
                $"打开本地数据库失败：{_dbPath}",
                "数据库可能正被客户端独占。WAL 模式下并发只读通常是安全的；若持续失败，请确认文件权限。",
                ex);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _connection?.Dispose();
        _connection = null;
    }

    /// <summary>仅供测试：拿到底层连接以验证它确实是只读的。</summary>
    internal SqliteConnection ConnectionForTests() => Open();

    private static bool IsSecretKey(string key)
        => SecretFragments.Any(f => key.Contains(f, StringComparison.OrdinalIgnoreCase));

    private List<string> QueryTableNames(string pattern)
    {
        var conn = Open();
        var result = new List<string>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name LIKE $p";
        cmd.Parameters.AddWithValue("$p", pattern);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }

    /// <summary>config 表：只取白名单内的键。</summary>
    private Dictionary<string, string?> SafeConfigMap()
    {
        var conn = Open();
        var keys = SafeConfigKeys.Where(k => !IsSecretKey(k)).ToArray();
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (keys.Length == 0)
        {
            return result;
        }

        var placeholders = string.Join(",", keys.Select((_, i) => $"$k{i}"));
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT key, value FROM config WHERE key IN ({placeholders})";
        for (var i = 0; i < keys.Length; i++)
        {
            cmd.Parameters.AddWithValue($"$k{i}", keys[i]);
        }

        try
        {
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var key = reader.GetString(0);
                if (IsSecretKey(key))
                {
                    continue; // 双保险
                }

                result[key] = reader.IsDBNull(1) ? null : reader.GetString(1);
            }
        }
        catch (SqliteException)
        {
            return result;
        }

        return result;
    }

    /// <summary>
    /// <c>all_sandbox_list</c> 里每条记录出现了哪些字段名。
    ///
    /// <para>
    /// 只报**字段名**不报值：用来判断云端沙箱列表里有没有绑定所需的
    /// <c>sandboxId</c> / <c>magic</c> / <c>caps</c>，而不必把密钥类内容打出来。
    /// </para>
    /// </summary>
    public IReadOnlyList<IReadOnlyList<string>> SandboxListFieldNames()
    {
        var result = new List<IReadOnlyList<string>>();

        if (!Available)
        {
            return result;
        }

        string? raw;
        try
        {
            var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT value FROM config WHERE key = 'all_sandbox_list'";
            raw = cmd.ExecuteScalar() as string;
        }
        catch (SqliteException)
        {
            return result;
        }

        if (string.IsNullOrEmpty(raw))
        {
            return result;
        }

        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return result;
            }

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                result.Add(item.EnumerateObject()
                    .Select(p => p.Name)
                    .OrderBy(n => n, StringComparer.Ordinal)
                    .ToList());
            }
        }
        catch (JsonException)
        {
            // 结构异常就当没有
        }

        return result;
    }

    /// <summary>
    /// <c>all_sandbox_list</c> 里指定字段的**结构**（类型/子字段名），不含任何值。
    ///
    /// <para>
    /// 用来判断 <c>pathPerms</c> / <c>sandbox</c> 这些嵌套字段长什么样，
    /// 而不必把密钥类内容打出来。
    /// </para>
    /// </summary>
    public IReadOnlyList<string> SandboxListFieldShapes(params string[] fields)
    {
        var result = new List<string>();

        if (!Available)
        {
            return result;
        }

        string? raw;
        try
        {
            var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT value FROM config WHERE key = 'all_sandbox_list'";
            raw = cmd.ExecuteScalar() as string;
        }
        catch (SqliteException)
        {
            return result;
        }

        if (string.IsNullOrEmpty(raw))
        {
            return result;
        }

        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return result;
            }

            var first = doc.RootElement.EnumerateArray().FirstOrDefault();
            if (first.ValueKind != JsonValueKind.Object)
            {
                return result;
            }

            foreach (var field in fields)
            {
                if (!first.TryGetProperty(field, out var value))
                {
                    result.Add($"{field}: (不存在)");
                    continue;
                }

                result.Add($"{field}: {Describe(value, 0)}");
            }
        }
        catch (JsonException)
        {
            // 结构异常就当没有
        }

        return result;
    }

    /// <summary>描述 JSON 结构：只报类型与字段名，不报值。</summary>
    private static string Describe(JsonElement element, int depth)
    {
        if (depth > 3)
        {
            return "…";
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var names = element.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal);
                return "{ " + string.Join(", ", names) + " }";

            case JsonValueKind.Array:
                var items = element.EnumerateArray().ToList();
                if (items.Count == 0)
                {
                    return "[]";
                }

                return $"[{items.Count} × {Describe(items[0], depth + 1)}]";

            case JsonValueKind.String:
                return "string";

            case JsonValueKind.Number:
                return "number";

            case JsonValueKind.True:
            case JsonValueKind.False:
                return "bool";

            case JsonValueKind.Null:
                return "null";

            default:
                return element.ValueKind.ToString();
        }
    }

    /// <summary>config 表里所有的键名（不含任何值）。</summary>
    /// <remarks>
    /// 只列键名：用来判断会话令牌之类的凭据是否被持久化在本地，
    /// 而不必把值打出来。
    /// </remarks>
    public IReadOnlyList<string> ConfigKeys()
    {
        if (!Available)
        {
            return [];
        }

        try
        {
            var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT key FROM config ORDER BY key";
            using var reader = cmd.ExecuteReader();

            var result = new List<string>();
            while (reader.Read())
            {
                result.Add(reader.GetString(0));
            }

            return result;
        }
        catch (SqliteException)
        {
            return [];
        }
    }

    /// <summary>云端 sandbox 列表（已剔除密钥字段）。</summary>
    private List<JsonElement> SandboxList()
    {
        var conn = Open();
        string? raw = null;
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT value FROM config WHERE key = 'all_sandbox_list'";
            raw = cmd.ExecuteScalar() as string;
        }
        catch (SqliteException)
        {
            return [];
        }

        if (string.IsNullOrEmpty(raw))
        {
            return [];
        }

        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            // JsonDocument 随 using 释放，这里的元素需要在释放前克隆
            return doc.RootElement.EnumerateArray().Select(e => e.Clone()).ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string? JsonString(JsonElement obj, string property)
        => obj.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>云端沙箱的一条路径权限。</summary>
    public sealed record PathPerm(string Path, long Caps);

    /// <summary>
    /// 可以绑定到本地的云端沙箱：存在于云端、但本地还没有绑定。
    /// </summary>
    /// <remarks>
    /// <c>Magic</c> 是客户端内部标识（类似 inode），绑定时必须原样带上；
    /// 它不是凭据 —— 真正的密钥是 <c>lanSyncCipher</c>，那个永不导出。
    /// </remarks>
    public sealed record BindCandidate(
        long SandboxId,
        long? Magic,
        string? Name,
        IReadOnlyList<PathPerm> PathPerms);

    /// <summary>
    /// 列出可绑定的云端沙箱（云端有、本地未绑定），带上绑定所需的字段。
    /// </summary>
    /// <remarks>
    /// 数据全部来自本地库的 <c>all_sandbox_list</c> —— 实测它是 SandboxInfo 的
    /// 序列化形式，含 sandboxId / magic / pathPerms。所以绑定不需要活动的客户端对象，
    /// 也不需要额外的服务端调用。
    /// </remarks>
    public IReadOnlyList<BindCandidate> BindCandidates()
    {
        if (!Available)
        {
            return [];
        }

        var mounted = LocalSandboxes()
            .Where(x => x.SandboxId is not null)
            .Select(x => x.SandboxId!.Value)
            .ToHashSet();

        var result = new List<BindCandidate>();

        foreach (var sandbox in SandboxList())
        {
            var id = JsonLong(sandbox, "sandboxId");
            if (id is null || mounted.Contains(id.Value))
            {
                continue;
            }

            var perms = new List<PathPerm>();
            if (sandbox.TryGetProperty("pathPerms", out var raw) && raw.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in raw.EnumerateArray())
                {
                    var path = JsonString(p, "path");
                    var caps = JsonLong(p, "caps");

                    // 缺一不可：权限信息不完整时宁可不给候选，也不要给个残缺的
                    if (path is not null && caps is not null)
                    {
                        perms.Add(new PathPerm(path, caps.Value));
                    }
                }
            }

            result.Add(new BindCandidate(
                id.Value,
                JsonLong(sandbox, "magic"),
                JsonString(sandbox, "name"),
                perms));
        }

        return result;
    }

    private static long? JsonLong(JsonElement obj, string property)
        => obj.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;

    private static bool? JsonBool(JsonElement obj, string property)
        => obj.TryGetProperty(property, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    private sealed record LocalSandboxInfo(string Suffix, long? SandboxId, string? LocalPath);

    /// <summary>本地 <c>sndconfig&lt;hash&gt;</c> 表 → sandbox 身份信息。</summary>
    private List<LocalSandboxInfo> LocalSandboxes()
    {
        var conn = Open();
        var result = new List<LocalSandboxInfo>();

        foreach (var table in QueryTableNames("sndconfig%"))
        {
            var suffix = table["sndconfig".Length..];
            if (suffix.Length < 16 || suffix.Length > 64 || !suffix.All(Uri.IsHexDigit))
            {
                continue;
            }

            string? value = null;
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"SELECT value FROM {Quote(table)} WHERE key = 'sandbox_name'";
                value = cmd.ExecuteScalar() as string;
            }
            catch (SqliteException)
            {
                continue;
            }

            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(value);
                var root = doc.RootElement;
                // 只取路径与 id，magic 是内部标识不外泄
                result.Add(new LocalSandboxInfo(suffix, JsonLong(root, "id"), JsonString(root, "localAbsPath")));
            }
            catch (JsonException)
            {
                continue;
            }
        }

        return result;
    }

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    /// <summary>某个 sandbox 的对象统计。</summary>
    private SandboxStats? ObjectStats(string objectTable)
    {
        var conn = Open();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT COUNT(*), "
                + "COALESCE(SUM(CASE WHEN dir = 1 THEN 1 ELSE 0 END), 0), "
                + "COALESCE(SUM(CASE WHEN dir = 1 THEN 0 ELSE 1 END), 0), "
                + "COALESCE(SUM(CASE WHEN unpopulated = 1 THEN 1 ELSE 0 END), 0), "
                + "COALESCE(SUM(size), 0) "
                + $"FROM {Quote(objectTable)}";

            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            return new SandboxStats(
                reader.GetInt32(0),
                reader.GetInt32(1),
                reader.GetInt32(2),
                reader.GetInt32(3),
                reader.IsDBNull(4) ? 0 : reader.GetInt64(4));
        }
        catch (SqliteException)
        {
            return null; // 表不存在或结构变了
        }
    }

    /// <summary>
    /// 同步文件夹清单：云端 sandbox ⨝ 本地镜像路径 ⨝ 对象统计。
    /// <c>CloudPathInferred</c> 为 true 表示云端路径是从本地目录名推出来的 ——
    /// 默认 sandbox 在 <c>all_sandbox_list</c> 里 <c>name</c> 为空，只能这样推。
    /// </summary>
    public SyncFoldersResult ListSyncFolders()
    {
        var cloud = SandboxList();
        var cloudById = new Dictionary<long, JsonElement>();
        foreach (var s in cloud)
        {
            var id = JsonLong(s, "sandboxId");
            if (id is not null)
            {
                cloudById[id.Value] = s;
            }
        }

        var local = LocalSandboxes();
        var folders = new List<SyncFolder>();

        foreach (var l in local)
        {
            JsonElement? meta = l.SandboxId is not null && cloudById.TryGetValue(l.SandboxId.Value, out var m) ? m : null;
            var metaName = meta is null ? null : JsonString(meta.Value, "name");
            var localName = string.IsNullOrEmpty(l.LocalPath) ? null : Path.GetFileName(l.LocalPath);
            var displayName = !string.IsNullOrEmpty(metaName)
                ? metaName
                : localName ?? $"(sandbox {l.SandboxId})";

            folders.Add(new SyncFolder(
                l.SandboxId,
                displayName,
                "/" + displayName,
                string.IsNullOrEmpty(metaName),
                l.LocalPath,
                meta is null ? false : JsonBool(meta.Value, "isDefault") ?? false,
                meta is null ? null : JsonBool(meta.Value, "isOwner"),
                meta is null ? null : JsonString(meta.Value, "ownerNick"),
                meta is null ? null : JsonLong(meta.Value, "usedSpace"),
                meta is null ? null : JsonBool(meta.Value, "doNotSync"),
                meta is null ? null : JsonBool(meta.Value, "decoupled"),
                meta is null ? null : JsonString(meta.Value, "desc"),
                ObjectStats($"sndobject{l.Suffix}")));
        }

        // 云端有、本地未挂载的 sandbox —— 这正是「远程目录同步到本地」的候选
        var mounted = local.Where(x => x.SandboxId is not null).Select(x => x.SandboxId!.Value).ToHashSet();
        var unmounted = cloud
            .Where(s => JsonLong(s, "sandboxId") is not { } id || !mounted.Contains(id))
            .Select(s => new UnmountedSandbox(
                JsonLong(s, "sandboxId"),
                JsonString(s, "name") is { Length: > 0 } n ? n : "(未命名)",
                JsonLong(s, "usedSpace"),
                JsonBool(s, "doNotSync"),
                JsonString(s, "desc")))
            .ToList();

        folders.Sort(static (a, b) =>
        {
            var byDefault = (b.IsDefault ? 1 : 0) - (a.IsDefault ? 1 : 0);
            return byDefault != 0 ? byDefault : string.CompareOrdinal(a.Name, b.Name);
        });

        return new SyncFoldersResult(
            folders.Count,
            folders,
            unmounted,
            unmounted.Count > 0
                ? "unmountedSandboxes 是云端存在但本地未建立同步的目录。"
                  + "把它们挂载到本地属于 L3 控制面能力，尚未实现（见 ARCHITECTURE.md 第 7 节）。"
                : null);
    }

    /// <summary>客户端设置快照（只含白名单键）。</summary>
    public SettingsSnapshot GetSettings()
    {
        var cfg = SafeConfigMap();
        TrafficHistory? traffic = null;
        if (cfg.TryGetValue("traffic_history", out var rawTraffic) && !string.IsNullOrEmpty(rawTraffic))
        {
            try
            {
                using var doc = JsonDocument.Parse(rawTraffic);
                traffic = new TrafficHistory(
                    JsonLong(doc.RootElement, "totalDownloadSize"),
                    JsonLong(doc.RootElement, "totalUploadSize"));
            }
            catch (JsonException)
            {
                traffic = null;
            }
        }

        return new SettingsSnapshot(
            new AccountSettings(
                cfg.GetValueOrDefault("user_name"),
                cfg.GetValueOrDefault("user_nick"),
                cfg.GetValueOrDefault("login_date")),
            new ClientSettings(
                cfg.GetValueOrDefault("app_version"),
                cfg.GetValueOrDefault("cloud_file_enabled"),
                ParseLong(cfg.GetValueOrDefault("cache_max_usage_percent")),
                ParseLong(cfg.GetValueOrDefault("cache_remain_days")),
                cfg.GetValueOrDefault("is_placeholder_auto_cleanup"),
                cfg.GetValueOrDefault("latest_upload_file_date"),
                cfg.GetValueOrDefault("pinned_sandbox_id_set")),
            traffic,
            "这是只读快照，来自本地数据库。修改客户端设置属于 L3 控制面能力，尚未实现。");
    }

    private static long? ParseLong(string? s)
        => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

    /// <summary>同步错误清单。</summary>
    public SyncErrorsResult GetSyncErrors(int limit = 100)
    {
        var conn = Open();
        var errors = new List<SyncErrorItem>();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT file_path, reason, type FROM sync_errors LIMIT $n";
            cmd.Parameters.AddWithValue("$n", Math.Clamp(limit, 1, 1000));
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var code = reader.IsDBNull(1) ? (long?)null : reader.GetInt64(1);
                errors.Add(new SyncErrorItem(
                    reader.IsDBNull(0) ? null : reader.GetString(0),
                    code,
                    code is not null && SyncErrorReasonHints.TryGetValue(code.Value, out var name) ? name : null,
                    reader.IsDBNull(2) ? null : reader.GetInt64(2)));
            }
        }
        catch (SqliteException)
        {
            return new SyncErrorsResult(0, [], "本地库中没有 sync_errors 表或结构已变化。");
        }

        return new SyncErrorsResult(
            errors.Count,
            errors,
            "reasonName 是根据客户端常量符号推测的，数值映射未经官方确认；请以 reasonCode 为准。");
    }

    /// <summary>查询单个云端路径的同步状态。</summary>
    public FileStatusResult FileStatus(string cloudPath)
    {
        var conn = Open();
        var folders = ListSyncFolders().Folders;

        var normalized = cloudPath == CloudPath.Root ? CloudPath.Root : cloudPath.TrimEnd('/');
        var owner = folders
            .Where(f => normalized == f.CloudPath || normalized.StartsWith(f.CloudPath + "/", StringComparison.Ordinal))
            .OrderByDescending(f => f.CloudPath.Length)
            .FirstOrDefault();

        if (owner is null)
        {
            var mounted = folders.Count > 0 ? string.Join(", ", folders.Select(f => f.CloudPath)) : "(无)";
            return new FileStatusResult(
                cloudPath, false, null, null, null, null,
                "LOCAL_PATH_NOT_FOUND",
                $"没有同步文件夹覆盖该路径。当前已挂载：{mounted}");
        }

        var rel = normalized.Length > owner.CloudPath.Length
            ? normalized[owner.CloudPath.Length..]
            : "/";
        var lastSlash = rel.LastIndexOf('/');
        var dirPath = lastSlash <= 0 ? "/" : rel[..lastSlash];
        var fileName = lastSlash >= 0 && lastSlash + 1 < rel.Length ? rel[(lastSlash + 1)..] : null;

        var suffix = LocalSandboxes().FirstOrDefault(x => x.SandboxId == owner.SandboxId)?.Suffix;
        if (suffix is null)
        {
            return new FileStatusResult(
                cloudPath, false, SandboxRef(owner), null, null, null,
                "NOT_IN_SYNC_INDEX", "找不到该 sandbox 的本地索引表。");
        }

        var table = $"sndobject{suffix}";
        long? version = null, size = null, mtime = null;
        var isDir = false;
        var cloudOnly = false;
        var found = false;

        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = fileName is not null
                ? $"SELECT version, dir, size, mtime, unpopulated FROM {Quote(table)} WHERE dir_path = $d AND file_name = $f LIMIT 1"
                : $"SELECT version, dir, size, mtime, unpopulated FROM {Quote(table)} WHERE dir_path = $d AND dir = 1 LIMIT 1";
            cmd.Parameters.AddWithValue("$d", dirPath);
            if (fileName is not null)
            {
                cmd.Parameters.AddWithValue("$f", fileName);
            }

            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                found = true;
                version = reader.IsDBNull(0) ? null : reader.GetInt64(0);
                isDir = !reader.IsDBNull(1) && reader.GetInt64(1) == 1;
                size = reader.IsDBNull(2) ? null : reader.GetInt64(2);
                mtime = reader.IsDBNull(3) ? null : reader.GetInt64(3);
                cloudOnly = !reader.IsDBNull(4) && reader.GetInt64(4) == 1;
            }
        }
        catch (SqliteException)
        {
            found = false;
        }

        if (!found)
        {
            return new FileStatusResult(
                cloudPath, false, SandboxRef(owner), null, null, null,
                "NOT_IN_SYNC_INDEX",
                "该路径不在本地同步索引里 —— 可能是云端新增、尚未同步下来，或路径大小写不一致。");
        }

        var localHint = owner.LocalPath is null
            ? null
            : Path.Combine(owner.LocalPath, rel.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));

        return new FileStatusResult(
            cloudPath,
            true,
            SandboxRef(owner),
            new
            {
                isDirectory = isDir,
                version,
                sizeBytes = size,
                mtimeTicks = mtime,
                cloudOnly,
            },
            DotNetTicksToIso(mtime),
            localHint,
            null,
            null);
    }

    private static object SandboxRef(SyncFolder f) => new
    {
        id = f.SandboxId,
        name = f.Name,
        cloudPath = f.CloudPath,
        localPath = f.LocalPath,
    };

    /// <summary>总体健康快照。</summary>
    public LocalSummary Summary()
    {
        if (!Available)
        {
            return new LocalSummary(
                false, DbPath, 0, 0, 0, 0, 0, 0, null,
                "未找到本地坚果云数据库。L1 文件面不受影响。");
        }

        var folders = ListSyncFolders();
        long totalObjects = 0, cloudOnly = 0, trackedBytes = 0;
        foreach (var f in folders.Folders)
        {
            if (f.Stats is null)
            {
                continue;
            }

            totalObjects += f.Stats.Objects;
            cloudOnly += f.Stats.CloudOnly;
            trackedBytes += f.Stats.TrackedBytes;
        }

        var errors = GetSyncErrors(1000);

        return new LocalSummary(
            true,
            DbPath,
            folders.Count,
            folders.UnmountedSandboxes.Count,
            totalObjects,
            cloudOnly,
            trackedBytes,
            errors.Count,
            GetSettings(),
            null);
    }

    /// <summary>
    /// 把本地库里的时间戳转成 ISO 8601。
    ///
    /// ⚠️ 这里的时间戳是 **.NET DateTime ticks**（100ns 计数，纪元 0001-01-01），
    /// **不是** Windows FILETIME（纪元 1601-01-01）。两者差 1600 年：
    /// 用 FILETIME 的偏移量去换算，<c>639118499520000000</c> 会得到 3626 年。
    /// 实测校准依据：库里 mtime=639118499520000000 的条目，对应文件名为
    /// 「某个 2026-04-15 的表格文件」——换算出 2026-04-15 才自洽。
    /// </summary>
    public static string? DotNetTicksToIso(long? value)
    {
        if (value is null or <= 0)
        {
            return null;
        }

        var ms = value.Value / 10000L - DotNetEpochOffsetMs;
        // 边界包含 Unix 纪元本身（ms == 0）；早于 1970 的时间戳对本项目无意义
        if (ms < 0 || ms > 253402300799999L)
        {
            return null;
        }

        return DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
    }
}
