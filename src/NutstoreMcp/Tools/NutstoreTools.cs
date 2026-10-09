using System.ComponentModel;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;

namespace NutstoreMcp;

/// <summary>
/// MCP 工具定义。
///
/// 分两组：
///   L1 文件面 —— 走 WebDAV
///   L2 数据面 —— 读本地库，只读
///
/// ⚠️ SDK 的注解默认值有两个坑，每个工具都必须显式覆盖：
///   <c>Destructive</c> 默认 **true**（只读工具必须写 false，否则会被标注成破坏性操作）
///   <c>OpenWorld</c>   默认 **true**（本地只读工具应写 false）
///
/// 返回值一律是**格式化 JSON 字符串**。SDK 会把非字符串返回值塞进一个 text block，
/// 但那样中文会被转义成 \uXXXX；这里用 UnsafeRelaxedJsonEscaping 保住可读性。
/// </summary>
[McpServerToolType]
public sealed class NutstoreTools
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        // 用 camelCase 而不是 .NET 默认的 PascalCase：模型对 camelCase 更熟，
        // 也与本项目 Node 参考实现的输出形状一致。
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly AppConfig _config;
    private readonly WebDavClient _webdav;
    private readonly LocalNutstoreData _local;

    public NutstoreTools(AppConfig config, WebDavClient webdav, LocalNutstoreData local)
    {
        _config = config;
        _webdav = webdav;
        _local = local;
    }

    private static string Json(object value) => JsonSerializer.Serialize(value, JsonOpts);

    /// <summary>写操作闸门 —— 只读模式下一律拒绝。</summary>
    private void RequireWritable(string action)
    {
        if (_config.ReadOnly)
        {
            throw new ReadOnlyException(
                $"当前处于只读模式，拒绝执行：{action}",
                "如需写入，请显式设置 NUTSTORE_READ_ONLY=false 后重启服务。建议只在需要时临时开启。");
        }
    }

    /// <summary>
    /// 云端路径守卫（纵深防御）。
    ///
    /// WebDavClient 内部也有同样的校验，这里再做一次是刻意的：白名单是安全边界，
    /// 不该只由一个组件把关。而且工具层自带守卫后，它的行为可以脱离真实网络被测试。
    /// </summary>
    private string GuardCloud(string? input)
    {
        var cloudPath = CloudPath.Normalize(input);
        CloudPath.AssertAllowed(cloudPath, _config.Roots);
        return cloudPath;
    }

    /// <summary>
    /// 解析本机路径：相对路径一律相对于**第一个白名单目录**，而不是进程 CWD。
    ///
    /// 这一条很关键：MCP 服务的 CWD 是宿主决定的（可能是任何地方），
    /// 用 CWD 解析相对路径会让「上传 report.md」指到一个谁也说不清的文件。
    /// </summary>
    private string ResolveLocalInput(string? input, string label)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            throw new RemoteException($"必须提供 {label}");
        }

        var candidate = Path.IsPathRooted(input)
            ? input
            : Path.Combine(_config.LocalRoots[0], input);

        return CloudPath.AssertLocalPathAllowed(candidate, _config.LocalRoots);
    }

    // ─────────────────────────── L1 文件面 ───────────────────────────

    [McpServerTool(Name = "nutstore_health", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("检查坚果云 WebDAV 连通性、账号身份、配额与本地限流余量，并报告本地同步库是否可用。排查问题时先跑这个。")]
    public async Task<string> HealthAsync(CancellationToken ct)
    {
        object localState = _local.Available
            ? new { available = true, dbPath = _local.DbPath }
            : new
            {
                available = false,
                dbPath = _local.DbPath,
                hint = "未找到本地数据库；只影响 L2 工具，L1 文件面不受影响。",
            };

        object webdavState;
        if (!_config.HasCredentials)
        {
            webdavState = new
            {
                ok = false,
                reason = "NO_CREDENTIALS",
                hint = "设置 NUTSTORE_USER / NUTSTORE_PASS（应用密码）后可用。",
            };
        }
        else
        {
            try
            {
                var h = await _webdav.HealthAsync(ct);
                webdavState = new
                {
                    ok = true,
                    h.BaseUrl,
                    authenticatedAs = h.AuthenticatedAs,
                    probedPath = h.ProbedPath,
                    resource = h.Resource,
                    quota = h.Quota,
                    rateLimit = h.RateLimit,
                    roots = h.Roots,
                    readOnly = h.ReadOnly,
                };
            }
            catch (NutstoreException ex)
            {
                webdavState = new { ok = false, reason = ex.Code, message = ex.Message, hint = ex.Hint };
            }
        }

        return Json(new
        {
            config = new
            {
                host = _config.Host,
                hasCredentials = _config.HasCredentials,
                roots = _config.Roots,
                localRoots = _config.LocalRoots,
                readOnly = _config.ReadOnly,
                rateLimit = _config.RateLimit,
                rateWindowMs = _config.RateWindowMs,
            },
            local = localState,
            webdav = webdavState,
        });
    }

    [McpServerTool(Name = "nutstore_list", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("列出云端某个目录下的文件与子目录（PROPFIND Depth:1）。注意：坚果云单次枚举上限 750 项，超限时结果会标记 truncated=true。")]
    public async Task<string> ListAsync(
        [Description("云端绝对路径，从根开始，例如 \"/我的坚果云/文档\"。大小写敏感。")] string path,
        CancellationToken ct)
    {
        var cloudPath = GuardCloud(path);
        var result = await _webdav.ListAsync(cloudPath, ct);

        return Json(new
        {
            path = result.Path,
            count = result.Entries.Count,
            truncated = result.Truncated,
            warning = result.Warning,
            entries = result.Entries.Select(e => new
            {
                name = e.Path.Split('/')[^1],
                path = e.Path,
                type = e.IsCollection ? "directory" : "file",
                size = e.Size,
                modified = e.LastModified,
            }),
        });
    }

    [McpServerTool(Name = "nutstore_stat", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("获取单个云端路径的类型、大小、修改时间与 ETag。")]
    public async Task<string> StatAsync(
        [Description("云端绝对路径，从根开始。")] string path,
        CancellationToken ct)
        => Json(await _webdav.StatAsync(GuardCloud(path), ct));

    [McpServerTool(Name = "nutstore_read_text", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("直接读出云端文本文件内容（适合 .md/.txt/.json/.csv 等）。默认上限 1 MB —— 超过请改用 nutstore_download 保存到本地。")]
    public async Task<string> ReadTextAsync(
        [Description("云端绝对路径，从根开始。")] string path,
        [Description("读取上限，默认 1048576（1 MB）。")] long maxBytes = 1024 * 1024,
        CancellationToken ct = default)
    {
        var cloudPath = GuardCloud(path);
        var (text, bytes) = await _webdav.ReadTextAsync(cloudPath, maxBytes, ct);
        return Json(new { path = cloudPath, bytes, text });
    }

    [McpServerTool(Name = "nutstore_download", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("把云端文件流式下载到本机。目标路径必须在本机白名单目录内；相对路径相对于第一个白名单目录。")]
    public async Task<string> DownloadAsync(
        [Description("云端绝对路径，从根开始。")] string path,
        [Description("本机目标路径（绝对，或相对于本机白名单目录）。")] string localPath,
        CancellationToken ct)
    {
        var cloudPath = GuardCloud(path);
        var target = ResolveLocalInput(localPath, "localPath");

        var existed = File.Exists(target);
        var dir = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        using var download = await _webdav.DownloadAsync(cloudPath, ct);
        await using (var fs = File.Create(target))
        {
            await download.Stream.CopyToAsync(fs, ct);
        }

        return Json(new
        {
            cloudPath,
            localPath = target,
            bytes = new FileInfo(target).Length,
            declaredSize = download.Size,
            etag = download.Etag,
            lastModified = download.LastModified,
            overwritten = existed,
        });
    }

    [McpServerTool(Name = "nutstore_upload", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true)]
    [Description("把本机文件上传到云端（单文件 ≤ 500 MB）。源文件必须在本机白名单目录内。path 以 \"/\" 结尾时按目录处理并自动补上源文件名；否则视为完整目标路径。")]
    public async Task<string> UploadAsync(
        [Description("本机源文件路径。")] string localPath,
        [Description("云端目标路径。以 \"/\" 结尾表示目录，自动补文件名。")] string path,
        CancellationToken ct)
    {
        RequireWritable("上传文件");

        var source = ResolveLocalInput(localPath, "localPath");

        // 只有明确以 "/" 结尾才当目录处理 —— 不做「看扩展名猜意图」的魔法，
        // 那会在上传无扩展名文件时把名字改掉。
        var cloudPath = path.EndsWith('/')
            ? GuardCloud(path + Path.GetFileName(source))
            : GuardCloud(path);

        var result = await _webdav.UploadFileAsync(cloudPath, source, ct);

        return Json(new
        {
            path = result.Path,
            bytes = result.Bytes,
            sizeKnown = result.SizeKnown,
            created = result.Created,
            localPath = source,
        });
    }

    [McpServerTool(Name = "nutstore_mkdir", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("在云端创建目录；recursive=true 时逐级创建缺失的父目录。目录已存在不算错误。")]
    public async Task<string> MkdirAsync(
        [Description("云端绝对路径，从根开始。")] string path,
        [Description("是否递归创建父目录，默认 false。")] bool recursive = false,
        CancellationToken ct = default)
    {
        RequireWritable("创建目录");
        var cloudPath = GuardCloud(path);

        if (recursive)
        {
            var r = await _webdav.MkdirpAsync(cloudPath, ct);
            return Json(new { path = r.Path, created = r.Created, createdPaths = r.CreatedPaths });
        }

        var single = await _webdav.MkdirAsync(cloudPath, ct);
        return Json(new { path = single.Path, created = single.Created, existed = single.Existed });
    }

    [McpServerTool(Name = "nutstore_move", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true)]
    [Description("把云端资源从 from 移动到 to（同目录内即重命名）。默认不覆盖已存在的目标。")]
    public async Task<string> MoveAsync(
        [Description("源云端路径。")] string from,
        [Description("目标云端路径。")] string to,
        [Description("目标存在时是否覆盖，默认 false。")] bool overwrite = false,
        CancellationToken ct = default)
    {
        RequireWritable("移动资源");
        var r = await _webdav.MoveAsync(GuardCloud(from), GuardCloud(to), overwrite, ct);
        return Json(new { from = r.From, to = r.To, overwritten = r.Overwritten });
    }

    [McpServerTool(Name = "nutstore_delete", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = true)]
    [Description("⚠️ 删除云端资源，目录会连同内容一起删除。必须显式传 confirm=true 才会执行。客户端有回收站，但不要依赖它。")]
    public async Task<string> DeleteAsync(
        [Description("云端绝对路径，从根开始。")] string path,
        [Description("必须显式传 true 才会执行 —— 防止模型误删。")] bool confirm,
        CancellationToken ct)
    {
        RequireWritable("删除资源");

        if (!confirm)
        {
            throw new RemoteException(
                "删除操作需要显式确认",
                hint: "请传入 confirm: true。这一步是刻意设置的：删除是不可逆的高风险操作。");
        }

        var r = await _webdav.RemoveAsync(GuardCloud(path), ct);
        return Json(new { path = r.Path, deleted = r.Deleted, existed = r.Existed });
    }

    // ─────────────────────────── L2 数据面 ───────────────────────────

    [McpServerTool(Name = "nutstore_sync_folders", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("列出本机已挂载的同步文件夹：云端路径、本地镜像路径、对象数、云端独有对象数。同时列出「云端存在但本地未挂载」的目录（unmountedSandboxes）。")]
    public string SyncFolders() => Json(_local.ListSyncFolders());

    [McpServerTool(Name = "nutstore_l3_bind_candidates", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("列出「云端存在但本地未绑定」的沙箱，带上绑定所需的字段（sandboxId、magic、pathPerms）。这些值原样喂给插件侧的 nutstore_l3_bind 即可完成绑定。")]
    public string L3BindCandidates()
    {
        var candidates = _local.BindCandidates();

        return Json(new
        {
            count = candidates.Count,
            candidates,
            note = "magic 是客户端内部标识（类似 inode），绑定时必须原样带上；它不是凭据。"
                 + "真正的密钥 lanSyncCipher / channelId 永不导出。",
        });
    }

    [McpServerTool(Name = "nutstore_sync_status", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("本地同步总览：同步文件夹数、受管对象数、云端独有对象数、同步错误数、客户端设置快照。")]
    public string SyncStatus() => Json(_local.Summary());

    [McpServerTool(Name = "nutstore_file_status", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("查询某个云端路径在本地同步索引里的状态：是否已跟踪、版本、大小、修改时间、是否为「不占用电脑空间」的云端占位符，以及对应的本地绝对路径。")]
    public string FileStatus(
        [Description("云端绝对路径，从根开始。")] string path)
        => Json(_local.FileStatus(GuardCloud(path)));

    [McpServerTool(Name = "nutstore_sync_errors", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("列出客户端记录的同步失败项（文件名冲突、超限、特殊字符等）。")]
    public string SyncErrors(
        [Description("最多返回条数，默认 100。")] int limit = 100)
        => Json(_local.GetSyncErrors(limit));

    [McpServerTool(Name = "nutstore_settings", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("读取客户端设置快照：账号、版本、云桥模式开关、缓存策略、流量统计。只读取白名单字段，token / 密码哈希等敏感键在 SQL 层就被排除。")]
    public string Settings() => Json(_local.GetSettings());
}
