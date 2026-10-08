using NutstoreMcp;

namespace NutstoreMcp.Tests;

public class MultiStatusTests
{
    private const string BaseUrl = "https://dav.jianguoyun.com/dav";

    [Fact]
    public void ParsesEntriesAndDistinguishesCollections()
    {
        const string xml = """
            <?xml version="1.0"?>
            <D:multistatus xmlns:D="DAV:">
              <D:response>
                <D:href>/dav/code/</D:href>
                <D:propstat><D:prop><D:resourcetype><D:collection/></D:resourcetype></D:prop>
                  <D:status>HTTP/1.1 200 OK</D:status></D:propstat>
              </D:response>
              <D:response>
                <D:href>/dav/code/a.md</D:href>
                <D:propstat><D:prop>
                  <D:resourcetype/>
                  <D:getcontentlength>5</D:getcontentlength>
                  <D:getetag>"abc"</D:getetag>
                </D:prop><D:status>HTTP/1.1 200 OK</D:status></D:propstat>
              </D:response>
              <D:response>
                <D:href>/dav/code/sub/</D:href>
                <D:propstat><D:prop><D:resourcetype><D:collection/></D:resourcetype></D:prop>
                  <D:status>HTTP/1.1 200 OK</D:status></D:propstat>
              </D:response>
            </D:multistatus>
            """;

        var r = MultiStatus.Parse(xml, BaseUrl, "/code");

        Assert.NotNull(r.Self);
        Assert.Equal("/code", r.Self!.Path);
        Assert.True(r.Self.IsCollection);

        Assert.Equal(["/code/a.md", "/code/sub"], r.Entries.Select(e => e.Path));
        var file = r.Entries[0];
        Assert.False(file.IsCollection);
        Assert.Equal(5, file.Size);
        Assert.Equal("\"abc\"", file.Etag);
        Assert.True(r.Entries[1].IsCollection);
        Assert.False(r.Truncated);
    }

    [Fact]
    public void IgnoresNonSuccessPropstatBlocks()
    {
        // 关键回归：曾经用「随便找个 prop」兜底，把 403 的条目当成有效结果混进了列表
        const string xml = """
            <?xml version="1.0"?>
            <D:multistatus xmlns:D="DAV:">
              <D:response>
                <D:href>/dav/code/</D:href>
                <D:propstat><D:prop><D:resourcetype><D:collection/></D:resourcetype></D:prop>
                  <D:status>HTTP/1.1 200 OK</D:status></D:propstat>
              </D:response>
              <D:response>
                <D:href>/dav/code/ok.md</D:href>
                <D:propstat><D:prop><D:getcontentlength>12</D:getcontentlength></D:prop>
                  <D:status>HTTP/1.1 200 OK</D:status></D:propstat>
              </D:response>
              <D:response>
                <D:href>/dav/code/denied.md</D:href>
                <D:propstat><D:prop></D:prop>
                  <D:status>HTTP/1.1 403 Forbidden</D:status></D:propstat>
              </D:response>
            </D:multistatus>
            """;

        var r = MultiStatus.Parse(xml, BaseUrl, "/code");

        Assert.Equal(["/code/ok.md"], r.Entries.Select(e => e.Path));
        Assert.Equal(12, r.Entries[0].Size);
        // 响应条数含被拒的条目 —— 截断判断看的是响应条数
        Assert.Equal(3, r.ResponseCount);
    }

    [Fact]
    public void HandlesUnprefixedAndMixedNamespacePrefixes()
    {
        const string xml = """
            <?xml version="1.0"?>
            <multistatus xmlns="DAV:">
              <response>
                <href>/dav/code/</href>
                <propstat><prop><resourcetype><collection/></resourcetype></prop>
                  <status>HTTP/1.1 200 OK</status></propstat>
              </response>
              <response>
                <href>/dav/code/x.txt</href>
                <propstat><prop><getcontentlength>1</getcontentlength></prop>
                  <status>HTTP/1.1 200 OK</status></propstat>
              </response>
            </multistatus>
            """;

        var r = MultiStatus.Parse(xml, BaseUrl, "/code");
        Assert.Single(r.Entries);
        Assert.Equal("/code/x.txt", r.Entries[0].Path);
    }

    [Fact]
    public void DecodesPercentEncodedHrefs()
    {
        const string xml = """
            <?xml version="1.0"?>
            <D:multistatus xmlns:D="DAV:">
              <D:response>
                <D:href>/dav/%E6%88%91%E7%9A%84%E5%9D%9A%E6%9E%9C%E4%BA%91/</D:href>
                <D:propstat><D:prop><D:resourcetype><D:collection/></D:resourcetype></D:prop>
                  <D:status>HTTP/1.1 200 OK</D:status></D:propstat>
              </D:response>
              <D:response>
                <D:href>/dav/%E6%88%91%E7%9A%84%E5%9D%9A%E6%9E%9C%E4%BA%91/a%20b.md</D:href>
                <D:propstat><D:prop><D:getcontentlength>1</D:getcontentlength></D:prop>
                  <D:status>HTTP/1.1 200 OK</D:status></D:propstat>
              </D:response>
            </D:multistatus>
            """;

        var r = MultiStatus.Parse(xml, BaseUrl, "/我的坚果云");
        Assert.Equal(["/我的坚果云/a b.md"], r.Entries.Select(e => e.Path));
    }

    [Fact]
    public void TruncatedWhenResponseCountReaches750()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("<?xml version=\"1.0\"?><D:multistatus xmlns:D=\"DAV:\">");
        for (var i = 0; i < MultiStatus.ListLimit; i++)
        {
            sb.Append($"<D:response><D:href>/dav/code/f{i:D4}.txt</D:href>");
            sb.Append("<D:propstat><D:prop><D:getcontentlength>1</D:getcontentlength></D:prop>");
            sb.Append("<D:status>HTTP/1.1 200 OK</D:status></D:propstat></D:response>");
        }

        sb.Append("</D:multistatus>");

        var r = MultiStatus.Parse(sb.ToString(), BaseUrl, "/code");
        Assert.True(r.Truncated, "达到 750 条响应就必须告警");
        Assert.Equal(MultiStatus.ListLimit, r.ResponseCount);
        Assert.Equal(MultiStatus.ListLimit, r.Entries.Count);
    }

    [Fact]
    public void DoesNotReportTruncationBelowLimit()
    {
        const string xml = """
            <?xml version="1.0"?>
            <D:multistatus xmlns:D="DAV:">
              <D:response><D:href>/dav/code/</D:href>
                <D:propstat><D:prop><D:resourcetype><D:collection/></D:resourcetype></D:prop>
                <D:status>HTTP/1.1 200 OK</D:status></D:propstat></D:response>
              <D:response><D:href>/dav/code/a.txt</D:href>
                <D:propstat><D:prop><D:getcontentlength>1</D:getcontentlength></D:prop>
                <D:status>HTTP/1.1 200 OK</D:status></D:propstat></D:response>
            </D:multistatus>
            """;

        Assert.False(MultiStatus.Parse(xml, BaseUrl, "/code").Truncated);
    }

    [Fact]
    public void RejectsNonMultiStatusInput()
        => Assert.Throws<RemoteException>(() => MultiStatus.Parse("<html/>", BaseUrl, "/"));

    [Fact]
    public void RejectsMalformedXml()
        => Assert.Throws<RemoteException>(() => MultiStatus.Parse("<a><b></a>", BaseUrl, "/"));

    [Fact]
    public void ProhibitsDtdSoEntityExpansionCannotBeUsed()
    {
        // XXE / 实体扩展炸弹必须挡在解析层
        const string xxe = """
            <?xml version="1.0"?>
            <!DOCTYPE foo [<!ENTITY xxe SYSTEM "file:///C:/Windows/win.ini">]>
            <D:multistatus xmlns:D="DAV:"><D:response><D:href>&xxe;</D:href></D:response></D:multistatus>
            """;

        Assert.Throws<RemoteException>(() => MultiStatus.Parse(xxe, BaseUrl, "/"));
    }

    [Fact]
    public void ParseQuotaReturnsNullWhenUnsupported()
        => Assert.Null(MultiStatus.ParseQuota("<D:multistatus xmlns:D=\"DAV:\"/>"));

    [Fact]
    public void ParseQuotaReadsValues()
    {
        const string xml = """
            <?xml version="1.0"?>
            <D:multistatus xmlns:D="DAV:"><D:response><D:propstat><D:prop>
              <D:quota-available-bytes>1073741824</D:quota-available-bytes>
              <D:quota-used-bytes>536870912</D:quota-used-bytes>
            </D:prop></D:propstat></D:response></D:multistatus>
            """;

        var q = MultiStatus.ParseQuota(xml);
        Assert.NotNull(q);
        Assert.Equal(1073741824, q!.Value.AvailableBytes);
        Assert.Equal(536870912, q.Value.UsedBytes);
    }
}
