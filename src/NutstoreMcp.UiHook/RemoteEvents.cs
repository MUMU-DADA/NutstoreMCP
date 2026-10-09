// 读取单页云端事件，不进入客户端本地 EventDb 或最近页状态。
using NutstoreMcp.ClientBindings;
using NutstoreMcp.Plugin.L3;
using System.Globalization;
using System.IO;
using System.Net;
using System.Reflection;

namespace NutstoreMcp.UiHook;

public sealed partial class UiHook
{
    /// <summary>
    /// 只请求并解析一页远端事件。
    /// </summary>
    /// <remarks>
    /// 不调用客户端的 c524... 入口，因为那个入口在读取后会写 EventDb、未读数和最近页。
    /// 会话头只在默认域的局部变量中存在；返回值只包含脱敏游标和 NDJSON 字符串。
    /// </remarks>
    public string ReadRemoteEventsPage(long sandboxId, long magic, string localRoot, long cursor)
    {
        object? response = null;
        Stream? rawStream = null;
        Stream? safeStream = null;
        try
        {
            if (sandboxId <= 0 || magic <= 0)
                return RemoteEventsFailure(sandboxId, cursor, "sandboxId / magic 必须为正数");
            if (string.IsNullOrWhiteSpace(localRoot) || !Path.IsPathRooted(localRoot))
                return RemoteEventsFailure(sandboxId, cursor, "localRoot 必须是绝对的本地路径");
            if (cursor < 0)
                return RemoteEventsFailure(sandboxId, cursor, "cursor 必须是非负整数");

            var api = FindClientType(ClientSymbols.CloudOpsType);
            var requestType = FindClientType(ClientSymbols.NutstoreRequestType);
            var netType = FindClientType(ClientSymbols.NutstoreNetManagerType);
            if (api is null || requestType is null || netType is null)
                return RemoteEventsFailure(sandboxId, cursor, "找不到客户端事件读取类型（版本可能变了）");

            var box = BuildNSSandbox(sandboxId, magic, localRoot, out var boxError);
            if (box is null)
                return RemoteEventsFailure(sandboxId, cursor, boxError ?? "NSSandbox 构造失败");

            var sessionMethod = api.GetMethod(
                ClientSymbols.SessionHeaderHelper,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                binder: null, types: Type.EmptyTypes, modifiers: null);
            var session = sessionMethod?.Invoke(null, null) as string;
            if (string.IsNullOrWhiteSpace(session))
                return RemoteEventsFailure(sandboxId, cursor, "客户端没有可用会话");

            var makeRequest = requestType.GetMethod(
                ClientSymbols.MakeGetEventsRequest,
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                types: new[] { box.GetType(), typeof(long), typeof(string) },
                modifiers: null);
            if (makeRequest is null)
                return RemoteEventsFailure(sandboxId, cursor, "找不到事件请求构造方法（版本可能变了）");

            var request = makeRequest.Invoke(null, new object[] { box, cursor, session! });
            if (request is null)
                return RemoteEventsFailure(sandboxId, cursor, "事件请求构造返回 null");

            var send = netType.GetMethod(
                ClientSymbols.SendHttpRequest,
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                types: new[] { requestType, typeof(string) },
                modifiers: null);
            if (send is null)
                return RemoteEventsFailure(sandboxId, cursor, "找不到客户端 HTTP 请求方法（版本可能变了）");

            response = send.Invoke(null, new object?[] { request, null });
            if (response is null)
                return RemoteEventsFailure(sandboxId, cursor, "事件请求没有响应");

            var status = RemoteStatus(response);
            if (status != (int)HttpStatusCode.OK)
                return RemoteEventsFailure(sandboxId, cursor, "事件请求返回非 200", status);

            var contentType = Get(response, ClientSymbols.ContentTypeMember) as string;
            var contentTypeText = contentType?.TrimStart();
            if (string.IsNullOrWhiteSpace(contentTypeText))
                return RemoteEventsFailure(sandboxId, cursor, "事件响应不是文本内容", status);
            if (!contentTypeText!.StartsWith("text", StringComparison.OrdinalIgnoreCase))
                return RemoteEventsFailure(sandboxId, cursor, "事件响应不是文本内容", status);

            var latest = RemoteHeader(response, ClientSymbols.LatestEventHeader);
            var more = RemoteHeader(response, ClientSymbols.MoreDataHeader);
            var encoding = Get(response, ClientSymbols.ContentEncodingMember) as string;

            var getStream = response.GetType().GetMethod(
                ClientSymbols.GetResponseStream,
                BindingFlags.Public | BindingFlags.Instance,
                binder: null, types: Type.EmptyTypes, modifiers: null);
            rawStream = getStream?.Invoke(response, null) as Stream;
            if (rawStream is null)
                return RemoteEventsFailure(sandboxId, cursor, "事件响应没有正文流", status);

            safeStream = WrapResponseStream(netType, rawStream);
            var page = RemoteEventCodec.ParsePage(cursor, latest, more, safeStream, encoding);
            return Json.Obj(
                ("ok", true),
                ("sandboxId", sandboxId),
                ("cursorIn", page.CursorIn),
                ("cursorOut", page.CursorOut),
                ("hasMore", page.HasMore),
                ("complete", true),
                ("eventCount", page.EventCount),
                ("payloadBytes", page.PayloadBytes),
                ("events", Json.StringArray(page.Events)));
        }
        catch (Exception ex)
        {
            return RemoteEventsFailure(sandboxId, cursor, CapabilityError(ex),
                response is null ? null : RemoteStatus(response));
        }
        finally
        {
            RemoteEventCodec.DisposeQuietly(safeStream is not null && !ReferenceEquals(safeStream, rawStream) ? safeStream : null);
            RemoteEventCodec.DisposeQuietly(rawStream);
            RemoteEventCodec.DisposeQuietly(response as IDisposable);
        }
    }

    private static Stream WrapResponseStream(Type netType, Stream raw)
    {
        var safeType = FindClientType(ClientSymbols.ReadWriteTimeoutSafeStreamType);
        var ctor = safeType?.GetConstructor(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null, types: new[] { typeof(Stream) }, modifiers: null);
        return ctor?.Invoke(new object[] { raw }) as Stream ?? raw;
    }

    private static int? RemoteStatus(object response)
    {
        var value = Get(response, ClientSymbols.StatusCodeMember);
        try
        {
            if (value is HttpStatusCode code) return (int)code;
            if (value is not null) return Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }
        catch (Exception) { }
        return null;
    }

    private static string? RemoteHeader(object response, string name)
    {
        var headers = Get(response, ClientSymbols.HeadersMember);
        if (headers is null) return null;
        try
        {
            var get = headers.GetType().GetMethod("Get", BindingFlags.Public | BindingFlags.Instance,
                binder: null, types: new[] { typeof(string) }, modifiers: null);
            return get?.Invoke(headers, new object[] { name }) as string;
        }
        catch (Exception) { return null; }
    }

    private static string RemoteEventsFailure(long sandboxId, long cursor, string reason, int? status = null)
        => Json.Obj(
            ("ok", false),
            ("sandboxId", sandboxId),
            ("cursorIn", cursor >= 0 ? cursor : null),
            ("complete", false),
            ("httpStatus", status),
            ("reason", reason));
}
