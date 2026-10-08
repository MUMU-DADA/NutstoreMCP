using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NutstoreMcp.Plugin.Transport;

/// <summary>
/// 把官方 MCP SDK 的「流对」传输暴露成 MCP Streamable HTTP 端点。
///
/// 为什么需要它：插件跑在 NutstoreClient.exe 进程里，而 GUI 程序没有 stdio，
/// 所以 stdio 传输用不了。DSH 的 MCP 客户端支持 <c>streamable-http</c>，正好对上。
///
/// 结构：
/// <code>
///   HTTP POST /mcp ──► 解析 JSON-RPC ──► _toSdk（SDK 读）
///                                          │
///   HTTP 响应     ◄── 按 id 配对     ◄── _fromSdk（SDK 写）
/// </code>
///
/// 只绑 <c>127.0.0.1</c>，不监听外部网卡 —— 这个端点能读坚果云的本地状态，
/// 绝不能对外暴露。
/// </summary>
internal sealed class HttpMcpServer : IDisposable
{
    /// <summary>MCP 端点路径。</summary>
    public const string Path = "/mcp";

    private readonly HttpListener _listener = new();
    private readonly QueueStream _toSdk = new();
    private readonly QueueStream _fromSdk = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _pending = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Action<string> _log;

    private int _requestSeq;
    private bool _disposed;

    public HttpMcpServer(Action<string> log)
    {
        _log = log;
    }

    /// <summary>喂给 SDK 的输入流（SDK 从这里读请求）。</summary>
    public Stream SdkInput => _toSdk;

    /// <summary>SDK 的输出流（SDK 往这里写响应）。</summary>
    public Stream SdkOutput => _fromSdk;

    public int Port { get; private set; }

    public void Start(int port)
    {
        // 只绑回环。用 127.0.0.1 而不是 localhost：后者在 hosts 被改过时
        // 可能解析到别处，而这里必须保证「只有本机能连」。
        _listener.Prefixes.Add($"http://127.0.0.1:{port}{Path}/");
        _listener.Start();
        Port = port;

        _ = Task.Run(AcceptLoopAsync);
        _ = Task.Run(ReadSdkOutputLoopAsync);

        _log($"MCP 端点已监听 http://127.0.0.1:{port}{Path}");
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Stop()/Close() 会打断 GetContextAsync —— 正常退出路径
                return;
            }

            _ = Task.Run(() => HandleAsync(ctx));
        }
    }

    /// <summary>读 SDK 写出的每一行 JSON-RPC 响应，按 id 唤醒等待中的 HTTP 请求。</summary>
    private async Task ReadSdkOutputLoopAsync()
    {
        var buffer = new byte[8192];
        var acc = new StringBuilder();

        while (!_cts.IsCancellationRequested)
        {
            int n;
            try
            {
                n = await Task.Run(() => _fromSdk.Read(buffer, 0, buffer.Length), _cts.Token)
                             .ConfigureAwait(false);
            }
            catch (Exception)
            {
                return;
            }

            if (n <= 0)
            {
                return; // EOF
            }

            acc.Append(Encoding.UTF8.GetString(buffer, 0, n));

            // SDK 的流传输按行分隔；一行可能被拆成多次 Read，所以攒到换行为止
            while (true)
            {
                var text = acc.ToString();
                var nl = text.IndexOf('\n');
                if (nl < 0)
                {
                    break;
                }

                var line = text.Substring(0, nl).Trim();
                acc.Remove(0, nl + 1);

                if (line.Length > 0)
                {
                    DispatchToWaiter(line);
                }
            }
        }
    }

    private void DispatchToWaiter(string json)
    {
        string? id = null;
        try
        {
            var node = JsonNode.Parse(json);
            id = node?["id"]?.ToString();
        }
        catch (JsonException)
        {
            // 无法解析 —— 丢弃
        }

        if (id is not null && _pending.TryRemove(id, out var tcs))
        {
            tcs.TrySetResult(json);
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        try
        {
            await HandleCoreAsync(ctx).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 绝不把异常抛到宿主进程 —— 这是跑在用户同步客户端里的代码
            _log($"处理请求出错: {ex.GetType().Name}: {ex.Message}");
            TryFail(ctx, 500, "internal error");
        }
    }

    private async Task HandleCoreAsync(HttpListenerContext ctx)
    {
        var req = ctx.Request;

        // 只接受回环来源。HttpListener 已经绑了 127.0.0.1，这里是第二道闸。
        if (!IPAddress.IsLoopback(req.RemoteEndPoint.Address))
        {
            TryFail(ctx, 403, "loopback only");
            return;
        }

        if (req.Url?.AbsolutePath.TrimEnd('/') != Path)
        {
            TryFail(ctx, 404, "not found");
            return;
        }

        switch (req.HttpMethod)
        {
            case "POST":
                await HandlePostAsync(ctx).ConfigureAwait(false);
                return;

            // 本实现不做服务端主动推送（SSE），也没有会话要删。
            // 明确回 405 而不是假装支持 —— 让客户端早点发现。
            case "GET":
            case "DELETE":
                TryFail(ctx, 405, "server-initiated streams are not supported");
                return;

            default:
                TryFail(ctx, 405, "method not allowed");
                return;
        }
    }

    private async Task HandlePostAsync(HttpListenerContext ctx)
    {
        string body;
        using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
        {
            body = await reader.ReadToEndAsync().ConfigureAwait(false);
        }

        if (body.Trim().Length == 0)
        {
            TryFail(ctx, 400, "empty body");
            return;
        }

        // 一个 POST 可能是单条消息，也可能是批量的
        List<JsonNode> messages;
        try
        {
            var parsed = JsonNode.Parse(body);
            messages = parsed is JsonArray arr
                ? [.. arr.Where(x => x is not null).Select(x => x!)]
                : [parsed!];
        }
        catch (JsonException ex)
        {
            TryFail(ctx, 400, $"invalid JSON: {ex.Message}");
            return;
        }

        var responses = new List<string>();

        foreach (var msg in messages)
        {
            var idNode = msg["id"];

            // 通知（无 id）只需投递，不需要回应
            if (idNode is null || msg["method"] is null)
            {
                _toSdk.PushLine(msg.ToJsonString());
                continue;
            }

            var id = idNode.ToString();
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

            // 防御：客户端送来重复 id 时不能把前一个请求永久挂住
            if (!_pending.TryAdd(id, tcs))
            {
                TryFail(ctx, 400, $"duplicate request id: {id}");
                return;
            }

            try
            {
                _toSdk.PushLine(msg.ToJsonString());

                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, _cts.Token);

                var completed = await Task.WhenAny(tcs.Task, Task.Delay(Timeout.Infinite, linked.Token))
                                          .ConfigureAwait(false);

                if (completed != tcs.Task)
                {
                    TryFail(ctx, 504, "timeout waiting for server response");
                    return;
                }

                responses.Add(await tcs.Task.ConfigureAwait(false));
            }
            finally
            {
                _pending.TryRemove(id, out _);
            }
        }

        // 只有通知 → 202 Accepted，符合 Streamable HTTP 规范
        if (responses.Count == 0)
        {
            ctx.Response.StatusCode = 202;
            ctx.Response.Close();
            return;
        }

        var payload = responses.Count == 1
            ? responses[0]
            : "[" + string.Join(",", responses) + "]";

        await WriteJsonAsync(ctx, payload).ConfigureAwait(false);
    }

    private static async Task WriteJsonAsync(HttpListenerContext ctx, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
        ctx.Response.Close();
    }

    private static void TryFail(HttpListenerContext ctx, int status, string message)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(message);
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "text/plain; charset=utf-8";
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            ctx.Response.Close();
        }
        catch (Exception)
        {
            // 连接已经断了 —— 无所谓
        }
    }

    /// <summary>给需要主动发起请求的场景分配一个自增 id。</summary>
    internal string NextId() => Interlocked.Increment(ref _requestSeq).ToString();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            _cts.Cancel();
            _listener.Stop();
            _listener.Close();
        }
        catch (Exception)
        {
            // 关闭失败不值得上报
        }

        _toSdk.CompleteAdding();
        _fromSdk.CompleteAdding();

        foreach (var kv in _pending)
        {
            kv.Value.TrySetCanceled();
        }

        _pending.Clear();
        _cts.Dispose();
    }
}
