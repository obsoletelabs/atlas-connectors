using Atlas.Client;
using Xunit;

namespace Atlas.Client.Tests;

/// <summary>Version negotiation, client side (C16 sections 3.2, 6 and 10.1).</summary>
public class WireTests
{
    [Theory]
    [InlineData("min=1, max=3")]
    [InlineData("use=3, min=1, max=4")]
    [InlineData("min=1,max=1")]
    [InlineData("  min=1 ,  max=2  ")]
    [InlineData("min=1, max=1, future_key=9")]
    public void Valid_headers_parse(string value) => Assert.NotNull(WireHeader.TryParse(value));

    [Theory]
    [InlineData("")]
    [InlineData("min=1, min=2")]               // duplicate key
    [InlineData("min=a")]
    [InlineData("MIN=1")]
    [InlineData("min=12345")]                  // five digits
    [InlineData("min=")]
    [InlineData("=1")]
    [InlineData("min=1, max=1,")]
    [InlineData("min=-1")]
    [InlineData("a=1,b=1,c=1,d=1,e=1,f=1,g=1,h=1,i=1")]   // nine members
    [InlineData("averyveryverylongkey=1")]
    public void Malformed_headers_do_not_parse(string value) => Assert.Null(WireHeader.TryParse(value));

    [Fact]
    public void We_send_our_range()
    {
        Assert.Equal("min=1, max=1", WireHeader.Format(new WireRange(1, 1)));
        Assert.Equal("min=2, max=5", WireHeader.Format(new WireRange(2, 5)));
    }

    [Fact]
    public void Response_reading_follows_the_rules()
    {
        var mine = new WireRange(1, 3);
        var ok = WireHeader.ReadResponse("use=3, min=1, max=4", mine);
        Assert.Equal(WireMode.Negotiated, ok.Mode);
        Assert.Equal(3, ok.Use);
        Assert.Equal(new WireRange(1, 4), ok.Server);

        var v0 = WireHeader.ReadResponse(null, mine);                          // no header: a v0 Atlas
        Assert.True(v0.IsDegraded);
        Assert.Null(v0.Use);

        Assert.Throws<AtlasProtocolException>(() => WireHeader.ReadResponse("use=4, min=1, max=4", mine));   // outside our own range
        Assert.Throws<AtlasProtocolException>(() => WireHeader.ReadResponse("use=0, min=0, max=1", mine));    // min < 1
        Assert.Throws<AtlasProtocolException>(() => WireHeader.ReadResponse("use=1, min=2, max=3", mine));    // use below the server's min
        Assert.Throws<AtlasProtocolException>(() => WireHeader.ReadResponse("min=1, max=3", mine));           // no use=
        Assert.Throws<AtlasProtocolException>(() => WireHeader.ReadResponse("use=1, min=3, max=1", mine));    // min > max
        Assert.Throws<AtlasProtocolException>(() => WireHeader.ReadResponse("garbage", mine));
    }

    [Fact]
    public async Task A_negotiated_answer_is_recorded()
    {
        using var rig = new Rig(configure: o => o.Wire = new WireRange(1, 3));
        rig.Handler.On("GET", "/api/v2/games", _ => Reply.Raw(200, "{\"results\":[]}", wire: "use=3, min=1, max=4"));
        Assert.Null(rig.Client.LastWire);
        await rig.Client.SearchAsync("a");
        Assert.Equal(WireMode.Negotiated, rig.Client.LastWire.Mode);
        Assert.Equal(3, rig.Client.LastWire.Use);
        Assert.Equal("min=1, max=3", rig.Api.Single().Header("Atlas-Wire"));
    }

    [Fact]
    public async Task No_header_in_the_answer_means_a_v0_atlas_and_a_degraded_notice_but_it_still_works()
    {
        using var rig = new Rig();
        rig.Handler.On("GET", "/api/v2/games", _ => Reply.Raw(200, "{\"results\":[{\"id\":\"a\",\"title\":\"T\"}]}", wire: null));
        var page = await rig.Client.SearchAsync("a");
        Assert.Single(page.Results);
        Assert.True(rig.Client.LastWire.IsDegraded);
    }

    [Fact]
    public async Task A_use_outside_our_range_is_a_protocol_error_never_guessed_at()
    {
        using var rig = new Rig();
        rig.Handler.On("GET", "/api/v2/games", _ => Reply.Raw(200, "{\"results\":[]}", wire: "use=5, min=2, max=6"));
        var e = await Assert.ThrowsAsync<AtlasProtocolException>(() => rig.Client.SearchAsync("a"));
        Assert.Contains("wire", e.Message);
    }

    [Fact]
    public async Task Conflict_wire_unsupported_carries_detail_verbatim_and_both_ranges()
    {
        using var rig = new Rig();
        const string detail = "This Atlas node (atlas.example.org, Atlas 2.3.0) speaks wire 2-4. The caller speaks wire 1. Upgrade the caller to an Atlas release that speaks wire 2 or newer; its data and sync cursor are kept.";
        rig.Handler.On("GET", "/api/v2/games", _ => Reply.Status(409, new
        {
            code = "wire_unsupported", reason = "peer_too_old", detail,
            server = new { wire = new { min = 2, max = 4 }, release = "2.3.0", node = "x" },
            client = new { wire = new { min = 1, max = 1 } },
            action = "upgrade_client",
        }, ("Atlas-Wire", "min=2, max=4")));

        var e = await Assert.ThrowsAsync<AtlasWireUnsupportedException>(() => rig.Client.SearchAsync("a"));
        Assert.Equal(detail, e.Detail);
        Assert.Equal(detail, e.Message);
        Assert.Equal("peer_too_old", e.Reason);
        Assert.Equal("upgrade_client", e.Action);
        Assert.Equal(new WireRange(2, 4), e.Server);
        Assert.Equal(new WireRange(1, 1), e.Client);
        Assert.Equal(409, e.Status);
        Assert.Empty(rig.Delays);                                                // not retried
        Assert.Single(rig.Api);
    }

    [Fact]
    public async Task A_409_for_another_reason_is_a_plain_api_error()
    {
        using var rig = new Rig();
        rig.Handler.On("GET", "/api/v2/games", _ => Reply.Status(409, new { error = new { code = "conflict", message = "x" } }));
        var e = await Assert.ThrowsAsync<AtlasApiException>(() => rig.Client.SearchAsync("a"));
        Assert.IsNotType<AtlasWireUnsupportedException>(e);
        Assert.Equal("conflict", e.Code);
    }

    [Fact]
    public async Task Invalid_wire_400_is_reported_with_its_code()
    {
        using var rig = new Rig();
        rig.Handler.On("GET", "/api/v2/games", _ => Reply.Status(400, new { error = new { code = "invalid_wire", message = "bad header" } }));
        var e = await Assert.ThrowsAsync<AtlasApiException>(() => rig.Client.SearchAsync("a"));
        Assert.Equal("invalid_wire", e.Code);
    }
}
