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

        var r = await p.GetMetadata(Info("some file name", ("Isbn", Harness.BookIsbn)), default);

        Assert.True(r.HasMetadata);
        Assert.Equal(Harness.BookTitle, r.Item.Name);
        Assert.Equal(Harness.BookIsbn, r.Item.ProviderIds["Isbn"]);
        Assert.Equal(Harness.BookId, r.Item.ProviderIds["Atlas"]);
        var resolve = h.Handler.Requests.Single(x => x.Uri.AbsolutePath.EndsWith("/resolve"));
        Assert.Contains("\"isbn\"", resolve.Body);
        Assert.Contains(Harness.BookIsbn, resolve.Body);
        Assert.Equal("Bearer " + Harness.Token, resolve.Header("Authorization"));
        Assert.StartsWith("atlas-connector-jellyfin/", resolve.Header("User-Agent"));
        Assert.All(h.Handler.Requests, x => Assert.DoesNotContain(Harness.Token, x.Uri.ToString()));
    }

    [Fact]
    public async Task Title_only_never_applies_a_record_automatically()
    {
        var handler = Harness.BookHandler();
        using var h = new Harness(handler);

        var r = await new AtlasBookProvider(() => h.Runtime).GetMetadata(Info(Harness.BookTitle), default);

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

        var r = await new AtlasBookProvider(() => h.Runtime).GetMetadata(Info(Harness.BookTitle, ("Isbn", "9780000000101")), default);

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
        Assert.Equal(Harness.BookTitle, offline.Item.Name);
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
        handler.On("GET", "/api/v2/games", s => FakeHandler.Json(200, Repo.Text("contract", "fixtures", "books", "search-author.json")));
        using var h = new Harness(handler);

        var found = (await new AtlasBookProvider(() => h.Runtime).GetSearchResults(Info("quillfeather"), default)).ToList();
        var asMovies = (await new AtlasMovieProvider(() => h.Runtime).GetSearchResults(new MovieInfo { Name = "quillfeather" }, default)).ToList();

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

    // ---- ISBN: Atlas keys its `isbn` crosswalk by bare ISBN-13, and GET /games?q=<isbn> answers the book alone ----------------

    private const string NothingMatched = "{\"results\":[{\"canonical_id\":null,\"reason\":\"none\",\"tier\":\"none\",\"method\":\"none\",\"confidence\":0.0,\"candidates\":[]}]}";

    [Theory]
    [InlineData("0-00-000011-6")]
    [InlineData("0000000116")]
    [InlineData("978-0-00-000011-8")]
    [InlineData(" 978 0 00 000011 8 ")]
    public async Task An_isbn10_or_hyphenated_isbn_is_sent_as_the_bare_isbn13_atlas_keys_by(string isbn)
    {
        using var h = new Harness(Harness.BookHandler());

        var r = await new AtlasBookProvider(() => h.Runtime).GetMetadata(Info("file", ("Isbn", isbn)), default);

        Assert.True(r.HasMetadata);
        var sent = new SystemTextJsonAdapter().Parse(h.Handler.Requests.Single(x => x.Uri.AbsolutePath.EndsWith("/resolve")).Body);
        Assert.Equal(Harness.BookIsbn, sent["items"].Items[0]["provider_ids"]["isbn"].AsString());
    }

    [Fact]
    public void An_invalid_isbn_is_no_identity_and_is_never_sent()
    {
        var ids = AtlasLookup.MappedIds(new Dictionary<string, string> { { "Isbn", "9780000000119" }, { "Tmdb", "603" } });
        Assert.Equal(new[] { "tmdb" }, ids.Keys.ToArray());
    }

    [Fact]
    public async Task When_resolve_matches_nothing_the_isbn_search_names_the_book()
    {
        // contract/fixtures/books/search-isbn.json: GET /games?q=9780000000101 (the paperback) answers The Lamp Keeper alone
        var handler = Harness.BookHandler();
        handler.On("POST", "/api/v2/resolve", s => FakeHandler.Json(200, NothingMatched));
        handler.On("GET", "/api/v2/games", s => FakeHandler.Json(200, Repo.Text("contract", "fixtures", "books", "search-isbn.json")));
        using var h = new Harness(handler);

        var r = await new AtlasBookProvider(() => h.Runtime).GetMetadata(Info("file", ("Isbn", "0-00-000010-8")), default);

        Assert.True(r.HasMetadata);
        Assert.Equal(Harness.BookTitle, r.Item.Name);
        Assert.Equal(Harness.BookId, r.Item.ProviderIds["Atlas"]);
        var search = handler.Requests.Single(x => x.Method == HttpMethod.Get && x.Uri.AbsolutePath.EndsWith("/games"));
        Assert.Contains("q=9780000000101", search.Uri.Query);
        Assert.Contains("type=book", search.Uri.Query);
    }

    [Fact]
    public async Task The_isbn_search_is_not_a_match_when_it_answers_nothing_or_several_books()
    {
        foreach (var answer in new[] { "{\"results\":[]}", Repo.Text("contract", "fixtures", "books", "search-author.json") })
        {
            var handler = Harness.BookHandler();
            handler.On("POST", "/api/v2/resolve", s => FakeHandler.Json(200, NothingMatched));
            handler.On("GET", "/api/v2/games", s => FakeHandler.Json(200, answer));
            using var h = new Harness(handler);

            var r = await new AtlasBookProvider(() => h.Runtime).GetMetadata(Info("file", ("Isbn", Harness.BookIsbn)), default);

            Assert.False(r.HasMetadata);
        }
    }

    [Theory]
    [InlineData("conflict")]
    [InlineData("type_mismatch")]
    public async Task A_conflict_or_another_type_from_resolve_is_never_overridden_by_the_isbn_search(string reason)
    {
        var handler = Harness.BookHandler();
        handler.On("POST", "/api/v2/resolve", s => FakeHandler.Json(200, "{\"results\":[{\"canonical_id\":null,\"reason\":\"" + reason + "\",\"tier\":\"id\",\"method\":\"none\",\"confidence\":0.0,\"candidates\":[]}]}"));
        handler.On("GET", "/api/v2/games", s => FakeHandler.Json(200, Repo.Text("contract", "fixtures", "books", "search-isbn.json")));
        using var h = new Harness(handler);

        var r = await new AtlasBookProvider(() => h.Runtime).GetMetadata(Info("file", ("Isbn", Harness.BookIsbn)), default);

        Assert.False(r.HasMetadata);
        Assert.DoesNotContain(handler.Requests, x => x.Method == HttpMethod.Get && x.Uri.AbsolutePath.EndsWith("/games"));
    }

    [Fact]
    public async Task Identify_offers_the_book_an_isbn_names()
    {
        var handler = Harness.BookHandler();
        handler.On("POST", "/api/v2/resolve", s => FakeHandler.Json(200, NothingMatched));
        handler.On("GET", "/api/v2/games", s => FakeHandler.Json(200, s.Uri.Query.Contains("q=9780000000101")
            ? Repo.Text("contract", "fixtures", "books", "search-isbn.json")
            : "{\"results\":[]}"));
        using var h = new Harness(handler);

        var found = (await new AtlasBookProvider(() => h.Runtime).GetSearchResults(Info(string.Empty, ("Isbn", "9780000000101")), default)).ToList();

        Assert.Equal(Harness.BookId, Assert.Single(found).ProviderIds["Atlas"]);
    }

    [Fact]
    public async Task Two_candidate_ids_that_land_on_one_survivor_are_offered_once()
    {
        const string old = "atlas:01kdvdna9c00000000000old";
        var handler = Harness.BookHandler();
        handler.On("POST", "/api/v2/resolve", s => FakeHandler.Json(200, "{\"results\":[{\"canonical_id\":null,\"reason\":\"ambiguous\",\"tier\":\"weak\",\"method\":\"none\",\"confidence\":0.5,"
            + "\"candidates\":[{\"id\":\"" + old + "\",\"score\":0.6},{\"id\":\"" + Harness.BookId + "\",\"score\":0.5}]}]}"));
        handler.On("POST", "/api/v2/games/batch", s => FakeHandler.Json(200, "{\"results\":["
            + "{\"requested\":\"" + old + "\",\"status\":\"ok\",\"game\":" + Harness.Book + "},"
            + "{\"requested\":\"" + Harness.BookId + "\",\"status\":\"ok\",\"game\":" + Harness.Book + "}]}"));
        using var h = new Harness(handler);

        var r = await h.Runtime.GetLookup().FindAsync(new ItemQuery { Kind = ItemKind.Book, Name = "x", ProviderIds = new Dictionary<string, string> { { "Isbn", Harness.BookIsbn } } }, default);

        Assert.Equal(LookupOutcome.Candidates, r.Outcome);
        Assert.Equal(Harness.BookId, Assert.Single(r.Candidates).Id);
    }
}
