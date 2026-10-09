using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace NutstoreMcp;

public sealed record ListResult(string Path, IReadOnlyList<CloudEntry> Entries, bool Truncated, string? Warning);

public sealed record QuotaInfo(long? AvailableBytes, long? UsedBytes);

public sealed record DownloadResult(Stream Stream, long? Size, string? Etag, string? LastModified, string? ContentType)
    : IDisposable
{
    public void Dispose() => Stream.Dispose();
}

public sealed record UploadResult(string Path, long? Bytes, bool SizeKnown, bool Created);

public sealed record MkdirResult(string Path, bool Created, bool Existed);

public sealed record MkdirpResult(string Path, bool Created, IReadOnlyList<string> CreatedPaths);

public sealed record TransferResult(string From, string To, bool Overwritten);

public sealed record DeleteResult(string Path, bool Deleted, bool Existed);

public sealed record HealthResult(
    string BaseUrl,
    string? AuthenticatedAs,
    string ProbedPath,
    CloudEntry? Resource,
    QuotaInfo? Quota,
    RateLimitSnapshot RateLimit,
    IReadOnlyList<string> Roots,
    bool ReadOnly);

/// <summary>
/// L1 文件面 —— WebDAV 客户端。
///
/// 三个必须记住的官方限制（见 ARCHITECTURE.md）：
///   1. 单次目录枚举最多 750 项 —— 超了会被静默截断，本类**显式报告**而非假装完整
///   2. 单文件上传 500 MB
///   3. 600 次 / 30 分钟（免费）—— 每次请求都过限流器
/// </summary>
public sealed class WebDavClient : IDisposable
{
    private static readonly HashSet<int> RedirectStatuses = [301, 302, 303, 307, 308];
    private const int MaxRedirects = 5;

    private readonly AppConfig _config;
    private readonly SlidingWindowRateLimiter _limiter;
    private readonly HttpClient _http;
    private readonly string? _authHeader;
    private readonly bool _ownsHttp;

    public WebDavClient(AppConfig config, SlidingWindowRateLimiter limiter, HttpMessageHandler? handler = null)
    {
        _config = config;
        _limiter = limiter;
        _ownsHttp = handler is null;

        handler ??= new SocketsHttpHandler
        {
            // 手动处理重定向：自动重定向会把 PROPFIND/PUT 改写成 GET（或丢掉请求体）
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(30),
        };

        _http = new HttpClient(handler, disposeHandler: _ownsHttp)
        {
            Timeout = Timeout.InfiniteTimeSpan, // 超时按请求用 CTS 控制
        };

        _authHeader = config.HasCredentials
            ? "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{config.User}:{config.Pass}"))
            : null;
    }

    public string BaseUrl => _config.Host;

    public void Dispose()
    {
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }

    /// <summary>凭据缺失时给出可操作的错误，而不是发一个注定 401 的请求。</summary>
    private void RequireCredentials()
    {
        if (_authHeader is null)
        {
            throw new AuthException(
                "未配置 WebDAV 凭据，L1 文件面不可用",
                "请设置 NUTSTORE_USER 与 NUTSTORE_PASS（应用密码）。"
                + "只使用 L2 本地只读能力时无需凭据。");
        }
    }

    private void Guard(string cloudPath) => CloudPath.AssertAllowed(cloudPath, _config.Roots);

    private sealed record RawResponse(HttpResponseMessage Response, string FinalUrl) : IDisposable
    {
        public void Dispose() => Response.Dispose();
    }

    /// <summary>
    /// 发一次带认证的请求，手动处理重定向以便**保留方法**。
    ///
    /// 拒绝跨源重定向：Basic 凭据是明文，跟着跳到别的域等于泄露应用密码。
    /// </summary>
    private async Task<RawResponse> SendAsync(
        string method,
        string cloudPath,
        Func<HttpContent>? contentFactory,
        IReadOnlyDictionary<string, string>? extraHeaders,
        int? timeoutMs,
        CancellationToken ct)
    {
        RequireCredentials();
        Guard(cloudPath);

        var url = CloudPath.ToUrl(BaseUrl, cloudPath);
        var origin = new Uri(url).GetLeftPart(UriPartial.Authority);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeoutMs ?? _config.TimeoutMs);

        var current = url;
        var hops = 0;

        while (true)
        {
            _limiter.Acquire(SlidingWindowRateLimiter.NowMs());

            using var request = new HttpRequestMessage(new HttpMethod(method), current);
            request.Headers.TryAddWithoutValidation("Authorization", _authHeader);
            request.Headers.TryAddWithoutValidation("User-Agent", "nutstore-mcp/0.1 (+local)");

            if (extraHeaders is not null)
            {
                foreach (var (k, v) in extraHeaders)
                {
                    request.Headers.TryAddWithoutValidation(k, v);
                }
            }

            if (contentFactory is not null)
            {
                request.Content = contentFactory();
            }

            var response = await SendOnceAsync(request, origin, timeoutCts, timeoutMs, ct);

            if (!RedirectStatuses.Contains((int)response.StatusCode))
            {
                return new RawResponse(response, current);
            }

            var location = response.Headers.Location?.ToString();
            response.Dispose();

            if (string.IsNullOrEmpty(location))
            {
                throw new RemoteException(
                    $"收到 {(int)response.StatusCode} 重定向但缺少 Location 头");
            }

            var next = new Uri(new Uri(current), location);
            var nextOrigin = next.GetLeftPart(UriPartial.Authority);
            if (!string.Equals(nextOrigin, origin, StringComparison.OrdinalIgnoreCase))
            {
                throw new RemoteException(
                    $"拒绝跟随跨源重定向：{origin} → {nextOrigin}",
                    hint: "Basic 凭据会随重定向泄露到第三方域，已主动中止。");
            }

            if (++hops > MaxRedirects)
            {
                throw new RemoteException($"重定向次数超过 {MaxRedirects} 次");
            }

            current = next.ToString();
        }
    }

    /// <summary>
    /// 真正把请求发出去，并把**传输层**失败翻译成结构化错误。
    ///
    /// 为什么不靠调用方 catch：HttpClient 在 DNS/TCP/TLS/超时时抛的是
    /// <see cref="HttpRequestException"/> 或 <see cref="TaskCanceledException"/>，
    /// 而调用方只 catch <see cref="NutstoreException"/>。这类异常以前会一路穿到
    /// <c>--doctor</c> 顶层变成未处理崩溃（实测：TLS 失败整段自检直接退出），
    /// 在 MCP 工具层则退化成一句通用错误。网络问题和「服务端拒绝」必须分得开。
    /// </summary>
    private async Task<HttpResponseMessage> SendOnceAsync(
        HttpRequestMessage request,
        string origin,
        CancellationTokenSource timeoutCts,
        int? timeoutMs,
        CancellationToken ct)
    {
        try
        {
            return await _http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeoutCts.Token);
        }
        catch (HttpRequestException ex)
        {
            throw new NetworkException(
                $"连不上 {origin}：{ex.Message}",
                hint: "DNS/连接/TLS 握手失败。受限沙箱或受限令牌下 Schannel 可能拿不到凭据"
                    + "（0x8009030E「安全包中没有可用的凭证」），这种环境要在沙箱外才能连通。",
                cause: ex);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            // 调用方没取消 → 只能是上面那个 CancelAfter 到点了
            throw new NetworkException(
                $"请求超时：{origin}（{timeoutMs ?? _config.TimeoutMs} ms）",
                hint: "调大 NUTSTORE_TIMEOUT_MS，或先确认到该主机的连通性。",
                cause: ex);
        }
    }

    private static async Task<NutstoreException> ErrorFromAsync(HttpResponseMessage response, string method, string cloudPath)
    {
        string detail;
        try
        {
            detail = (await response.Content.ReadAsStringAsync()).ReplaceLineEndings(" ").Trim();
            if (detail.Length > 200)
            {
                detail = detail[..200];
            }
        }
        catch (HttpRequestException)
        {
            detail = string.Empty;
        }

        var suffix = string.IsNullOrEmpty(detail) ? string.Empty : $"：{detail}";
        var status = (int)response.StatusCode;

        return status switch
        {
            401 => new AuthException(
                $"WebDAV 认证失败（401）{suffix}",
                "请检查 NUTSTORE_USER / NUTSTORE_PASS。密码必须是网页端生成的"
                + "「应用密码」，不是登录密码。"
                + "生成步骤：https://help.jianguoyun.com/?p=2064"),
            403 => new RemoteException($"无权限访问 \"{cloudPath}\"（403）{suffix}", status),
            404 => new RemoteNotFoundException(
                $"远端不存在：\"{cloudPath}\"（404）",
                "路径大小写敏感，且必须从云端根开始写全。"),
            405 => new RemoteException(
                $"方法 {method} 不被支持（405）{suffix}",
                status,
                method == "MKCOL" ? "目标是已存在的目录，无需创建。" : null),
            409 => new RemoteException(
                $"冲突（409）{suffix}",
                status,
                "通常是父目录不存在 —— 请先创建上级目录。"),
            423 => new RemoteException($"资源被锁定（423）{suffix}", status),
            429 => new RemoteException(
                $"服务端限流（429）{suffix}",
                status,
                "坚果云 WebDAV 上限为 600 次 / 30 分钟（免费版）。请降低请求频率。"),
            507 => new RemoteException($"存储空间或流量配额不足（507）{suffix}", status),
            _ => new RemoteException($"{method} {cloudPath} 失败：HTTP {status}{suffix}", status),
        };
    }

    private static StringContent XmlContent(string body)
        => new(body, Encoding.UTF8, "application/xml");

    public async Task<MultiStatusResult> PropFindAsync(
        string cloudPath, int depth, CancellationToken ct, string? body = null)
    {
        var headers = new Dictionary<string, string>(StringComparer.Ordinal) { ["Depth"] = depth.ToString() };
        using var raw = await SendAsync(
            "PROPFIND", cloudPath, () => XmlContent(body ?? MultiStatus.PropFindBody()), headers, null, ct);

        if ((int)raw.Response.StatusCode == 207)
        {
            var xml = await raw.Response.Content.ReadAsStringAsync(ct);
            return MultiStatus.Parse(xml, BaseUrl, cloudPath);
        }

        var error = await ErrorFromAsync(raw.Response, "PROPFIND", cloudPath);
        raw.Response.Dispose();
        throw error;
    }

    public async Task<ListResult> ListAsync(string cloudPath, CancellationToken ct)
    {
        var result = await PropFindAsync(cloudPath, 1, ct);
        var warning = result.Truncated
            ? $"返回条目达到 {MultiStatus.ListLimit} 条上限，结果可能不完整。"
              + "坚果云 WebDAV 单次枚举上限为 750 项；请改用更具体的子目录重新列举。"
            : null;
        return new ListResult(cloudPath, result.Entries, result.Truncated, warning);
    }

    public async Task<CloudEntry> StatAsync(string cloudPath, CancellationToken ct)
    {
        var result = await PropFindAsync(cloudPath, 0, ct);
        return result.Self ?? throw new RemoteNotFoundException($"远端不存在：\"{cloudPath}\"");
    }

    public async Task<bool> ExistsAsync(string cloudPath, CancellationToken ct)
    {
        using var raw = await SendAsync("HEAD", cloudPath, null, null, null, ct);
        var status = (int)raw.Response.StatusCode;
        if (status is 200 or 204)
        {
            return true;
        }

        if (status is 404 or 403)
        {
            return false;
        }

        var error = await ErrorFromAsync(raw.Response, "HEAD", cloudPath);
        raw.Response.Dispose();
        throw error;
    }

    /// <summary>
    /// 诊断探针的落点。
    ///
    /// 刻意**不用**云端根 <c>/</c> —— 那会绕过 NUTSTORE_ROOTS 白名单。
    /// 白名单是安全边界，诊断工具没有理由成为例外；配额属性在任意资源上
    /// 查询都会返回账号级数值。
    /// </summary>
    private string? ProbePath() => _config.Roots.Count > 0 ? _config.Roots[0] : null;

    /// <summary>查询配额。服务端不支持时返回 null，而不是报错。</summary>
    public async Task<QuotaInfo?> QuotaAsync(CancellationToken ct)
    {
        var path = ProbePath();
        if (path is null)
        {
            return null;
        }

        var headers = new Dictionary<string, string>(StringComparer.Ordinal) { ["Depth"] = "0" };
        using var raw = await SendAsync("PROPFIND", path, () => XmlContent(MultiStatus.QuotaBody()), headers, null, ct);
        if ((int)raw.Response.StatusCode != 207)
        {
            raw.Response.Dispose();
            return null;
        }

        var xml = await raw.Response.Content.ReadAsStringAsync(ct);
        var parsed = MultiStatus.ParseQuota(xml);
        return parsed is null ? null : new QuotaInfo(parsed.Value.AvailableBytes, parsed.Value.UsedBytes);
    }

    public async Task<DownloadResult> DownloadAsync(string cloudPath, CancellationToken ct, int? timeoutMs = null)
    {
        var raw = await SendAsync("GET", cloudPath, null, null, timeoutMs ?? 5 * 60_000, ct);
        if (raw.Response.StatusCode == HttpStatusCode.OK)
        {
            var stream = await raw.Response.Content.ReadAsStreamAsync(ct);
            return new DownloadResult(
                stream,
                raw.Response.Content.Headers.ContentLength,
                raw.Response.Headers.ETag?.ToString(),
                raw.Response.Content.Headers.LastModified?.ToString("R"),
                raw.Response.Content.Headers.ContentType?.ToString());
        }

        var error = await ErrorFromAsync(raw.Response, "GET", cloudPath);
        raw.Response.Dispose();
        throw error;
    }

    /// <summary>读小文本文件。默认上限 1 MB —— 再大就不该塞进模型上下文了。</summary>
    public async Task<(string Text, long Bytes)> ReadTextAsync(
        string cloudPath, long maxBytes, CancellationToken ct, Encoding? encoding = null)
    {
        using var raw = await SendAsync("GET", cloudPath, null, null, null, ct);
        if (raw.Response.StatusCode != HttpStatusCode.OK)
        {
            var error = await ErrorFromAsync(raw.Response, "GET", cloudPath);
            throw error;
        }

        var declared = raw.Response.Content.Headers.ContentLength;
        if (declared is not null && declared > maxBytes)
        {
            throw new RemoteException(
                $"文件大小 {declared} 字节，超过读取上限 {maxBytes} 字节",
                hint: "如需完整文件请改用 nutstore_download 保存到本地。上限可用 maxBytes 参数调整。");
        }

        await using var stream = await raw.Response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        long total = 0;
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            total += read;
            if (total > maxBytes)
            {
                throw new RemoteException($"文件超过读取上限 {maxBytes} 字节");
            }

            buffer.Write(chunk, 0, read);
        }

        var bytes = buffer.ToArray();
        return ((encoding ?? Encoding.UTF8).GetString(bytes), bytes.LongLength);
    }

    public async Task<UploadResult> UploadAsync(
        string cloudPath, Func<Stream> streamFactory, long? contentLength, string? contentType, CancellationToken ct)
    {
        if (contentLength is not null && contentLength > _config.MaxUploadBytes)
        {
            throw new RemoteException(
                $"文件大小 {contentLength} 字节超过上传上限 {_config.MaxUploadBytes} 字节",
                hint: "坚果云 WebDAV 单文件上传限制为 500 MB。"
                      + "更大的文件请放入本地同步目录，由客户端同步。");
        }

        HttpContent Factory()
        {
            var content = new StreamContent(streamFactory());
            if (contentLength is not null)
            {
                content.Headers.ContentLength = contentLength;
            }

            content.Headers.ContentType = new MediaTypeHeaderValue(contentType ?? "application/octet-stream");
            return content;
        }

        using var raw = await SendAsync("PUT", cloudPath, Factory, null, 10 * 60_000, ct);
        var status = (int)raw.Response.StatusCode;
        if (status is 200 or 201 or 204)
        {
            return new UploadResult(cloudPath, contentLength, contentLength is not null, status == 201);
        }

        var error = await ErrorFromAsync(raw.Response, "PUT", cloudPath);
        raw.Response.Dispose();
        throw error;
    }

    public async Task<UploadResult> UploadFileAsync(string cloudPath, string localPath, CancellationToken ct)
    {
        var info = new FileInfo(localPath);
        if (!info.Exists)
        {
            throw new RemoteException($"\"{localPath}\" 不存在");
        }

        return await UploadAsync(cloudPath, () => File.OpenRead(localPath), info.Length, null, ct);
    }

    /// <summary>建目录。已存在时返回 <c>Created=false</c>，不算错误。</summary>
    public async Task<MkdirResult> MkdirAsync(string cloudPath, CancellationToken ct)
    {
        using var raw = await SendAsync("MKCOL", cloudPath, null, null, null, ct);
        var status = (int)raw.Response.StatusCode;
        if (status == 201)
        {
            return new MkdirResult(cloudPath, true, false);
        }

        if (status == 405)
        {
            return new MkdirResult(cloudPath, false, true);
        }

        var error = await ErrorFromAsync(raw.Response, "MKCOL", cloudPath);
        raw.Response.Dispose();
        throw error;
    }

    /// <summary>递归建目录（逐级 MKCOL）。</summary>
    public async Task<MkdirpResult> MkdirpAsync(string cloudPath, CancellationToken ct)
    {
        if (cloudPath == CloudPath.Root)
        {
            return new MkdirpResult(CloudPath.Root, false, []);
        }

        var made = new List<string>();
        var current = string.Empty;
        foreach (var seg in cloudPath.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current += "/" + seg;
            var r = await MkdirAsync(current, ct);
            if (r.Created)
            {
                made.Add(current);
            }
        }

        return new MkdirpResult(cloudPath, made.Count > 0, made);
    }

    private async Task<TransferResult> TransferAsync(
        string method, string from, string to, bool overwrite, CancellationToken ct)
    {
        var headers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Destination"] = CloudPath.ToUrl(BaseUrl, to),
            ["Overwrite"] = overwrite ? "T" : "F",
        };

        using var raw = await SendAsync(method, from, null, headers, null, ct);
        var status = (int)raw.Response.StatusCode;
        if (status is 201 or 204)
        {
            return new TransferResult(from, to, status == 204);
        }

        var error = await ErrorFromAsync(raw.Response, method, from);
        raw.Response.Dispose();
        throw error;
    }

    public Task<TransferResult> MoveAsync(string from, string to, bool overwrite, CancellationToken ct)
    {
        Guard(to);
        return TransferAsync("MOVE", from, to, overwrite, ct);
    }

    public Task<TransferResult> CopyAsync(string from, string to, bool overwrite, CancellationToken ct)
    {
        Guard(to);
        return TransferAsync("COPY", from, to, overwrite, ct);
    }

    public async Task<DeleteResult> RemoveAsync(string cloudPath, CancellationToken ct)
    {
        using var raw = await SendAsync("DELETE", cloudPath, null, null, null, ct);
        var status = (int)raw.Response.StatusCode;
        if (status is 200 or 204)
        {
            return new DeleteResult(cloudPath, true, true);
        }

        if (status == 404)
        {
            return new DeleteResult(cloudPath, false, false);
        }

        var error = await ErrorFromAsync(raw.Response, "DELETE", cloudPath);
        raw.Response.Dispose();
        throw error;
    }

    /// <summary>连通性与配额自检。</summary>
    public async Task<HealthResult> HealthAsync(CancellationToken ct)
    {
        var probe = ProbePath()
            ?? throw new RemoteException(
                "云端白名单为空，没有可探测的路径",
                hint: "请设置 NUTSTORE_ROOTS，例如 NUTSTORE_ROOTS=我的坚果云");

        var result = await PropFindAsync(probe, 0, ct);

        QuotaInfo? quota = null;
        try
        {
            quota = await QuotaAsync(ct);
        }
        catch (NutstoreException)
        {
            quota = null; // 服务端不支持配额查询不算故障
        }

        return new HealthResult(
            BaseUrl,
            _config.User,
            probe,
            result.Self,
            quota,
            _limiter.Snapshot(SlidingWindowRateLimiter.NowMs()),
            _config.Roots,
            _config.ReadOnly);
    }
}
