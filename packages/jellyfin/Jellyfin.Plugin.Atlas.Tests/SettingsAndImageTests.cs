using System.Text.Json;
using Jellyfin.Plugin.Atlas.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.Atlas.Tests;

public class SettingsTests
{
    private static SettingsService Service(Harness h, Action save = null) => new SettingsService(h.Runtime, save ?? (() => { }));

    [Fact]
    public void The_settings_view_never_contains_the_token()
    {
        using var h = new Harness(Harness.BookHandler());
        var view = Service(h).Get();
        var json = JsonSerializer.Serialize(view);

        Assert.True(view.HasToken);
        Assert.DoesNotContain(Harness.Token, json);
        Assert.DoesNotContain("\"Token\"", json);
    }

    [Fact]
    public void The_token_is_not_part_of_the_plugin_configuration_xml()
    {
        // BasePluginConfiguration is what Jellyfin serialises and what Plugins/{id}/Configuration returns
        Assert.DoesNotContain(typeof(PluginConfiguration).GetProperties(), p => p.Name.Contains("Token", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Saving_validates_normalises_and_keeps_or_replaces_or_forgets_the_token()
    {
        using var h = new Harness(Harness.BookHandler(), withToken: false);
        var saved = 0;
        var s = Service(h, () => saved++);

        Assert.NotEmpty(s.Save(new SettingsUpdate { BaseUrl = "ftp://x" }));
        Assert.NotEmpty(s.Save(new SettingsUpdate { BaseUrl = "http://atlas.lan/" }));                     // plain http, not allowed
        Assert.NotEmpty(s.Save(new SettingsUpdate { BaseUrl = "https://u:p@atlas.example.org/" }));
        Assert.NotEmpty(s.Save(new SettingsUpdate { BaseUrl = "https://atlas.example.org/?x=1" }));
        Assert.NotEmpty(s.Save(new SettingsUpdate { BaseUrl = "https://atlas.example.org", CacheTtlDays = 999 }));
        Assert.Equal(0, saved);                                                                              // nothing saved on error

        Assert.Empty(s.Save(new SettingsUpdate { BaseUrl = "https://atlas.example.org/sub", Token = "  secret-1 ", CacheTtlDays = 3, EnableMovies = false }));
        Assert.Equal("https://atlas.example.org/sub/", h.Config.BaseUrl);
        Assert.Equal(3, h.Config.CacheTtlDays);
        Assert.False(h.Config.EnableMovies);
        Assert.Equal("secret-1", h.Tokens.Read());
        Assert.Equal(1, saved);

        Assert.Empty(s.Save(new SettingsUpdate { BaseUrl = "https://atlas.example.org/sub/", Token = string.Empty }));   // empty = keep
        Assert.Equal("secret-1", h.Tokens.Read());

        Assert.Empty(s.Save(new SettingsUpdate { BaseUrl = "http://atlas.lan/", AllowPlainHttp = true, ForgetToken = true }));
        Assert.False(h.Tokens.Has);
        Assert.False(s.Get().HasToken);
    }

    [Fact]
    public void Loopback_http_is_accepted_without_the_plain_http_switch()
    {
        Assert.NotNull(SettingsRules.NormalizeBaseUrl("http://localhost:8000", false, out _));
    }

    [Fact]
    public void The_file_token_store_round_trips_and_clears()
    {
        var dir = Path.Combine(Path.GetTempPath(), "atlas-jf-token-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new FileTokenStore(Path.Combine(dir, "x.token"));
            Assert.False(store.Has);
            store.Write(" abc ");
            Assert.Equal("abc", store.Read());
            Assert.True(store.Has);
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(dir, "x.token")));
            }

            store.Write(string.Empty);
            Assert.False(store.Has);
        }
        finally
        {
            try
            {
                Directory.Delete(dir, true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public async Task Pairing_stores_the_token_server_side_and_never_returns_it()
    {
        var handler = Harness.BookHandler();
        handler.On("POST", "/api/v2/client/pair", _ => FakeHandler.Json(200, "{\"code\":\"K7QM-4XTD\",\"verify_url\":\"https://atlas.example.org/pair?code=K7QM-4XTD\",\"poll_token\":\"poll-SECRET\",\"expires_in\":600}"));
        var polls = 0;
        handler.On("POST", "/api/v2/client/pair/poll", _ => FakeHandler.Json(200, ++polls == 1 ? "{\"status\":\"pending\"}" : "{\"status\":\"approved\",\"token\":\"paired-token-1\"}"));
        using var h = new Harness(handler, withToken: false);
        var s = Service(h);

        var start = await s.StartPairingAsync(default);
        Assert.Equal("K7QM-4XTD", start.Code);
        Assert.DoesNotContain("poll-SECRET", JsonSerializer.Serialize(start));
        Assert.Equal("pending", await s.PollPairingAsync(default));
        Assert.False(h.Tokens.Has);
        var status = await s.PollPairingAsync(default);

        Assert.Equal("approved", status);
        Assert.Equal("paired-token-1", h.Tokens.Read());
        Assert.Equal("none", await s.PollPairingAsync(default));      // nothing pending any more
        Assert.All(handler.Requests.Where(r => r.Uri.AbsolutePath.Contains("/client/pair")), r => Assert.False(r.Headers.ContainsKey("Authorization")));
    }

    [Fact]
    public async Task Test_connection_reports_the_node_and_an_unusable_address()
    {
        using var h = new Harness(Harness.BookHandler());
        Assert.StartsWith("Connected to Atlas", await Service(h).TestAsync(default));

        using var none = new Harness(Harness.BookHandler(), new PluginConfiguration());
        Assert.Contains("valid Atlas address", await Service(none).TestAsync(default));
    }
}

public class ImageTests
{
    private static readonly byte[] Jpeg = { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3 };

    private static string ArtworkJson(string media, string image) =>
        "{\"id\":\"" + Harness.BookId + "\",\"results\":[" +
        "{\"id\":\"a1\",\"kind\":\"poster\",\"width\":600,\"height\":900,\"preferred\":true,\"media_url\":" + (media == null ? "null" : "\"" + media + "\"") + ",\"image_url\":" + (image == null ? "null" : "\"" + image + "\"") + "}," +
        "{\"id\":\"a2\",\"kind\":\"background\",\"image_url\":\"/api/v2/artwork/a2/image\"}," +
        "{\"id\":\"a3\",\"kind\":\"hologram\",\"image_url\":\"/api/v2/artwork/a3/image\"}]}";

    private static BaseItem BookWithAtlasId()
    {
        var b = new Book();
        b.SetProviderId("Atlas", Harness.BookId);
        return b;
    }

    [Fact]
    public async Task The_image_list_uses_public_media_urls_when_present_and_never_a_token_url()
    {
        var handler = Harness.BookHandler().On("GET", "/api/v2/games/" + Harness.BookId + "/artwork", _ => FakeHandler.Json(200, ArtworkJson("/api/v2/media/abc", null)));
        using var h = new Harness(handler);

        var images = (await new AtlasImageProvider(() => h.Runtime).GetImages(BookWithAtlasId(), default)).ToList();

        var primary = Assert.Single(images, i => i.Type == ImageType.Primary);
        Assert.Equal("https://atlas.example.org/api/v2/media/abc", primary.Url);
        Assert.Equal(600, primary.Width);
        Assert.Contains(images, i => i.Type == ImageType.Backdrop);
        Assert.DoesNotContain(images, i => i.Url.Contains("a3"));                        // an unknown kind is never guessed into a role
        Assert.All(images, i => Assert.DoesNotContain(Harness.Token, i.Url));
    }

    [Fact]
    public async Task Items_without_an_atlas_id_or_with_atlas_down_give_no_images()
    {
        using var h = new Harness(Harness.BookHandler());
        Assert.Empty(await new AtlasImageProvider(() => h.Runtime).GetImages(new Book(), default));

        var down = Harness.BookHandler();
        down.Offline = true;
        using var d = new Harness(down);
        Assert.Empty(await new AtlasImageProvider(() => d.Runtime).GetImages(BookWithAtlasId(), default));
    }

    [Fact]
    public async Task A_media_url_is_fetched_without_the_token()
    {
        var handler = Harness.BookHandler().On("GET", "/api/v2/media/abc", _ => FakeHandler.Bytes(Jpeg, "image/jpeg"));
        using var h = new Harness(handler);

        var response = await new AtlasImageProvider(() => h.Runtime).GetImageResponse("https://atlas.example.org/api/v2/media/abc", default);

        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal("image/jpeg", response.Content.Headers.ContentType.MediaType);
        Assert.Equal(Jpeg, await response.Content.ReadAsByteArrayAsync());
        Assert.False(handler.Requests.Last().Headers.ContainsKey("Authorization"));
    }

    [Fact]
    public async Task A_protected_image_sends_the_token_in_a_header_and_a_307_to_a_foreign_host_does_not_carry_it()
    {
        var handler = Harness.BookHandler()
            .On("GET", "/api/v2/artwork/a2/image", _ => FakeHandler.Redirect(307, "https://images.provider.example/c/2.jpg"))
            .OnHost("images.provider.example", _ => FakeHandler.Bytes(Jpeg, "image/jpeg"));
        using var h = new Harness(handler);

        var response = await new AtlasImageProvider(() => h.Runtime).GetImageResponse("https://atlas.example.org/api/v2/artwork/a2/image", default);

        Assert.True(response.IsSuccessStatusCode);
        var atlas = handler.Requests.Single(r => r.Uri.AbsolutePath.EndsWith("/artwork/a2/image"));
        Assert.Equal("Bearer " + Harness.Token, atlas.Header("Authorization"));
        var foreign = handler.Requests.Single(r => r.Uri.Host == "images.provider.example");
        Assert.False(foreign.Headers.ContainsKey("Authorization"), "the Atlas token must never reach a provider host");
        Assert.All(foreign.Headers.Values, v => Assert.DoesNotContain(Harness.Token, v));
        Assert.DoesNotContain(Harness.Token, foreign.Uri.ToString());
    }

    [Fact]
    public async Task A_url_on_another_host_is_refused_so_the_token_cannot_be_sent_there()
    {
        var handler = Harness.BookHandler().OnHost("evil.example", _ => FakeHandler.Bytes(Jpeg, "image/jpeg"));
        using var h = new Harness(handler);

        var response = await new AtlasImageProvider(() => h.Runtime).GetImageResponse("https://evil.example/api/v2/artwork/x/image", default);

        Assert.False(response.IsSuccessStatusCode);
        Assert.DoesNotContain(handler.Requests, r => r.Uri.Host == "evil.example");
    }

    [Fact]
    public async Task A_non_image_answer_is_a_failure_not_a_file()
    {
        var handler = Harness.BookHandler().On("GET", "/api/v2/media/html", _ => FakeHandler.Json(200, "{}"));
        using var h = new Harness(handler);
        var response = await new AtlasImageProvider(() => h.Runtime).GetImageResponse("https://atlas.example.org/api/v2/media/html", default);
        Assert.False(response.IsSuccessStatusCode);
    }
}
