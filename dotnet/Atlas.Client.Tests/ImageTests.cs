using Atlas.Client;
using Xunit;

namespace Atlas.Client.Tests;

/// <summary>CLIENT-CONTRACT section 6: the token never reaches a provider host.</summary>
public class ImageTests
{
    private static readonly byte[] Jpeg = { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3 };

    private static Artwork Art(string mediaUrl = null, string imageUrl = "/api/v2/artwork/7/image") =>
        new Artwork { Id = "7", Kind = "cover", MediaUrl = mediaUrl, ImageUrl = imageUrl };

    [Fact]
    public async Task Media_url_is_preferred_and_needs_no_token()
    {
        using var rig = new Rig();
        rig.Handler.On("GET", "/api/v2/media/abc", _ => Reply.Bytes(200, Jpeg, "image/jpeg"));
        var img = await rig.Client.DownloadArtworkAsync(Art(mediaUrl: "/api/v2/media/abc"));
        Assert.Equal(Jpeg, img.Bytes);
        Assert.Equal("image/jpeg", img.ContentType);
        var req = rig.Api.Single();
        Assert.False(req.HasHeader("Authorization"));
        Assert.Equal("/api/v2/media/abc", req.Uri.AbsolutePath);
    }

    [Fact]
    public async Task Image_url_uses_the_token_in_a_header()
    {
        using var rig = new Rig();
        rig.Handler.On("GET", "/api/v2/artwork/7/image", _ => Reply.Bytes(200, Jpeg, "image/png"));
        await rig.Client.DownloadArtworkAsync(Art());
        Assert.Equal("Bearer " + Rig.Token, rig.Api.Single().Header("Authorization"));
        Assert.DoesNotContain("SECRET", rig.Api.Single().Uri.ToString());
    }

    [Fact]
    public async Task An_artwork_with_only_an_id_gets_the_standard_image_path()
    {
        using var rig = new Rig();
        rig.Handler.On("GET", "/api/v2/artwork/7/image", _ => Reply.Bytes(200, Jpeg, "image/webp"));
        await rig.Client.DownloadArtworkAsync(new Artwork { Id = "7" });
    }

    [Fact]
    public async Task A_307_to_a_foreign_host_is_followed_once_without_authorization()
    {
        using var rig = new Rig();
        rig.Handler.On("GET", "/api/v2/artwork/7/image", _ => Reply.Raw(307, null, headers: ("Location", "https://images.provider.example/c/7.jpg")));
        rig.Handler.OnHost("images.provider.example", _ => Reply.Bytes(200, Jpeg, "image/jpeg", wire: null));

        var img = await rig.Client.DownloadArtworkAsync(Art());

        Assert.Equal(Jpeg, img.Bytes);
        var foreign = Assert.Single(rig.Handler.Requests, r => r.Uri.Host == "images.provider.example");
        Assert.False(foreign.HasHeader("Authorization"), "the Atlas token must never reach a provider host");
        Assert.False(foreign.HasHeader("Atlas-Wire"));
        Assert.DoesNotContain("SECRET", foreign.Uri.ToString());
        foreach (var r in rig.Handler.Requests.Where(r => r.Uri.Host == "images.provider.example"))
            foreach (var h in r.Headers.Values) Assert.DoesNotContain(Rig.Token, h);
    }

    [Fact]
    public async Task A_308_is_followed_too_and_a_relative_location_stays_on_the_node_without_the_token()
    {
        using var rig = new Rig();
        rig.Handler.On("GET", "/api/v2/artwork/7/image", _ => Reply.Raw(308, null, headers: ("Location", "/api/v2/media/xyz")));
        rig.Handler.On("GET", "/api/v2/media/xyz", _ => Reply.Bytes(200, Jpeg, "image/jpeg", wire: null));
        await rig.Client.DownloadArtworkAsync(Art());
        Assert.False(rig.Api.Last().HasHeader("Authorization"));
    }

    [Fact]
    public async Task A_redirect_to_plain_http_is_refused()
    {
        using var rig = new Rig();
        rig.Handler.On("GET", "/api/v2/artwork/7/image", _ => Reply.Raw(307, null, headers: ("Location", "http://images.provider.example/c.jpg")));
        await Assert.ThrowsAsync<AtlasProtocolException>(() => rig.Client.DownloadArtworkAsync(Art()));
        Assert.DoesNotContain(rig.Handler.Requests, r => r.Uri.Host == "images.provider.example");
    }

    [Fact]
    public async Task Only_one_redirect_hop_is_allowed()
    {
        using var rig = new Rig();
        rig.Handler.On("GET", "/api/v2/artwork/7/image", _ => Reply.Raw(307, null, headers: ("Location", "https://a.example/1")));
        rig.Handler.OnHost("a.example", _ => Reply.Raw(307, null, headers: ("Location", "https://b.example/2")));
        await Assert.ThrowsAsync<AtlasProtocolException>(() => rig.Client.DownloadArtworkAsync(Art()));
        Assert.DoesNotContain(rig.Handler.Requests, r => r.Uri.Host == "b.example");
    }

    [Theory]
    [InlineData(301)]
    [InlineData(302)]
    [InlineData(303)]
    public async Task Only_307_and_308_are_followed(int status)
    {
        using var rig = new Rig();
        rig.Handler.On("GET", "/api/v2/artwork/7/image", _ => Reply.Raw(status, null, headers: ("Location", "https://a.example/1")));
        await Assert.ThrowsAsync<AtlasProtocolException>(() => rig.Client.DownloadArtworkAsync(Art()));
        Assert.DoesNotContain(rig.Handler.Requests, r => r.Uri.Host == "a.example");
    }

    [Fact]
    public async Task A_redirect_without_a_location_is_refused()
    {
        using var rig = new Rig();
        rig.Handler.On("GET", "/api/v2/artwork/7/image", _ => Reply.Raw(307, null));
        await Assert.ThrowsAsync<AtlasProtocolException>(() => rig.Client.DownloadArtworkAsync(Art()));
    }

    [Fact]
    public async Task A_foreign_answer_must_be_an_image()
    {
        using var rig = new Rig();
        rig.Handler.On("GET", "/api/v2/artwork/7/image", _ => Reply.Raw(307, null, headers: ("Location", "https://a.example/1")));
        rig.Handler.OnHost("a.example", _ => Reply.Raw(200, "<html>login</html>", "text/html", wire: null));
        await Assert.ThrowsAsync<AtlasProtocolException>(() => rig.Client.DownloadArtworkAsync(Art()));
    }

    [Fact]
    public async Task The_size_cap_is_enforced_on_declared_and_streamed_length()
    {
        using var rig = new Rig(configure: o => o.MaxImageBytes = 10);
        rig.Handler.On("GET", "/api/v2/artwork/7/image", _ => Reply.Bytes(200, new byte[11], "image/png"));
        await Assert.ThrowsAsync<AtlasProtocolException>(() => rig.Client.DownloadArtworkAsync(Art()));

        // no Content-Length: chunked body is cut at the cap
        rig.Handler.On("GET", "/api/v2/artwork/8/image", _ =>
        {
            var r = new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StreamContent(new NoLengthStream(new byte[50])) };
            r.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
            r.Headers.TryAddWithoutValidation("Atlas-Wire", Reply.Wire);
            return r;
        });
        await Assert.ThrowsAsync<AtlasProtocolException>(() => rig.Client.DownloadArtworkAsync(new Artwork { Id = "8" }));
    }

    [Fact]
    public async Task A_non_image_answer_from_atlas_is_refused()
    {
        using var rig = new Rig();
        rig.Handler.On("GET", "/api/v2/artwork/7/image", _ => Reply.Json("{}"));
        await Assert.ThrowsAsync<AtlasProtocolException>(() => rig.Client.DownloadArtworkAsync(Art()));
    }

    [Theory]
    [InlineData("https://evil.example/media/abc")]
    [InlineData("//evil.example/media/abc")]
    [InlineData("media/abc")]
    [InlineData("http://atlas.test/media/abc")]    // same host, different scheme: a different origin
    [InlineData("file:///etc/passwd")]
    [InlineData("/\\evil.example/media/abc")]
    public async Task Artwork_urls_that_are_not_on_the_node_are_not_fetched(string url)
    {
        using var rig = new Rig();
        await Assert.ThrowsAsync<AtlasProtocolException>(() => rig.Client.DownloadArtworkAsync(new Artwork { Id = "7", MediaUrl = url }));
        await Assert.ThrowsAsync<AtlasProtocolException>(() => rig.Client.DownloadArtworkAsync(new Artwork { Id = "7", ImageUrl = url }));
        Assert.DoesNotContain(rig.Handler.Requests, r => r.Uri.Host == "evil.example");
        Assert.Empty(rig.Api);
    }

    [Fact]
    public async Task An_absolute_url_on_the_nodes_own_origin_is_fine()
    {
        using var rig = new Rig();
        rig.Handler.On("GET", "/media/abc", _ => Reply.Bytes(200, Jpeg, "image/jpeg"));
        await rig.Client.DownloadArtworkAsync(new Artwork { MediaUrl = "https://atlas.test/media/abc" });
    }

    [Fact]
    public async Task A_server_error_on_the_foreign_hop_never_loops_back_with_the_token()
    {
        using var rig = new Rig();
        rig.Handler.On("GET", "/api/v2/artwork/7/image", _ => Reply.Raw(307, null, headers: ("Location", "https://a.example/1")));
        rig.Handler.OnHost("a.example", _ => Reply.Raw(503, "x", "text/plain", wire: null));
        await Assert.ThrowsAsync<AtlasApiException>(() => rig.Client.DownloadArtworkAsync(Art()));
        Assert.All(rig.Handler.Requests.Where(r => r.Uri.Host == "a.example"), r => Assert.False(r.HasHeader("Authorization")));
    }

    private sealed class NoLengthStream : MemoryStream
    {
        public NoLengthStream(byte[] b) : base(b) { }
        public override bool CanSeek => false;   // StreamContent then sends no Content-Length
    }
}
