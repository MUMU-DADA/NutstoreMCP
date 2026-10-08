using System.Globalization;

namespace NutstoreMcp;

/// <summary>
/// 配置加载与护栏参数。
///
/// 来源优先级：进程环境变量 &gt; <c>.env</c> 文件。**凭据永远不进代码、不进 git**。
/// </summary>
public sealed class AppConfig
{
    public const string DefaultHost = "https://dav.jianguoyun.com/dav";
    public const int DefaultRateLimit = 600;
    public const long DefaultRateWindowMs = 30L * 60 * 1000;
    public const long DefaultMaxUploadBytes = 500L * 1024 * 1024;
    public const int DefaultTimeoutMs = 60_000;

    public required string Host { get; init; }
    public string? User { get; init; }
    public string? Pass { get; init; }
    public required IReadOnlyList<string> Roots { get; init; }
    public required IReadOnlyList<string> LocalRoots { get; init; }
    public bool ReadOnly { get; init; }
    public int RateLimit { get; init; }
    public long RateWindowMs { get; init; }
    public long MaxUploadBytes { get; init; }
    public int TimeoutMs { get; init; }
    public string? DataDir { get; init; }
    public string? EnvFileLoaded { get; init; }
    public required string ProjectRoot { get; init; }

    public bool HasCredentials => !string.IsNullOrEmpty(User) && !string.IsNullOrEmpty(Pass);

    /// <summary>默认的本地坚果云数据目录。</summary>
    public static string? DefaultDataDir(IReadOnlyDictionary<string, string?> env)
    {
        if (env.TryGetValue("APPDATA", out var appData) && !string.IsNullOrEmpty(appData))
        {
            return Path.Combine(appData, "Nutstore");
        }

        var folder = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return string.IsNullOrEmpty(folder) ? null : Path.Combine(folder, "Nutstore");
    }

    /// <summary>
    /// 读取配置。**不因为缺凭据而抛错** —— L2（本地只读）在没有 WebDAV 凭据时
    /// 依然应当可用，只是 L1 工具会返回明确的错误。
    /// </summary>
    public static AppConfig Load(
        IReadOnlyDictionary<string, string?>? environment = null,
        string? envFilePath = null)
    {
        var env = environment is null
            ? ProcessEnvironment()
            : new Dictionary<string, string?>(environment, StringComparer.OrdinalIgnoreCase);

        var loadedFrom = EnvFile.Apply(env, envFilePath);

        var host = (Get(env, "NUTSTORE_HOST") ?? DefaultHost).TrimEnd('/');
        if (!Uri.TryCreate(host, UriKind.Absolute, out var parsedHost))
        {
            throw new ConfigException($"NUTSTORE_HOST 不是合法 URL：{host}");
        }

        var isLoopback = parsedHost.Host is "localhost" or "127.0.0.1" or "::1";
        if (parsedHost.Scheme != Uri.UriSchemeHttps && !isLoopback)
        {
            throw new ConfigException(
                $"NUTSTORE_HOST 必须使用 https（本地测试可用 localhost）：{host}",
                "Basic 认证是明文传输的，走 http 会把应用密码暴露在链路上。");
        }

        var roots = CloudPath.NormalizeRoots(
            (Get(env, "NUTSTORE_ROOTS") ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        // 本机可读写目录白名单。上传的**来源**和下载的**目标**都必须落在其中 ——
        // 否则模型可以借「上传」把本机任意文件（比如密码本）传到云端。
        var projectRoot = FindProjectRoot();
        var localRoots = (Get(env, "NUTSTORE_LOCAL_ROOTS") ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Path.GetFullPath)
            .ToList();
        if (localRoots.Count == 0)
        {
            localRoots.Add(Path.Combine(projectRoot, ".downloads"));
        }

        return new AppConfig
        {
            Host = host,
            User = NullIfBlank(Get(env, "NUTSTORE_USER")),
            Pass = NullIfBlank(Get(env, "NUTSTORE_PASS")),
            Roots = roots,
            LocalRoots = localRoots,
            ReadOnly = ReadBool(env, "NUTSTORE_READ_ONLY", true),
            RateLimit = ReadInt(env, "NUTSTORE_RATE_LIMIT", DefaultRateLimit),
            RateWindowMs = ReadInt(env, "NUTSTORE_RATE_WINDOW_MS", (int)DefaultRateWindowMs),
            MaxUploadBytes = ReadInt(env, "NUTSTORE_MAX_UPLOAD_BYTES", (int)DefaultMaxUploadBytes),
            TimeoutMs = ReadInt(env, "NUTSTORE_TIMEOUT_MS", DefaultTimeoutMs),
            DataDir = NullIfBlank(Get(env, "NUTSTORE_DATA_DIR")) ?? DefaultDataDir(env),
            EnvFileLoaded = loadedFrom,
            ProjectRoot = projectRoot,
        };
    }

    internal static Dictionary<string, string?> ProcessEnvironment()
    {
        var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
        {
            if (e.Key is string k)
            {
                dict[k] = e.Value as string;
            }
        }

        return dict;
    }

    private static string? Get(IReadOnlyDictionary<string, string?> env, string key)
        => env.TryGetValue(key, out var v) ? v : null;

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static bool ReadBool(IReadOnlyDictionary<string, string?> env, string key, bool fallback)
    {
        var raw = Get(env, key);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "on" => true,
            "0" or "false" or "no" or "off" => false,
            _ => throw new ConfigException($"{key} 必须是布尔值（true/false），收到 \"{raw}\""),
        };
    }

    private static int ReadInt(IReadOnlyDictionary<string, string?> env, string key, int fallback)
    {
        var raw = Get(env, key);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        if (!long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n <= 0)
        {
            throw new ConfigException($"{key} 必须是正数，收到 \"{raw}\"");
        }

        return n > int.MaxValue ? int.MaxValue : (int)n;
    }

    private static string FindProjectRoot()
    {
        // 从可执行文件目录向上找带仓库标记的目录；找不到就退回可执行文件目录。
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && !string.IsNullOrEmpty(dir); i++)
        {
            if (File.Exists(Path.Combine(dir, "ARCHITECTURE.md"))
                || File.Exists(Path.Combine(dir, "NutstoreMCP.sln")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }

        return AppContext.BaseDirectory;
    }
}

/// <summary>
/// 极简 <c>.env</c> 读取。
///
/// 只支持 <c>KEY=VALUE</c>、<c>#</c> 注释、整体可选的单/双引号。
/// **不覆盖进程里已有的环境变量** —— 与 dotenv 约定一致，显式设的优先。
/// </summary>
public static class EnvFile
{
    private const int MaxAncestorHops = 6;

    public static string? Apply(Dictionary<string, string?> env, string? explicitPath = null)
    {
        var path = explicitPath ?? Find(env);
        if (path is null || !File.Exists(path))
        {
            return null;
        }

        foreach (var rawLine in File.ReadAllLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var eq = line.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                continue;
            }

            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();

            if (value.Length >= 2
                && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
            {
                value = value[1..^1];
            }

            // 进程环境变量优先
            if (!env.TryGetValue(key, out var existing) || string.IsNullOrEmpty(existing))
            {
                env[key] = value;
            }
        }

        return path;
    }

    private static string? Find(IReadOnlyDictionary<string, string?> env)
    {
        if (env.TryGetValue("NUTSTORE_ENV_FILE", out var explicitEnv) && !string.IsNullOrWhiteSpace(explicitEnv))
        {
            return explicitEnv;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var dir = start;
            for (var i = 0; i < MaxAncestorHops && !string.IsNullOrEmpty(dir); i++)
            {
                var candidate = Path.Combine(dir, ".env");
                if (seen.Add(candidate) && File.Exists(candidate))
                {
                    return candidate;
                }

                dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
            }
        }

        return null;
    }
}
