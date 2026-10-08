using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace NutstoreMcp;

public sealed record CloudEntry(
    string Path,
    bool IsCollection,
    long? Size,
    string? LastModified,
    string? Etag,
    string? ContentType);

public sealed record MultiStatusResult(
    CloudEntry? Self,
    IReadOnlyList<CloudEntry> Entries,
    int ResponseCount,
    bool Truncated);

/// <summary>
/// WebDAV <c>207 Multi-Status</c> 解析。
///
/// 用 LocalName 匹配而不是 <see cref="XNamespace"/>：不同服务端可能用 <c>D:</c>、
/// <c>d:</c> 或干脆不带前缀，按命名空间精确匹配会漏掉一些实现。
///
/// XML 读取刻意锁死：**禁止 DTD、禁用外部解析器、限制文档大小**。
/// 响应来自网络，属于不可信输入；XXE 和实体扩展炸弹都必须挡在门外。
/// </summary>
public static class MultiStatus
{
    /// <summary>坚果云 WebDAV 单次目录枚举的硬上限。</summary>
    public const int ListLimit = 750;

    private const long MaxDocumentChars = 64L * 1024 * 1024;

    private static readonly XmlReaderSettings ReaderSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersInDocument = MaxDocumentChars,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        IgnoreWhitespace = true,
    };

    public static MultiStatusResult Parse(string xml, string baseUrl, string requestPath)
    {
        XDocument doc;
        try
        {
            using var stringReader = new StringReader(xml);
            using var reader = XmlReader.Create(stringReader, ReaderSettings);
            doc = XDocument.Load(reader);
        }
        catch (XmlException ex)
        {
            throw new RemoteException($"响应不是合法的 XML：{ex.Message}");
        }

        var multistatus = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "multistatus");
        if (multistatus is null)
        {
            throw new RemoteException("响应不是合法的 WebDAV multistatus 文档");
        }

        var entries = new List<CloudEntry>();
        CloudEntry? self = null;
        var responseCount = 0;

        foreach (var response in ChildrenByLocal(multistatus, "response"))
        {
            var hrefNode = ChildrenByLocal(response, "href").FirstOrDefault();
            if (hrefNode is null)
            {
                continue;
            }

            string cloudPath;
            try
            {
                cloudPath = CloudPath.FromHref(hrefNode.Value, baseUrl);
            }
            catch (PathNotAllowedException)
            {
                continue; // href 无法解析就跳过，不因此让整个列表失败
            }

            responseCount++; // 计数含被跳过的条目 —— 截断判断看的是响应条数

            // 只接受 propstat 里状态为 2xx 的那个 prop 块。
            // 不能用「随便找个 prop」兜底 —— 那会穿进 403 的 propstat，
            // 把一个被拒绝的条目当成有效结果混进列表。
            XElement? prop = null;
            foreach (var propstat in ChildrenByLocal(response, "propstat"))
            {
                var statusLine = ChildrenByLocal(propstat, "status").FirstOrDefault()?.Value ?? string.Empty;
                if (Has2xx(statusLine))
                {
                    prop = ChildrenByLocal(propstat, "prop").FirstOrDefault();
                    break;
                }
            }

            if (prop is null)
            {
                // 极少数服务端把整体状态放在 response 层（此时无 prop 可取）
                var responseStatus = ChildrenByLocal(response, "status").FirstOrDefault()?.Value ?? string.Empty;
                if (Has2xx(responseStatus))
                {
                    var bare = new CloudEntry(cloudPath, false, null, null, null, null);
                    if (cloudPath == requestPath)
                    {
                        self = bare;
                    }
                    else
                    {
                        entries.Add(bare);
                    }
                }

                continue;
            }

            var resourcetype = ChildrenByLocal(prop, "resourcetype").FirstOrDefault();
            var isCollection = resourcetype is not null
                && resourcetype.Descendants().Any(e => e.Name.LocalName == "collection");

            var entry = new CloudEntry(
                cloudPath,
                isCollection,
                ParseLong(ChildrenByLocal(prop, "getcontentlength").FirstOrDefault()?.Value),
                NullIfEmpty(ChildrenByLocal(prop, "getlastmodified").FirstOrDefault()?.Value),
                NullIfEmpty(ChildrenByLocal(prop, "getetag").FirstOrDefault()?.Value),
                NullIfEmpty(ChildrenByLocal(prop, "getcontenttype").FirstOrDefault()?.Value));

            if (cloudPath == requestPath)
            {
                self = entry;
            }
            else
            {
                entries.Add(entry);
            }
        }

        entries.Sort(static (a, b) => string.CompareOrdinal(a.Path, b.Path));

        return new MultiStatusResult(self, entries, responseCount, responseCount >= ListLimit);
    }

    private static IEnumerable<XElement> ChildrenByLocal(XContainer parent, string localName)
        => parent.Elements().Where(e => e.Name.LocalName == localName);

    private static bool Has2xx(string statusLine)
    {
        // 形如 "HTTP/1.1 200 OK"
        var parts = statusLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2
            && int.TryParse(parts[1], System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var code)
            && code is >= 200 and < 300;
    }

    private static long? ParseLong(string? value)
        => long.TryParse(value, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : null;

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    /// <summary>生成 PROPFIND 请求体。</summary>
    public static string PropFindBody() =>
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n"
        + "<D:propfind xmlns:D=\"DAV:\">\n"
        + "  <D:prop>\n"
        + "    <D:resourcetype/>\n"
        + "    <D:getcontentlength/>\n"
        + "    <D:getlastmodified/>\n"
        + "    <D:getetag/>\n"
        + "    <D:getcontenttype/>\n"
        + "  </D:prop>\n"
        + "</D:propfind>";

    public static string QuotaBody() =>
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n"
        + "<D:propfind xmlns:D=\"DAV:\">\n"
        + "  <D:prop>\n"
        + "    <D:quota-available-bytes/>\n"
        + "    <D:quota-used-bytes/>\n"
        + "  </D:prop>\n"
        + "</D:propfind>";

    /// <summary>从配额响应里取 <c>{availableBytes, usedBytes}</c>；服务端不支持时返回 null。</summary>
    public static (long? AvailableBytes, long? UsedBytes)? ParseQuota(string xml)
    {
        try
        {
            using var stringReader = new StringReader(xml);
            using var reader = XmlReader.Create(stringReader, ReaderSettings);
            var doc = XDocument.Load(reader);

            var available = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "quota-available-bytes");
            var used = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "quota-used-bytes");
            if (available is null && used is null)
            {
                return null;
            }

            return (ParseLong(available?.Value), ParseLong(used?.Value));
        }
        catch (XmlException)
        {
            return null;
        }
    }
}
