using Atlas.Client;
using Jellyfin.Plugin.Atlas.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.Atlas.Tests;

/// <summary>The providers driven the way Jellyfin drives them (an info object in, a MetadataResult out), over a fake Atlas.</summary>
public class ProviderTests
{
    private static BookInfo Info(string name, params (string Key, string Value)[] ids)
    {
        var i = new BookInfo { Name = name };
        foreach (var (k, v) in ids)
        {
            i.ProviderIds[k] = v;
        }

        return i;
    }

    [Fact]
    public async Task Isbn_is_sent_as_identity_with_the_token_in_a_header_only()
    {
        using var h = new Harness(Harness.BookHandler());
        var p = new AtlasBookProvider(() => h.Runtime);

        var r = await p.GetMetadata(Info("some file name", ("Isbn", "9780000000002")), default);

        Assert.True(r.HasMetadata);
        Assert.Equal("The Synthetic Lighthouse", r.Item.Name);
        Assert.Equal(Harness.BookId, r.Item.ProviderIds["Atlas"]);
        var resolve = h.Handler.Requests.Single(x => x.Uri.AbsolutePath.EndsWith("/resolve"));
        Assert.Contains("\"isbn\"", resolve.Body);
        Assert.Contains("9780000000002", resolve.Body);
        Assert.Equal("Bearer " + Harness.Token, resolve.Header("Authorization"));
        Assert.StartsWith("atlas-connector-jellyfin/", resolve.Header("User-Agent"));
        Assert.All(h.Handler.Requests, x => Assert.DoesNotContain(Harness.Token, x.Uri.ToString()));
    }

    [Fact]
    public async Task Title_only_never_applies_a_record_automatically()
    {
        var handler = Harness.BookHandler();
        using var h = new Harness(handler);

        var r = await new AtlasBookProvider(() => h.Runtime).GetMetadata(Info("The Synthetic Lighthouse"), default);

        Assert.False(r.HasMetadata);
        Assert.DoesNotContain(handler.Requests, x => x.Uri.AbsolutePath.EndsWith("/resolve"));
    }

    [Fact]
    public async Task An_exact_title_answer_to_an_id_resolve_is_never_applied()
    {
        // Jellyfin sends the title next to the ids; when no id matches, Atlas may still answer "exact" (title evidence). That is a
        // candidate for Identify, never an automatic match (CONTRACT-C15 8.4).
        var handler = Harness.BookHandler();
        handler.On("POST", "/api/v2/resolve", s => FakeHandler.Json(200, "{\"results\":[{\"assigned\":true,\"canonical_id\":\"" + Harness.BookId + "\",\"reason\":\"exact\",\"method\":\"exact\",\"record\":" + Harness.Book + "}]}"));
        using var h = new Harness(handler);

        var r = await new AtlasBookProvider(() => h.Runtime).GetMetadata(Info("The Synthetic Lighthouse", ("Isbn", "9780000000019")), default);

        Assert.False(r.HasMetadata);
        Assert.Contains(handler.Requests, x => x.Uri.AbsolutePath.EndsWith("/resolve"));
    }

    [Fact]
    public async Task A_stored_atlas_id_skips_resolve_and_a_second_run_sends_the_etag()
    {
        var handler = Harness.BookHandler();
        using var h = new Harness(handler);
        var p = new AtlasBookProvider(() => h.Runtime);

        await p.GetMetadata(Info("x", ("Atlas", Harness.BookId)), default);
        var second = await p.GetMetadata(Info("x", ("Atlas", Harness.BookId)), default);

        Assert.True(second.HasMetadata);
        Assert.DoesNotContain(handler.Requests, x => x.Uri.AbsolutePath.EndsWith("/resolve"));
        var gets = handler.Requests.Where(x => x.Uri.AbsolutePath.EndsWith("/games/" + Harness.BookId)).ToList();
        Assert.Equal(2, gets.Count);
        Assert.Null(gets[0].Header("If-None-Match"));
        Assert.Equal("\"b1\"", gets[1].Header("If-None-Match"));
    }

    [Fact]
    public async Task A_merged_id_is_followed_and_the_new_id_replaces_the_old_one()
    {
        var handler = Harness.BookHandler();
        var merged = Harness.Book.Replace("\"redirected_from\": null", "\"redirected_from\": \"atlas:0old\"");
        handler.On("GET", "/api/v2/games/atlas:0old", s => FakeHandler.Json(200, merged));
        using var h = new Harness(handler);

        var r = await new AtlasBookProvider(() => h.Runtime).GetMetadata(Info("x", ("Atlas", "atlas:0old")), default);

        Assert.Equal(Harness.BookId, r.Item.ProviderIds["Atlas"]);
    }

    [Fact]
    public async Task An_id_of_another_media_type_is_not_applied()
    {
        var handler = Harness.BookHandler();
        using var h = new Harness(handler);

        var movie = await new AtlasMovieProvider(() => h.Runtime).GetMetadata(new MovieInfo { Name = "x", ProviderIds = new Dictionary<string, string> { { "Atlas", Harness.BookId } } }, default);

        Assert.False(movie.HasMetadata);
    }

    [Fact]
    public async Task Unset_address_disabled_type_and_outage_all_give_no_metadata_and_never_throw()
    {
        using var unset = new Harness(Harness.BookHandler(), new PluginConfiguration());
        Assert.False((await new AtlasBookProvider(() => unset.Runtime).GetMetadata(Info("x", ("Atlas", Harness.BookId)), default)).HasMetadata);

        using var off = new Harness(Harness.BookHandler(), new PluginConfiguration { BaseUrl = Harness.BaseUrl, EnableBooks = false });
        Assert.False((await new AtlasBookProvider(() => off.Runtime).GetMetadata(Info("x", ("Atlas", Harness.BookId)), default)).HasMetadata);
        Assert.Empty(off.Handler.Requests);

        var down = Harness.BookHandler();
        down.Offline = true;
        using var d = new Harness(down);
        d.Runtime.Config.CacheTtlDays = 7;
        var r = await new AtlasBookProvider(() => d.Runtime).GetMetadata(Info("x", ("Atlas", Harness.BookId)), default);
        Assert.False(r.HasMetadata);
        Assert.Contains(d.Log, l => l.Contains("unreachable"));
    }

    [Fact]
    public async Task A_cached_record_is_served_while_atlas_is_down()
    {
        var handler = Harness.BookHandler();
        using var h = new Harness(handler);
        var p = new AtlasBookProvider(() => h.Runtime);
        Assert.True((await p.GetMetadata(Info("x", ("Atlas", Harness.BookId)), default)).HasMetadata);

        handler.Offline = true;
        var offline = await p.GetMetadata(Info("x", ("Atlas", Harness.BookId)), default);

        Assert.True(offline.HasMetadata);
        Assert.Equal("The Synthetic Lighthouse", offline.Item.Name);
    }

    [Fact]
    public async Task Rate_limit_without_cache_is_an_outage_not_a_crash()
    {
        var handler = Harness.BookHandler();
        handler.On("GET", "/api/v2/games/" + Harness.BookId, s => FakeHandler.Json(429, "{}", "Retry-After", "3600"));
        using var h = new Harness(handler);
        Assert.False((await new AtlasBookProvider(() => h.Runtime).GetMetadata(Info("x", ("Atlas", Harness.BookId)), default)).HasMetadata);
    }

    [Fact]
    public async Task Search_returns_only_the_right_media_type_with_atlas_ids()
    {
        var handler = Harness.BookHandler();
        handler.On("GET", "/api/v2/games", s => FakeHandler.Json(200, Repo.Text("contract", "fixtures", "books", "search-lighthouse.json")));
        using var h = new Harness(handler);

        var found = (await new AtlasBookProvider(() => h.Runtime).GetSearchResults(Info("lighthouse"), default)).ToList();
        var asMovies = (await new AtlasMovieProvider(() => h.Runtime).GetSearchResults(new MovieInfo { Name = "lighthouse" }, default)).ToList();

        Assert.NotEmpty(found);
        Assert.All(found, f => Assert.StartsWith("atlas:", f.ProviderIds["Atlas"]));
        Assert.Empty(asMovies);
        var search = handler.Requests.First(x => x.Uri.AbsolutePath.EndsWith("/games") && x.Method == HttpMethod.Get);
        Assert.Contains("type=book", search.Uri.Query);
    }

    [Fact]
    public void Provider_id_keys_map_to_atlas_namespaces_and_unknown_keys_are_not_sent()
    {
        var ids = AtlasLookup.MappedIds(new Dictionary<string, string>
        {
            { "Isbn", "9780000000002" }, { "Tmdb", "603" }, { "Imdb", "tt0000001" }, { "Tvdb", "1" }, { "AniList", "9" }, { "MusicBrainzAlbum", "x" }, { "Tmdb2", " " },
        });
        Assert.Equal(new[] { "anilist", "imdb", "isbn", "tmdb", "tvdb" }, ids.Keys.OrderBy(k => k).ToArray());
    }
}
