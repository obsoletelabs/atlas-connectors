using Atlas.Client;
using Xunit;

namespace Atlas.Client.Tests;

/// <summary>The tolerant reader against contract/fixtures, through both serializer adapters.</summary>
public class ReaderTests
{
    public static IEnumerable<object[]> AdapterAndFixture() =>
        from a in new[] { "stj", "newtonsoft" } from f in Fixtures.AllJson() select new object[] { a, f };

    [Theory, MemberData(nameof(AdapterAndFixture))]
    public void Every_fixture_parses_and_round_trips(string adapter, string file)
    {
        var json = Adapters.Make(adapter);
        var tree = json.Parse(Fixtures.Text(file));
        Assert.Equal(JsonKind.Object, tree.Kind);
        Assert.True(Adapters.Same(tree, json.Parse(json.Write(tree))), "write then parse changed the tree: " + file);
    }

    [Fact]
    public void Both_adapters_build_the_same_tree_for_every_fixture()
    {
        var stj = Adapters.Make("stj");
        var nj = Adapters.Make("newtonsoft");
        foreach (var file in Fixtures.AllJson())
        {
            var text = Fixtures.Text(file);
            Assert.True(Adapters.Same(stj.Parse(text), nj.Parse(text)), file);
            Assert.True(Adapters.Same(stj.Parse(text), nj.Parse(stj.Write(stj.Parse(text)))), file);   // cross read of written text
        }
    }

    [Theory, MemberData(nameof(Adapters.Both), MemberType = typeof(Adapters))]
    public void Invalid_json_is_a_protocol_error_not_a_serializer_exception(string adapter)
    {
        var json = Adapters.Make(adapter);
        foreach (var bad in new[] { "", "{", "{\"a\":}", "[1,2", "nope", "{} extra" })
            Assert.Throws<AtlasProtocolException>(() => json.Parse(bad));
    }

    [Theory, MemberData(nameof(Adapters.Both), MemberType = typeof(Adapters))]
    public void Numbers_and_escapes_survive(string adapter)
    {
        var json = Adapters.Make(adapter);
        var n = json.Parse("{\"big\":9007199254740993,\"f\":1.5,\"s\":\"a\\u00e9\\n\\\"q\\\"\",\"d\":\"2011-04-19T00:00:00Z\"}");
        Assert.Equal(9007199254740993L, n["big"].AsLong());
        Assert.Equal(1.5, n["f"].AsDouble());
        Assert.Equal("aé\n\"q\"", n["s"].AsString());
        Assert.Equal("2011-04-19T00:00:00Z", n["d"].AsString());           // a date stays text
    }

    private const string LanternfallId = "atlas:01kdvdna5a000000000000000h";
    private const string LampKeeperId = "atlas:01kdvdna9c000000000000000y";

    [Theory, MemberData(nameof(Adapters.Both), MemberType = typeof(Adapters))]
    public void Recorded_game_record(string adapter)
    {
        var rec = AtlasReader.ReadRecord(Adapters.Make(adapter).Parse(Fixtures.Text("fixtures/games/record-lanternfall.json")));
        Assert.Equal(LanternfallId, rec.Id);
        Assert.Equal(Fixtures.SeedId("lanternfall"), rec.Id);
        Assert.Equal(MediaTypeKind.Game, rec.MediaTypeKind);
        Assert.Equal("Glowmoth Studio", rec.Developer);
        Assert.Equal("Harbour Lights Interactive", rec.Publisher);
        Assert.Equal(new PartialDate(2019, 3, 14), rec.FirstRelease);
        Assert.Equal(new[] { "Puzzle", "Adventure" }, rec.Subjects);              // a game's genres are its topic words
        Assert.Equal(5, rec.Version);
        Assert.Null(rec.RedirectedFrom);
        Assert.Null(rec.Series);
        Assert.Null(rec.Isbn);
    }

    [Theory, MemberData(nameof(Adapters.Both), MemberType = typeof(Adapters))]
    public void Recorded_merge_redirect_and_records_alias(string adapter)
    {
        var j = Adapters.Make(adapter);
        var redirected = AtlasReader.ReadRecord(j.Parse(Fixtures.Text("fixtures/games/record-redirected.json")));
        Assert.Equal(LanternfallId, redirected.Id);                                          // store the survivor
        Assert.Equal(Fixtures.SeedId("lanternfall-duplicate"), redirected.RedirectedFrom);
        var alias = AtlasReader.ReadRecord(j.Parse(Fixtures.Text("fixtures/games/records-alias-lanternfall.json")));
        Assert.True(Adapters.Same(alias.Raw, j.Parse(Fixtures.Text("fixtures/games/record-lanternfall.json"))));   // /records == /games
    }

    [Theory, MemberData(nameof(Adapters.Both), MemberType = typeof(Adapters))]
    public void Recorded_book_record(string adapter)
    {
        var rec = AtlasReader.ReadRecord(Adapters.Make(adapter).Parse(Fixtures.Text("fixtures/books/record-the-lamp-keeper.json")));
        Assert.Equal(LampKeeperId, rec.Id);
        Assert.Equal(MediaTypeKind.Book, rec.MediaTypeKind);
        Assert.Equal(new[] { "Ada Quillfeather", "Tomas Vell" }, rec.Authors.Select(a => a.Name));
        Assert.Equal(new[] { "author", "translator" }, rec.Authors.Select(a => a.Role));
        Assert.Equal(new[] { true, false }, rec.Authors.Select(a => a.IsAuthor));
        Assert.Equal(2, rec.Editions.Count);
        Assert.Equal("hardcover", rec.Editions[0].Format);
        Assert.Equal("9780000000118", rec.Editions[0].Isbn13);
        Assert.Equal("0000000116", rec.Editions[0].Isbn10);
        Assert.Equal("Harbour Press", rec.Editions[0].Publisher);
        Assert.Equal(new PartialDate(2006, 2, null), rec.Editions[1].Published);
        Assert.Equal("9780000000118", rec.Isbn);                                             // no provider isbn: first edition
        Assert.Equal("The Lamp Cycle", rec.Series.Name);                                     // from relations.members_of
        Assert.Equal("1", rec.Series.Position);
        Assert.Equal(Fixtures.SeedId("series-lamp-cycle"), rec.Series.Id);
        Assert.Equal(new PartialDate(2005, 8, 14), rec.FirstRelease);
        Assert.Equal(new[] { "Lighthouses", "Fiction" }, rec.Subjects);                     // Open Library subjects live in features
        Assert.Equal("fixturebooks", Assert.Single(rec.Sources));
    }

    [Theory, MemberData(nameof(Adapters.Both), MemberType = typeof(Adapters))]
    public void Every_recorded_creator_role_and_edition_format_is_read_never_refused(string adapter)
    {
        var j = Adapters.Make(adapter);
        foreach (var file in Fixtures.AllJson().Where(f => f.StartsWith("fixtures/books/record-", StringComparison.Ordinal)))
        {
            var rec = AtlasReader.ReadRecord(j.Parse(Fixtures.Text(file)));
            Assert.All(rec.Authors, a => Assert.False(string.IsNullOrEmpty(a.Name)));
            Assert.All(rec.Editions, e => Assert.False(string.IsNullOrEmpty(e.Format)));
        }
        var salt = AtlasReader.ReadRecord(j.Parse(Fixtures.Text("fixtures/books/record-salt-and-signal.json")));
        Assert.Equal(new[] { "author", "narrator" }, salt.Authors.Select(a => a.Role));
        Assert.Equal("ebook", Assert.Single(salt.Editions).Format);
        Assert.Equal(new PartialDate(2008, null, null), salt.FirstRelease);
        var series = AtlasReader.ReadRecord(j.Parse(Fixtures.Text("fixtures/books/record-series-lamp-cycle.json")));
        Assert.Equal(MediaTypeKind.Series, series.MediaTypeKind);
    }

    [Theory, MemberData(nameof(Adapters.Both), MemberType = typeof(Adapters))]
    public void Unknown_members_and_unknown_vocabulary_are_ignored_and_kept(string adapter)
    {
        var rec = AtlasReader.ReadRecord(Adapters.Make(adapter).Parse(Fixtures.Text("fixtures/forward-compat/record-unknown-members.json")));
        Assert.Equal("The Lamp Keeper", rec.Title);
        Assert.Equal(new PartialDate(2005, 8, 14), rec.FirstRelease);
        Assert.Equal("x_future_format", rec.Editions[0].Format);                          // unknown vocabulary value: kept as text
        Assert.Equal(JsonKind.Object, rec.Raw["x_future_member"].Kind);                    // unknown member: ignored, still reachable
        Assert.Equal("9780000000118", rec.Isbn);
    }

    [Theory, MemberData(nameof(Adapters.Both), MemberType = typeof(Adapters))]
    public void A_recorded_media_type_no_connector_knows_reads_as_unknown(string adapter)
    {
        var rec = AtlasReader.ReadRecord(Adapters.Make(adapter).Parse(Fixtures.Text("fixtures/games/record-unknown-type.json")));
        Assert.Equal(MediaTypeKind.Unknown, rec.MediaTypeKind);
        Assert.Equal("comic", rec.MediaType);
        Assert.Equal("Kelp Street Press", rec.Publisher);
        Assert.Null(rec.FirstRelease);
    }

    [Theory, MemberData(nameof(Adapters.Both), MemberType = typeof(Adapters))]
    public void Unknown_media_type_reads_as_unknown_and_keeps_the_text(string adapter)
    {
        var rec = AtlasReader.ReadRecord(Adapters.Make(adapter).Parse("{\"id\":\"atlas:x\",\"media_type\":\"podcast\",\"title\":\"T\"}"));
        Assert.Equal(MediaTypeKind.Unknown, rec.MediaTypeKind);
        Assert.Equal("podcast", rec.MediaType);
    }

    [Theory, MemberData(nameof(Adapters.Both), MemberType = typeof(Adapters))]
    public void Wrong_types_read_as_absent_never_throw(string adapter)
    {
        var json = "{\"id\":5,\"title\":[1],\"developer\":{\"x\":1},\"provider_ids\":{\"a\":{\"b\":1},\"n\":7,\"s\":\"v\"},"
                 + "\"authors\":[1,null,{\"name\":3},{\"name\":\"ok\"}],\"editions\":\"nope\",\"series\":[],"
                 + "\"first_release\":{\"date\":20110419,\"precision\":{}},\"release_year\":\"2011\",\"sources\":[1,\"a\"]}";
        var rec = AtlasReader.ReadRecord(Adapters.Make(adapter).Parse(json));
        Assert.Null(rec.Id);
        Assert.Null(rec.Title);
        Assert.Null(rec.Developer);
        Assert.Null(rec.FirstRelease);
        Assert.Equal(new[] { "ok" }, rec.Authors.Select(a => a.Name));
        Assert.Equal("7", rec.ProviderIds["n"]);
        Assert.Equal("v", rec.ProviderIds["s"]);
        Assert.False(rec.ProviderIds.ContainsKey("a"));
        Assert.Empty(rec.Editions);
        Assert.Equal(new[] { "a" }, rec.Sources);
        Assert.Null(AtlasReader.ReadRecord(JsonNode.Str("x")));
    }

    [Theory, MemberData(nameof(Adapters.Both), MemberType = typeof(Adapters))]
    public void Search_pages_read_results_or_items(string adapter)
    {
        var j = Adapters.Make(adapter);
        var games = AtlasReader.ReadSearch(j.Parse(Fixtures.Text("fixtures/games/search-lanternfall.json")));
        Assert.NotEmpty(games.Results);
        Assert.All(games.Results, r => Assert.StartsWith("atlas:", r.Id));
        Assert.Contains(games.Results, r => r.Id == LanternfallId);
        var byAuthor = AtlasReader.ReadSearch(j.Parse(Fixtures.Text("fixtures/books/search-author.json")));
        Assert.All(byAuthor.Results, r => Assert.Equal(MediaTypeKind.Book, r.MediaTypeKind));
        var byIsbn = AtlasReader.ReadSearch(j.Parse(Fixtures.Text("fixtures/books/search-isbn.json")));
        Assert.Equal(LampKeeperId, Assert.Single(byIsbn.Results).Id);                     // an ISBN answers its book, alone
        var drafted = AtlasReader.ReadSearch(j.Parse("{\"items\":[{\"id\":\"a\"}],\"total\":1,\"limit\":20}"));
        Assert.Equal(1, drafted.Total);
        Assert.Equal(20, drafted.Limit);
        Assert.Equal("a", Assert.Single(drafted.Results).Id);
    }

    [Theory, MemberData(nameof(Adapters.Both), MemberType = typeof(Adapters))]
    public void Site_with_and_without_client_api(string adapter)
    {
        var j = Adapters.Make(adapter);
        var site = AtlasReader.ReadSite(j.Parse(Fixtures.Text("fixtures/games/site.json")));
        Assert.Equal(1, site.Contract);
        Assert.Equal("1.0", site.ClientApi.Version);
        Assert.Equal(1, site.ClientApi.Major);
        Assert.Equal("1.0", site.ClientApi.MinSupported);
        Assert.Equal(new[] { "books", "pairing", "etag", "batch" }, site.ClientApi.Capabilities);
        Assert.Contains("comic", site.MediaTypes);
        var legacy = AtlasReader.ReadSite(j.Parse(Rig.LegacySite));
        Assert.Null(legacy.ClientApi);
        Assert.Contains("game", legacy.MediaTypes);
        var later = AtlasReader.ReadSite(j.Parse(Rig.SiteWith("{\"version\":\"1.3\",\"min_supported\":\"1.0\",\"capabilities\":[\"books\",\"batch\",\"public_read\"],\"deprecations\":[]}")));
        Assert.Equal(1, later.ClientApi.Major);
        Assert.Equal(new[] { "books", "batch", "public_read" }, later.ClientApi.Capabilities);
    }

    [Theory, MemberData(nameof(Adapters.Both), MemberType = typeof(Adapters))]
    public void Recorded_resolve_answer_and_the_auto_assign_rule(string adapter)
    {
        var results = AtlasReader.ReadResolve(Adapters.Make(adapter).Parse(Fixtures.Body("fixtures/games/resolve-by-ids.json")));
        Assert.Equal(5, results.Count);                                                    // one per item, in input order
        Assert.Equal("crosswalk", results[0].Reason);
        Assert.True(results[0].CanAutoAssign);
        Assert.Equal(LanternfallId, results[0].Record.Id);
        Assert.Equal("redirect", results[1].Reason);                                       // the merged duplicate
        Assert.True(results[1].CanAutoAssign);
        Assert.Equal(Fixtures.SeedId("lanternfall-duplicate"), results[1].RedirectedFrom);
        Assert.Equal(LampKeeperId, results[2].CanonicalId);
        Assert.Equal(MediaTypeKind.Book, results[2].Record.MediaTypeKind);
        Assert.False(results[3].CanAutoAssign);                                            // a title alone: a candidate, never assigned
        Assert.Equal(Fixtures.SeedId("quiet-orbit"), Assert.Single(results[3].Candidates).Id);
        Assert.Null(results[3].Record);
        Assert.False(results[4].CanAutoAssign);
        Assert.Empty(results[4].Candidates);
    }

    [Theory, MemberData(nameof(Adapters.Both), MemberType = typeof(Adapters))]
    public void Recorded_batch_answer_skips_not_found_and_follows_merges(string adapter)
    {
        var j = Adapters.Make(adapter);
        var records = AtlasReader.ReadBatch(j.Parse(Fixtures.Body("fixtures/games/batch-mixed.json")));
        Assert.Equal(new[] { LanternfallId, LanternfallId, Fixtures.SeedId("copper-tide") }, records.Select(r => r.Id));
        Assert.Equal(Fixtures.SeedId("lanternfall-duplicate"), records[1].RedirectedFrom);
        Assert.Equal(4, j.Parse(Fixtures.Request("fixtures/games/batch-mixed.json"))["ids"].Items.Count);
        var future = AtlasReader.ReadBatch(j.Parse("{\"results\":[{\"requested\":\"a\",\"status\":\"forbidden_later\",\"game\":{\"id\":\"a\"}},{\"id\":\"b\"},7]}"));
        Assert.Equal("b", Assert.Single(future).Id);                                     // unknown status skipped; a bare record still read
    }

    [Theory]
    [InlineData("id", true, true)]
    [InlineData("redirect", true, true)]
    [InlineData("crosswalk", true, true)]
    [InlineData("exact", true, false)]       // an exact TITLE match: a candidate for the user, never automatic (C15 8.4)
    [InlineData("crosswalk", false, false)]  // Atlas says not assignable: believe it
    [InlineData("conflict", true, false)]
    [InlineData("fuzzy", true, false)]
    [InlineData("none", false, false)]
    [InlineData("some_future_reason", true, false)]
    public void Only_id_redirect_crosswalk_auto_assign(string reason, bool assigned, bool expected)
    {
        var r = new ResolveResult { CanonicalId = "atlas:x", Reason = reason, Assigned = assigned };
        Assert.Equal(expected, r.CanAutoAssign);
    }

    [Fact]
    public void No_canonical_id_never_auto_assigns_and_older_atlas_is_judged_by_method()
    {
        Assert.False(new ResolveResult { Reason = "id", Assigned = true }.CanAutoAssign);
        Assert.True(new ResolveResult { CanonicalId = "atlas:x", Method = "crosswalk" }.CanAutoAssign);
        Assert.False(new ResolveResult { CanonicalId = "atlas:x", Method = "fuzzy" }.CanAutoAssign);
        Assert.False(new ResolveResult { CanonicalId = "atlas:x", Method = "exact" }.CanAutoAssign);
    }

    [Theory, MemberData(nameof(Adapters.Both), MemberType = typeof(Adapters))]
    public void Relations_and_artwork_shapes(string adapter)
    {
        var j = Adapters.Make(adapter);
        var g = AtlasReader.ReadRelations(j.Parse(Fixtures.Text("fixtures/games/relations-lanternfall.json")));
        Assert.Equal(LanternfallId, Assert.Single(g.Nodes).Id);
        Assert.False(g.Truncated);

        var book = AtlasReader.ReadRelations(j.Parse(Fixtures.Text("fixtures/books/relations-the-lamp-keeper.json")));
        var edge = Assert.Single(book.Edges);
        Assert.Equal("series_member", edge.Type);
        Assert.Equal("1", edge.Position);
        Assert.Equal(LampKeeperId, edge.Source);
        Assert.Equal(Fixtures.SeedId("series-lamp-cycle"), edge.Target);
        Assert.Equal(2, book.Editions.Count);
        Assert.True(book.Truncated);

        var edges = AtlasReader.ReadRelations(j.Parse("{\"id\":\"a\",\"nodes\":[],\"edges\":[{\"from\":\"a\",\"to\":\"b\",\"type\":\"series_member\",\"position\":2.5},{\"from\":\"b\",\"to\":\"c\",\"type\":\"future\"}]}"));
        Assert.Equal("2.5", edges.Edges[0].Position);
        Assert.Equal("future", edges.Edges[1].Type);

        // client-api 1.0: primary_by_role is role -> id; `primary` is keyed by KIND and must not be taken for roles
        var art = AtlasReader.ReadArtwork(j.Parse(Fixtures.Text("fixtures/games/artwork-lanternfall.json")));
        Assert.Equal(new[] { "background", "cover", "icon", "logo" }, art.PrimaryByRole.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal("key_art", art.Items.Single(a => a.Id == art.PrimaryByRole["cover"]).Kind);
        Assert.Equal("hero", art.Items.Single(a => a.Id == art.PrimaryByRole["background"]).Kind);
        Assert.False(art.PrimaryByRole.ContainsKey("key_art"));
        Assert.All(art.Items, a => Assert.StartsWith("/api/v2/artwork/", a.ImageUrl));
        var bookArt = AtlasReader.ReadArtwork(j.Parse(Fixtures.Text("fixtures/books/artwork-the-lamp-keeper.json")));
        Assert.Equal(new[] { "cover" }, bookArt.PrimaryByRole.Keys);                       // null roles are absent, not ""
        Assert.Equal("poster", bookArt.Items.Single(a => a.Id == bookArt.PrimaryByRole["cover"]).Kind);

        // an Atlas without primary_by_role: `primary` is the fallback (objects or ids)
        var older = AtlasReader.ReadArtwork(j.Parse("{\"id\":\"a\",\"primary\":{\"cover\":{\"id\":\"7\",\"kind\":\"cover\"}},\"results\":[{\"id\":\"7\",\"kind\":\"cover\",\"media_url\":\"/api/v2/media/abc\",\"image_url\":\"/api/v2/artwork/7/image\",\"cached\":true,\"preferred\":true,\"width\":600}]}"));
        Assert.Equal("7", older.PrimaryByRole["cover"]);
        Assert.Equal("/api/v2/media/abc", older.Items[0].MediaUrl);
        Assert.True(older.Items[0].Cached);
        var ids = AtlasReader.ReadArtwork(j.Parse("{\"id\":\"a\",\"primary\":{\"cover\":\"7\",\"icon\":9},\"items\":[]}"));
        Assert.Equal("9", ids.PrimaryByRole["icon"]);
        var both = AtlasReader.ReadArtwork(j.Parse("{\"primary\":{\"cover\":\"kind-pick\"},\"primary_by_role\":{\"cover\":\"role-pick\",\"logo\":null},\"results\":[]}"));
        Assert.Equal("role-pick", both.PrimaryByRole["cover"]);
        Assert.False(both.PrimaryByRole.ContainsKey("logo"));
    }

    [Theory]
    [InlineData("2011-04-19", "day", 2011, 4, 19)]
    [InlineData("2011-04-19", "month", 2011, 4, null)]
    [InlineData("2011-04-19", "year", 2011, null, null)]
    [InlineData("2011-04-19", null, 2011, 4, 19)]
    [InlineData("2011-04", "day", 2011, 4, null)]          // precision cannot add a day the text does not have
    [InlineData("2011", "day", 2011, null, null)]          // ...nor a month
    [InlineData("1999", "year", 1999, null, null)]
    [InlineData("2011-04-19", "decade", 2011, 4, 19)]      // unknown precision word: trust the text
    [InlineData(" 2012-02-29 ", "day", 2012, 2, 29)]
    public void Partial_dates_are_only_as_precise_as_proven(string text, string precision, int y, int? m, int? d)
    {
        Assert.True(PartialDate.TryParse(text, precision, out var p));
        Assert.Equal(new PartialDate(y, m, d), p);
    }

    [Theory]
    [InlineData("")] [InlineData(null)] [InlineData("2011-13-01")] [InlineData("2011-02-30")] [InlineData("2011-4-9")]
    [InlineData("11-04-19")] [InlineData("2011-04-19-01")] [InlineData("abcd")] [InlineData("0000")] [InlineData("2023-02-29")]
    public void Bad_dates_are_refused_not_guessed(string text) => Assert.False(PartialDate.TryParse(text, "day", out _));

    [Fact]
    public void A_day_without_a_month_is_dropped()
    {
        Assert.Null(new PartialDate(2011, null, 19).Day);
        Assert.Equal("2011", new PartialDate(2011, null, null).ToString());
        Assert.Equal("2011-04", new PartialDate(2011, 4, null).ToString());
    }

    [Fact]
    public void Release_date_and_year_are_fallbacks_for_first_release()
    {
        var j = Adapters.Make("stj");
        var a = AtlasReader.ReadRecord(j.Parse("{\"id\":\"a\",\"release_date\":\"2004-11-16\"}"));
        Assert.Equal(new PartialDate(2004, 11, 16), a.FirstRelease);
        var b = AtlasReader.ReadRecord(j.Parse("{\"id\":\"a\",\"release_year\":1993}"));
        Assert.Equal(new PartialDate(1993, null, null), b.FirstRelease);
        Assert.Null(AtlasReader.ReadRecord(j.Parse("{\"id\":\"a\"}")).FirstRelease);
    }

    [Fact]
    public void Playnite_vectors_hold_for_the_recorded_fixtures()
    {
        // The mapping a connector must produce (contract/vectors/playnite.json), written once here as the reference shape.
        var j = Adapters.Make("newtonsoft");
        var vectors = j.Parse(Fixtures.Text("vectors/playnite.json"));
        Assert.Equal("1.0", vectors["min_client_api"].AsString());
        foreach (var c in vectors["cases"].Items)
        {
            var rec = AtlasReader.ReadRecord(j.Parse(Fixtures.Text(c["record"].AsString().StartsWith("vectors/") ? c["record"].AsString() : "fixtures/" + c["record"].AsString())));
            var e = c["expect"];
            Assert.Equal(e["Name"].AsString(), rec.Title);
            if (e.Has("ReleaseDate"))
            {
                var rd = e["ReleaseDate"];
                Assert.Equal((int)rd["Year"].AsLong().Value, rec.FirstRelease.Value.Year);
                Assert.Equal((int?)rd["Month"].AsLong(), rec.FirstRelease.Value.Month);
                Assert.Equal((int?)rd["Day"].AsLong(), rec.FirstRelease.Value.Day);
            }
            if (e.Has("Developers")) Assert.Equal(e["Developers"].Items.Select(x => x.AsString()), new[] { rec.Developer });
        }
    }
}
