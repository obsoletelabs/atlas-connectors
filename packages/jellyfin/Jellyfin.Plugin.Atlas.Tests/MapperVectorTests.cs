using System.Globalization;
using System.Text.Json;
using Atlas.Client;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.Atlas.Tests;

/// <summary>Runs contract/vectors/jellyfin.json: Atlas record in, Jellyfin item fields out (real Jellyfin entity classes).</summary>
public class MapperVectorTests
{
    private static readonly IJsonAdapter Json = new SystemTextJsonAdapter();

    public static IEnumerable<object[]> Cases()
    {
        using var doc = JsonDocument.Parse(Repo.Text("contract", "vectors", "jellyfin.json"));
        foreach (var c in doc.RootElement.GetProperty("cases").EnumerateArray())
        {
            yield return new object[] { c.GetProperty("name").GetString() };
        }
    }

    private static AtlasRecord LoadRecord(string relative)
    {
        var baseDir = relative.StartsWith("vectors/", StringComparison.Ordinal) ? "contract" : Path.Combine("contract", "fixtures");
        return AtlasReader.ReadRecord(Json.Parse(Repo.Text(baseDir, relative.Replace('/', Path.DirectorySeparatorChar))));
    }

    private static (BaseItem Item, List<PersonInfo> People) Map(string kind, AtlasRecord r)
    {
        switch (kind)
        {
            case "book":
                var b = ItemMapper.ToBook(r);
                return (b.Item, b.People?.ToList() ?? new List<PersonInfo>());   // List in 10.11, IReadOnlyList in 12
            case "movie": return (ItemMapper.ToMovie(r).Item, new List<PersonInfo>());
            case "series": return (ItemMapper.ToSeries(r).Item, new List<PersonInfo>());
            default: throw new InvalidOperationException("unknown kind " + kind);
        }
    }

    /// <summary>The value of a vector field on the mapped item, as JSON text; null when the item has none.</summary>
    private static JsonElement? Project(BaseItem item, List<PersonInfo> people, string field)
    {
        var authors = people.Where(p => p.Type == PersonKind.Author).Select(p => p.Name).ToList();
        object value = field switch
        {
            "Name" => item.Name,
            "Overview" => item.Overview,
            "ProductionYear" => item.ProductionYear,
            "PremiereDate" => item.PremiereDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            "Genres" => item.Genres is { Length: > 0 } ? item.Genres : null,
            "Tags" => item.Tags is { Length: > 0 } ? item.Tags : null,
            "Studios" => item.Studios is { Length: > 0 } ? item.Studios : null,
            "SeriesName" => (item as Book)?.SeriesName,
            "IndexNumber" => item.IndexNumber,
            "Authors" => authors.Count > 0 ? authors : null,
            "People" => people.Count > 0 ? people.Select(p => new Dictionary<string, string> { ["Name"] = p.Name, ["Kind"] = p.Type.ToString() }).ToList() : null,
            "ProviderIds" => item.ProviderIds.Count > 0 ? item.ProviderIds : null,
            _ => throw new InvalidOperationException("vector names a field this test cannot project: " + field),
        };
        return value == null ? null : JsonSerializer.SerializeToElement(value);
    }

    private static bool Matches(JsonElement expected, JsonElement actual)
    {
        if (expected.ValueKind == JsonValueKind.Object)
        {
            // a subset: every expected member must be present and equal (ProviderIds may carry more)
            return expected.EnumerateObject().All(p => actual.TryGetProperty(p.Name, out var a) && Matches(p.Value, a));
        }

        if (expected.ValueKind == JsonValueKind.Array)
        {
            var e = expected.EnumerateArray().ToList();
            var a = actual.ValueKind == JsonValueKind.Array ? actual.EnumerateArray().ToList() : new List<JsonElement>();
            return e.Count == a.Count && e.Zip(a).All(pair => Matches(pair.First, pair.Second));
        }

        return expected.GetRawText() == actual.GetRawText();
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Vector(string name)
    {
        using var doc = JsonDocument.Parse(Repo.Text("contract", "vectors", "jellyfin.json"));
        var c = doc.RootElement.GetProperty("cases").EnumerateArray().Single(x => x.GetProperty("name").GetString() == name);
        var (item, people) = Map(c.GetProperty("kind").GetString(), LoadRecord(c.GetProperty("record").GetString()));

        foreach (var e in c.GetProperty("expect").EnumerateObject())
        {
            var got = Project(item, people, e.Name);
            Assert.True(got.HasValue && Matches(e.Value, got.Value), name + ": " + e.Name + " expected " + e.Value.GetRawText() + " got " + (got?.GetRawText() ?? "nothing"));
        }

        if (c.TryGetProperty("expect_absent", out var absent))
        {
            foreach (var f in absent.EnumerateArray())
            {
                Assert.Null(Project(item, people, f.GetString()));
            }
        }
    }

    [Fact]
    public void The_vector_file_has_cases_for_every_media_kind()
    {
        using var doc = JsonDocument.Parse(Repo.Text("contract", "vectors", "jellyfin.json"));
        var kinds = doc.RootElement.GetProperty("cases").EnumerateArray().Select(c => c.GetProperty("kind").GetString()).ToHashSet();
        Assert.Equal("jellyfin", doc.RootElement.GetProperty("connector").GetString());
        Assert.Superset(new HashSet<string> { "book", "movie", "series" }, kinds);
    }

    [Fact]
    public void Every_recorded_fixture_maps_without_throwing_and_keeps_its_title()
    {
        var root = Path.Combine(Repo.Root, "contract", "fixtures");
        var n = 0;
        foreach (var path in Directory.EnumerateFiles(root, "record-*.json", SearchOption.AllDirectories))
        {
            var rec = AtlasReader.ReadRecord(Json.Parse(File.ReadAllText(path)));
            Assert.Equal(rec.Title, ItemMapper.ToMovie(rec).Item.Name);
            Assert.Equal(rec.Title, ItemMapper.ToBook(rec).Item.Name);
            n++;
        }

        Assert.True(n >= 3);
    }

    [Fact]
    public void Search_result_carries_the_atlas_id_and_the_same_date_rules()
    {
        var r = LoadRecord("vectors/records/movie-day-date.json");
        var s = ItemMapper.ToSearchResult(r, null);
        Assert.Equal("Synthetic Harbour", s.Name);
        Assert.Equal(2004, s.ProductionYear);
        Assert.Equal(new DateTime(2004, 6, 15, 0, 0, 0, DateTimeKind.Utc), s.PremiereDate);
        Assert.Equal(r.Id, s.ProviderIds["Atlas"]);
        Assert.Null(ItemMapper.ToSearchResult(LoadRecord("vectors/records/series-year-only.json"), null).PremiereDate);
    }

    [Fact]
    public void Plugin_ids_are_stable_and_the_external_ids_claim_only_their_own_type()
    {
        Assert.Equal(Guid.Parse("0ca48640-0596-432e-9978-e24ebb438d29"), Plugin.PluginId);
        Assert.True(new AtlasBookExternalId().Supports(new Book()));
        Assert.False(new AtlasBookExternalId().Supports(new Movie()));
        Assert.True(new AtlasMovieExternalId().Supports(new Movie()));
        Assert.True(new AtlasSeriesExternalId().Supports(new Series()));
        Assert.Equal("Atlas", new AtlasBookExternalId().Key);
    }

    [Fact]
    public void The_record_page_link_uses_the_configured_node_and_never_a_token()
    {
        using var h = new Harness(Harness.BookHandler());
        var urls = new AtlasExternalUrlProvider(() => h.Runtime);
        var book = new Book();
        book.SetProviderId("Atlas", Harness.BookId);

        Assert.Equal(new[] { "https://atlas.example.org/game/" + Harness.BookId }, urls.GetExternalUrls(book).ToArray());
        Assert.Empty(urls.GetExternalUrls(new Movie()));
        Assert.All(urls.GetExternalUrls(book), u => Assert.DoesNotContain(Harness.Token, u));
    }

    [Fact]
    public void Media_types_route_to_the_right_provider()
    {
        Assert.True(AtlasLookup.Accepts(ItemKind.Book, MediaTypeKind.Book));
        Assert.False(AtlasLookup.Accepts(ItemKind.Book, MediaTypeKind.Game));
        Assert.True(AtlasLookup.Accepts(ItemKind.Movie, MediaTypeKind.Movie));
        Assert.False(AtlasLookup.Accepts(ItemKind.Movie, MediaTypeKind.Anime));
        Assert.True(AtlasLookup.Accepts(ItemKind.Series, MediaTypeKind.Anime));
        Assert.True(AtlasLookup.Accepts(ItemKind.Series, MediaTypeKind.TvShow));
        Assert.False(AtlasLookup.Accepts(ItemKind.Series, MediaTypeKind.Unknown));
    }
}
