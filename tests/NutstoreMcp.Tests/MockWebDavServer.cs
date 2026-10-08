using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace NutstoreMcp.Tests;

public sealed record MockNode(bool IsDirectory, byte[]? Data = null);

public sealed record RecordedRequest(string Method, string Path, string? Authorization, string? Depth);

/// <summary>
/// 内存版 mock WebDAV 服务器（Kestrel 起真实 HTTP）。
///
/// 用它来真实验证 <see cref="WebDavClient"/>：走真 socket、真状态码、真 multistatus XML，
/// 而不是替换 <c>HttpMessageHandler</c> —— 后者只能证明代码自洽，证明不了协议正确。
/// </summary>
public sealed class MockWebDavServer : IAsyncDisposable
{
    public const string BasePath = "/dav";

    private readonly WebApplication _app;

    private MockWebDavServer(WebApplication app, string baseUrl)
    {
        _app = app;
        BaseUrl = baseUrl;
    }

    public string BaseUrl { get; }

    public string User { get; set; } = "test@example.com";

    public string Pass { get; set; } = "app-password";

    public Dictionary<string, MockNode> Tree { get; } = new(StringComparer.Ordinal) { ["/"] = new MockNode(true) };

    public List<RecordedRequest> Requests { get; } = [];

    /// <summary>模拟坚果云 750 项截断。</summary>
    public int PropfindLimit { get; set; } = int.MaxValue;

    /// <summary>对该前缀的请求返回一次 302（补尾斜杠）。</summary>
    public string? RedirectPrefix { get; set; }

    /// <summary>完全接管某个请求；返回 true 表示已处理。</summary>
    public Func<HttpContext, bool>? CustomHandler { get; set; }

    public static async Task<MockWebDavServer> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        var app = builder.Build();

        MockWebDavServer? server = null;

        app.Run(async context =>
        {
            await server!.HandleAsync(context);
        });

        await app.StartAsync();

        var addresses = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!;
        var url = addresses.Addresses.First().TrimEnd('/');

        server = new MockWebDavServer(app, url + BasePath);
        return server;
    }

    public void AddFile(string path, string content)
    {
        EnsureAncestors(path);
        Tree[path] = new MockNode(false, Encoding.UTF8.GetBytes(content));
    }

    public void AddDirectory(string path)
    {
        EnsureAncestors(path);
        Tree[path] = new MockNode(true);
    }

    /// <summary>
    /// 补齐父目录。真实 WebDAV 的 MKCOL 在父目录缺失时返回 409，
    /// 但测试里没必要手工铺整条链路，只关心自己那一层。
    /// </summary>
    private void EnsureAncestors(string path)
    {
        var idx = path.LastIndexOf('/');
        while (idx > 0)
        {
            var parent = path[..idx];
            if (!Tree.ContainsKey(parent))
            {
                Tree[parent] = new MockNode(true);
            }

            path = parent;
            idx = path.LastIndexOf('/');
        }
    }

    private async Task HandleAsync(HttpContext context)
    {
        var request = context.Request;
        var response = context.Response;

        var raw = request.Path.Value ?? "/";
        var decoded = Uri.UnescapeDataString(raw);
        if (decoded.StartsWith(BasePath, StringComparison.Ordinal))
        {
            decoded = decoded[BasePath.Length..];
        }

        if (decoded.Length == 0)
        {
            decoded = "/";
        }

        var path = decoded.Length > 1 ? decoded.TrimEnd('/') : decoded;
        if (path.Length == 0)
        {
            path = "/";
        }

        lock (Requests)
        {
            Requests.Add(new RecordedRequest(
                request.Method, path, request.Headers.Authorization.ToString(), request.Headers["Depth"].ToString()));
        }

        if (CustomHandler is not null && CustomHandler(context))
        {
            return;
        }

        var expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{User}:{Pass}"));
        if (request.Headers.Authorization.ToString() != expected)
        {
            response.StatusCode = 401;
            response.Headers.WWWAuthenticate = "Basic realm=\"nutstore\"";
            await response.WriteAsync("unauthorized");
            return;
        }

        if (RedirectPrefix is not null
            && path.StartsWith(RedirectPrefix, StringComparison.Ordinal)
            && !raw.EndsWith('/'))
        {
            response.StatusCode = 302;
            response.Headers.Location = $"{request.Scheme}://{request.Host}{raw}/";
            return;
        }

        switch (request.Method)
        {
            case "PROPFIND":
                await HandlePropfindAsync(context, path);
                return;

            case "HEAD":
                if (!Tree.TryGetValue(path, out var headNode))
                {
                    response.StatusCode = 404;
                    return;
                }

                response.StatusCode = 200;
                response.ContentLength = headNode.IsDirectory ? 0 : headNode.Data!.Length;
                return;

            case "GET":
                if (!Tree.TryGetValue(path, out var getNode) || getNode.IsDirectory)
                {
                    response.StatusCode = 404;
                    await response.WriteAsync("not found");
                    return;
                }

                response.StatusCode = 200;
                response.ContentType = "application/octet-stream";
                response.ContentLength = getNode.Data!.Length;
                await response.Body.WriteAsync(getNode.Data);
                return;

            case "PUT":
                using (var ms = new MemoryStream())
                {
                    await request.Body.CopyToAsync(ms);
                    var existed = Tree.ContainsKey(path);
                    Tree[path] = new MockNode(false, ms.ToArray());
                    response.StatusCode = existed ? 204 : 201;
                }

                return;

            case "MKCOL":
                if (Tree.ContainsKey(path))
                {
                    response.StatusCode = 405;
                    return;
                }

                var parent = path[..path.LastIndexOf('/')];
                if (parent.Length == 0)
                {
                    parent = "/";
                }

                if (!Tree.ContainsKey(parent))
                {
                    response.StatusCode = 409;
                    return;
                }

                Tree[path] = new MockNode(true);
                response.StatusCode = 201;
                return;

            case "MOVE":
                if (!Tree.TryGetValue(path, out var moveNode))
                {
                    response.StatusCode = 404;
                    return;
                }

                var destination = request.Headers["Destination"].ToString();
                var destUri = new Uri(destination);
                var destDecoded = Uri.UnescapeDataString(destUri.AbsolutePath);
                if (destDecoded.StartsWith(BasePath, StringComparison.Ordinal))
                {
                    destDecoded = destDecoded[BasePath.Length..];
                }

                var destPath = destDecoded.TrimEnd('/');
                if (destPath.Length == 0)
                {
                    destPath = "/";
                }

                var destExisted = Tree.ContainsKey(destPath);
                Tree[destPath] = moveNode;
                Tree.Remove(path);
                response.StatusCode = destExisted ? 204 : 201;
                return;

            case "DELETE":
                if (!Tree.Remove(path))
                {
                    response.StatusCode = 404;
                    return;
                }

                response.StatusCode = 204;
                return;

            default:
                response.StatusCode = 405;
                return;
        }
    }

    private async Task HandlePropfindAsync(HttpContext context, string path)
    {
        var response = context.Response;

        if (!Tree.TryGetValue(path, out var node))
        {
            response.StatusCode = 404;
            await response.WriteAsync("not found");
            return;
        }

        var depth = context.Request.Headers["Depth"].ToString() == "0" ? 0 : 1;
        var targets = new List<string> { path };
        if (depth == 1 && node.IsDirectory)
        {
            targets.AddRange(Tree.Keys
                .Where(k => k != path)
                .Where(k => ParentOf(k) == path)
                .OrderBy(k => k, StringComparer.Ordinal));
        }

        var selected = targets;
        if (targets.Count > PropfindLimit)
        {
            selected = targets.Take(PropfindLimit).ToList();
        }

        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        sb.Append("<D:multistatus xmlns:D=\"DAV:\">");
        foreach (var t in selected)
        {
            var n = Tree[t];
            var segments = t.Split('/').Select(Uri.EscapeDataString);
            var href = t == "/" ? "/" : string.Join('/', segments) + (n.IsDirectory ? "/" : string.Empty);

            sb.Append("<D:response>");
            sb.Append($"<D:href>{SecurityElement(href)}</D:href>");
            sb.Append("<D:propstat><D:prop>");
            sb.Append(n.IsDirectory ? "<D:resourcetype><D:collection/></D:resourcetype>" : "<D:resourcetype/>");
            if (!n.IsDirectory)
            {
                sb.Append($"<D:getcontentlength>{n.Data!.Length}</D:getcontentlength>");
                sb.Append("<D:getcontenttype>application/octet-stream</D:getcontenttype>");
            }

            sb.Append($"<D:getlastmodified>{DateTime.UtcNow:R}</D:getlastmodified>");
            sb.Append($"<D:getetag>\"{t.Length}-etag\"</D:getetag>");
            sb.Append("</D:prop><D:status>HTTP/1.1 200 OK</D:status></D:propstat>");
            sb.Append("</D:response>");
        }

        sb.Append("</D:multistatus>");

        response.StatusCode = 207;
        response.ContentType = "application/xml; charset=utf-8";
        await response.WriteAsync(sb.ToString());
    }

    private static string ParentOf(string path)
    {
        var idx = path.LastIndexOf('/');
        return idx <= 0 ? "/" : path[..idx];
    }

    private static string SecurityElement(string s) => s
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal)
        .Replace("'", "&apos;", StringComparison.Ordinal);

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
