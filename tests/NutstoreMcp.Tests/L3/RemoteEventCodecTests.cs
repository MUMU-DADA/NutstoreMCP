using System.IO.Compression;
using System.Text;
using NutstoreMcp.Plugin.L3;

namespace NutstoreMcp.Tests;

public sealed class RemoteEventCodecTests
{
    [Fact]
    public void IdentitySkipsBlankLinesAndPreservesRecords()
    {
        using var body = new MemoryStream(Encoding.UTF8.GetBytes("\n  \r\n{\"id\":1}\n\n{\"id\":2}"));

        var page = RemoteEventCodec.ParsePage(4, "9", null, body, "identity");

        Assert.Equal(4, page.CursorIn);
        Assert.Equal(9, page.CursorOut);
        Assert.False(page.HasMore);
        Assert.Equal(["{\"id\":1}", "{\"id\":2}"], page.Events);
        Assert.Equal(2, page.EventCount);
    }

    [Fact]
    public void GzipAndEmptyMoreDataHeaderAreHandled()
    {
        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionMode.Compress, leaveOpen: true))
        using (var writer = new StreamWriter(gzip, new UTF8Encoding(false), 256, leaveOpen: true))
        {
            writer.Write("{\"id\":3}\n");
        }
        compressed.Position = 0;

        var page = RemoteEventCodec.ParsePage(3, "4", string.Empty, compressed, "gzip");

        Assert.True(page.HasMore);
        Assert.Equal(["{\"id\":3}"], page.Events);
    }

    [Fact]
    public void MissingOrInvalidLatestCursorIsRejected()
    {
        using var body = new MemoryStream();

        Assert.Throws<RemoteEventCodec.ProtocolException>(
            () => RemoteEventCodec.ParsePage(0, null, null, body));
        Assert.Throws<RemoteEventCodec.ProtocolException>(
            () => RemoteEventCodec.ParsePage(0, "not-a-number", null, body));
        Assert.Throws<RemoteEventCodec.ProtocolException>(
            () => RemoteEventCodec.ParsePage(0, "-1", null, body));
        Assert.Throws<RemoteEventCodec.ProtocolException>(
            () => RemoteEventCodec.ParsePage(0, "+1", null, body));
    }

    [Fact]
    public void MorePagesRequireStrictlyAdvancingCursor()
    {
        using var body = new MemoryStream();
        Assert.Throws<RemoteEventCodec.ProtocolException>(
            () => RemoteEventCodec.ParsePage(5, "5", "", body));
        Assert.Throws<RemoteEventCodec.ProtocolException>(
            () => RemoteEventCodec.ParsePage(5, "4", null, body));
        Assert.Throws<RemoteEventCodec.ProtocolException>(
            () => RemoteEventCodec.ParsePage(5, "4", "1", body));
    }

    [Fact]
    public void UnknownEncodingAndNegativeInputCursorAreRejected()
    {
        using var body = new MemoryStream();

        Assert.Throws<RemoteEventCodec.ProtocolException>(
            () => RemoteEventCodec.ParsePage(0, "0", null, body, "br"));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => RemoteEventCodec.ParsePage(-1, "0", null, body));
    }

    [Fact]
    public void EventAndLineBudgetsFailClosed()
    {
        using var body = new MemoryStream(Encoding.UTF8.GetBytes("{\"id\":1}\n{\"id\":2}\n"));
        var limits = new RemoteEventCodec.Limits(maxEvents: 1, maxLineBytes: 100, maxPayloadBytes: 100);

        Assert.Throws<RemoteEventCodec.LimitException>(
            () => RemoteEventCodec.ParsePage(0, "2", null, body, limits: limits));

        using var longLine = new MemoryStream(Encoding.UTF8.GetBytes("{\"long\":\"123456\"}"));
        var lineLimits = new RemoteEventCodec.Limits(maxEvents: 10, maxLineBytes: 5, maxPayloadBytes: 100);
        Assert.Throws<RemoteEventCodec.LimitException>(
            () => RemoteEventCodec.ParsePage(0, "1", null, longLine, limits: lineLimits));
    }

    [Fact]
    public void PayloadBudgetFailsBeforeReturningPartialPage()
    {
        using var body = new MemoryStream(Encoding.UTF8.GetBytes("{\"id\":1}\n"));
        var limits = new RemoteEventCodec.Limits(maxEvents: 10, maxLineBytes: 100, maxPayloadBytes: 2);

        Assert.Throws<RemoteEventCodec.LimitException>(
            () => RemoteEventCodec.ParsePage(0, "1", null, body, limits: limits));
    }

    [Fact]
    public void TotalWireAndDecodedBudgetsIncludeBlankLines()
    {
        using var tooMuchWire = new MemoryStream(Encoding.UTF8.GetBytes("12345"));
        var wireLimits = new RemoteEventCodec.Limits(
            maxEvents: 10, maxLineBytes: 100, maxPayloadBytes: 100,
            maxResponseBytes: 4, maxDecodedBytes: 100);
        Assert.Throws<RemoteEventCodec.LimitException>(
            () => RemoteEventCodec.ParsePage(0, "1", null, tooMuchWire, limits: wireLimits));

        using var tooMuchDecoded = new MemoryStream(Encoding.UTF8.GetBytes("\n\n\n"));
        var decodedLimits = new RemoteEventCodec.Limits(
            maxEvents: 10, maxLineBytes: 100, maxPayloadBytes: 100,
            maxResponseBytes: 100, maxDecodedBytes: 2);
        Assert.Throws<RemoteEventCodec.LimitException>(
            () => RemoteEventCodec.ParsePage(0, "1", null, tooMuchDecoded, limits: decodedLimits));

        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionMode.Compress, leaveOpen: true))
            gzip.Write(Encoding.UTF8.GetBytes(new string('\n', 256)));
        compressed.Position = 0;
        var expansionLimits = new RemoteEventCodec.Limits(
            maxEvents: 10, maxLineBytes: 100, maxPayloadBytes: 100,
            maxResponseBytes: 100, maxDecodedBytes: 32);
        Assert.Throws<RemoteEventCodec.LimitException>(
            () => RemoteEventCodec.ParsePage(0, "1", null, compressed, "gzip", expansionLimits));
    }

    [Fact]
    public void JsonRecordsMustBeCompleteObjectsOrArrays()
    {
        using var valid = new MemoryStream(Encoding.UTF8.GetBytes(
            " {\"text\":\"escaped \\\" quote\",\"items\":[true,null,3.5e-2]} \n"));
        var page = RemoteEventCodec.ParsePage(0, "1", null, valid);
        Assert.Single(page.Events);

        foreach (var invalid in new[]
        {
            "plain text",
            "{} {}",
            "{\"id\":1",
            "{\"id\":\"unterminated}",
            "{\"id\":tru}",
            "[1,]",
        })
        {
            using var body = new MemoryStream(Encoding.UTF8.GetBytes(invalid));
            Assert.Throws<RemoteEventCodec.ProtocolException>(
                () => RemoteEventCodec.ParsePage(0, "1", null, body));
        }
    }

    [Fact]
    public void JsonDepthAndEncodingAreBounded()
    {
        using var deep = new MemoryStream(Encoding.UTF8.GetBytes("{\"x\":" + new string('[', 66) + "0" + new string(']', 66) + "}"));
        Assert.Throws<RemoteEventCodec.LimitException>(
            () => RemoteEventCodec.ParsePage(0, "1", null, deep));

        using var invalidUtf8 = new MemoryStream([0x7B, 0x22, 0x78, 0x22, 0x3A, 0x22, 0xC3, 0x28, 0x22, 0x7D]);
        Assert.Throws<DecoderFallbackException>(
            () => RemoteEventCodec.ParsePage(0, "1", null, invalidUtf8));

        using var utf16 = new MemoryStream(Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("{}\n")).ToArray());
        Assert.Throws<DecoderFallbackException>(
            () => RemoteEventCodec.ParsePage(0, "1", null, utf16));
    }

    [Fact]
    public void DisposeFailureDoesNotPreventLaterResourcesFromBeingReleased()
    {
        var first = new DisposeProbe(throws: true);
        var second = new DisposeProbe(throws: false);

        RemoteEventCodec.DisposeQuietly(first);
        RemoteEventCodec.DisposeQuietly(second);

        Assert.True(first.WasDisposed);
        Assert.True(second.WasDisposed);
    }

    private sealed class DisposeProbe(bool throws) : IDisposable
    {
        public bool WasDisposed { get; private set; }

        public void Dispose()
        {
            WasDisposed = true;
            if (throws) throw new IOException("dispose failure");
        }
    }
}
