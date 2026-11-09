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

    [Theory, MemberData(nameof(Adapters.Both), MemberType = typeof(Adapters))]
    public void Portal_game_record(string adapter)
    {
        var rec = AtlasReader.ReadRecord(Adapters.Make(adapter).Parse(Fixtures.Text("fixtures/games/record-portal.json")));
        Assert.Equal("atlas:01m70bhdfwzc60xawvc1avtnv7", rec.Id);
        Assert.Equal("Portal 2", rec.Title);
        Assert.Equal(MediaTypeKind.Game, rec.MediaTypeKind);
        Assert.Equal("Valve", rec.Developer);
        Assert.Equal(new PartialDate(2011, 4, 19), rec.FirstRelease);
        Assert.Equal("620", rec.ProviderIds["steamsync"]);
        Assert.Equal(2, rec.Version);
        Assert.Null(rec.RedirectedFrom);
    }

    [Theory, MemberData(nameof(Adapters.Both), MemberType = typeof(Adapters))]
    public void Book_record_draft(string adapter)
    {
        var rec = AtlasReader.ReadRecord(Adapters.Make(adapter).Parse(Fixtures.Text("fixtures/books/record-synthetic-lighthouse.json")));
        Assert.Equal(MediaTypeKind.Book, rec.MediaTypeKind);
        Assert.Equal(new[] { "Ada Example", "B. Placeholder" }, rec.Authors.Select(a => a.Name));
        Assert.Equal(2, rec.Editions.Count);
        Assert.Equal("9780000000019", rec.Editions[1].ProviderIds["isbn"]);
        Assert.Equal("The Fictional Coast", rec.Series.Name);
        Assert.Equal("2", rec.Series.Position);
        Assert.Equal(new PartialDate(1999, null, null), rec.FirstRelease);   // year precision: no month, no day
        Assert.Equal(DatePrecision.Year, rec.FirstRelease.Value.Precision);
        Assert.False(rec.AchievementsApplicable);
        Assert.Contains("Lighthouses", rec.Subjects);
        Assert.Equal("9780000000002", rec.ProviderIds["isbn"]);
    }

    [Theory, MemberData(nameof(Adapters.Both), MemberType = typeof(Adapters))]
    public void Unknown_members_and_unknown_vocabulary_are_ignored_and_kept(string adapter)
    {
        var rec = AtlasReader.ReadRecord(Adapters.Make(adapter).Parse(Fixtures.Text("fixtures/forward-compat/record-unknown-members.json")));
        Assert.Equal("Forward Compatibility Probe", rec.Title);
        Assert.Equal(new PartialDate(2011, 4, 19), rec.FirstRelease);
        Assert.Equal("holographic", rec.Raw["platform_family"].AsString());          // unknown vocabulary value: kept as text, no failure
        Assert.Equal(JsonKind.Object, rec.Raw["a_field_added_in_a_later_minor"].Kind);
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
        var games = AtlasReader.ReadSearch(j.Parse(Fixtures.Text("fixtures/games/search-portal.json")));
        Assert.Equal("Portal 2", Assert.Single(games.Results).Title);
        var books = AtlasReader.ReadSearch(j.Parse(Fixtures.Text("fixtures/books/search-lighthouse.json")));
        Assert.Equal(1, books.Total);
        Assert.Equal(20, books.Limit);
        Assert.Equal(MediaTypeKind.Book, Assert.Single(books.Results).MediaTypeKind);
    }

    [Theory, MemberData(nameof(Adapters.Both), MemberType = typeof(Adapters))]
    public void Site_with_and_without_client_api(string adapter)
    {
        var j = Adapters.Make(adapter);
        var today = AtlasReader.ReadSite(j.Parse(Fixtures.Text("fixtures/games/site.json")));
        Assert.Equal(1, today.Contract);
        Assert.Null(today.ClientApi);
        Assert.Contains("game", today.MediaTypes);
        var a24 = AtlasReader.ReadSite(j.Parse(Rig.SiteWith("{\"version\":\"1.3\",\"min_supported\":\"1.0\",\"capabilities\":[\"books\",\"batch\"],\"deprecations\":[]}")));
        Assert.Equal(1, a24.ClientApi.Major);
        Assert.Equal(new[] { "books", "batch" }, a24.ClientApi.Capabilities);
    }

    [Theory, MemberData(nameof(Adapters.Both), MemberType = typeof(Adapters))]
    public void Resolve_answer_and_the_auto_assign_rule(string adapter)
    {
        var results = AtlasReader.ReadResolve(Adapters.Make(adapter).Parse(Fixtures.Text("fixtures/games/resolve-by-atlas-id.json")));
        var r = Assert.Single(results);
        Assert.Equal("id", r.Reason);
        Assert.True(r.Assigned);
        Assert.True(r.CanAutoAssign);
        Assert.Equal("Portal 2", r.Record.Title);
        Assert.Single(r.Candidates);
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
        var g = AtlasReader.ReadRelations(j.Parse(Fixtures.Text("fixtures/games/relations-portal.json")));
        Assert.Equal("Portal 2", Assert.Single(g.Nodes).Title);
        Assert.False(g.Truncated);

        var edges = AtlasReader.ReadRelations(j.Parse("{\"id\":\"a\",\"nodes\":[],\"edges\":[{\"from\":\"a\",\"to\":\"b\",\"type\":\"series_member\",\"position\":2.5},{\"from\":\"b\",\"to\":\"c\",\"type\":\"future\"}]}"));
        Assert.Equal("2.5", edges.Edges[0].Position);
        Assert.Equal("series_member", edges.Edges[0].Type);
        Assert.Equal("future", edges.Edges[1].Type);

        var today = AtlasReader.ReadArtwork(j.Parse("{\"id\":\"a\",\"primary\":{\"cover\":{\"id\":\"7\",\"kind\":\"cover\"}},\"results\":[{\"id\":\"7\",\"kind\":\"cover\",\"media_url\":\"/api/v2/media/abc\",\"image_url\":\"/api/v2/artwork/7/image\",\"cached\":true,\"preferred\":true,\"width\":600}]}"));
        Assert.Equal("7", today.PrimaryByRole["cover"]);
        Assert.Equal("/api/v2/media/abc", today.Items[0].MediaUrl);
        Assert.True(today.Items[0].Cached);
        var a24 = AtlasReader.ReadArtwork(j.Parse("{\"id\":\"a\",\"primary\":{\"cover\":\"7\",\"icon\":9},\"items\":[]}"));
        Assert.Equal("7", a24.PrimaryByRole["cover"]);
        Assert.Equal("9", a24.PrimaryByRole["icon"]);
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
            var rec = AtlasReader.ReadRecord(j.Parse(Fixtures.Text("fixtures/" + c["record"].AsString())));
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
