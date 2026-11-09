using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Atlas.Client;
using Atlas.Playnite;
using Playnite.SDK.Models;
using Xunit;

namespace Atlas.Playnite.Tests
{
    /// <summary>Runs contract/vectors/playnite.json: Atlas record in, Playnite GameMetadata fields out.</summary>
    public class MapperVectorTests
    {
        private static readonly IJsonAdapter Json = new NewtonsoftJsonAdapter();

        public static IEnumerable<object[]> Cases()
        {
            var v = Json.Parse(Repo.Text("contract", "vectors", "playnite.json"));
            foreach (var c in v["cases"].Items) yield return new object[] { c["name"].AsString() };
        }

        private static AtlasRecord LoadRecord(string relative)
        {
            var baseDir = relative.StartsWith("vectors/", StringComparison.Ordinal) ? "contract" : Path.Combine("contract", "fixtures");
            return AtlasReader.ReadRecord(Json.Parse(Repo.Text(baseDir, relative.Replace('/', Path.DirectorySeparatorChar))));
        }

        /// <summary>The fields a vector can talk about, as plain JSON values, so the comparison is one code path.</summary>
        private static JsonNode Project(GameMetadata m, string field)
        {
            Func<IEnumerable<MetadataProperty>, JsonNode> names = set => set == null ? JsonNode.Null :
                JsonNode.Array(set.Select(p => JsonNode.Str(((MetadataNameProperty)p).Name)));
            switch (field)
            {
                case "Name": return JsonNode.Str(m.Name);
                case "Description": return JsonNode.Str(m.Description);
                case "ReleaseDate":
                    if (!m.ReleaseDate.HasValue) return JsonNode.Null;
                    var d = m.ReleaseDate.Value;
                    var members = new List<KeyValuePair<string, JsonNode>> { new KeyValuePair<string, JsonNode>("Year", JsonNode.Num(d.Year)) };
                    if (d.Month.HasValue) members.Add(new KeyValuePair<string, JsonNode>("Month", JsonNode.Num(d.Month.Value)));
                    if (d.Day.HasValue) members.Add(new KeyValuePair<string, JsonNode>("Day", JsonNode.Num(d.Day.Value)));
                    return JsonNode.Object(members);
                case "Developers": return names(m.Developers);
                case "Publishers": return names(m.Publishers);
                case "Series": return names(m.Series);
                case "Genres": return names(m.Genres);
                case "Tags": return names(m.Tags);
                case "Links":
                    return m.Links == null ? JsonNode.Null : JsonNode.Array(m.Links.Select(l => JsonNode.Object(new[]
                    {
                        new KeyValuePair<string, JsonNode>("Name", JsonNode.Str(l.Name)),
                        new KeyValuePair<string, JsonNode>("Url", JsonNode.Str(l.Url)),
                    })));
                default: throw new InvalidOperationException("vector names a field this test cannot project: " + field);
            }
        }

        private static bool Same(JsonNode a, JsonNode b)
        {
            if (a.Kind != b.Kind) return false;
            switch (a.Kind)
            {
                case JsonKind.String: return a.AsString() == b.AsString();
                case JsonKind.Number: return a.AsDouble() == b.AsDouble();
                case JsonKind.Array: return a.Items.Count == b.Items.Count && a.Items.Zip(b.Items, Same).All(x => x);
                case JsonKind.Object:
                    return a.Members.Count == b.Members.Count && a.Members.All(m => b.Has(m.Key) && Same(m.Value, b[m.Key]));
                default: return true;
            }
        }

        [Theory]
        [MemberData(nameof(Cases))]
        public void Vector(string name)
        {
            var v = Json.Parse(Repo.Text("contract", "vectors", "playnite.json"));
            var c = v["cases"].Items.Single(x => x["name"].AsString() == name);
            var meta = GameMapper.Map(LoadRecord(c["record"].AsString()), v["base_url"].AsString());

            foreach (var e in c["expect"].Members)
                Assert.True(Same(e.Value, Project(meta, e.Key)), name + ": " + e.Key + " expected " + Json.Write(e.Value) + " got " + Json.Write(Project(meta, e.Key)));

            foreach (var f in c["expect_absent"].Items)
            {
                var got = Project(meta, f.AsString());
                Assert.True(got.IsNull || (got.Kind == JsonKind.Array && got.Items.Count == 0), name + ": " + f.AsString() + " should be absent, got " + Json.Write(got));
            }
        }

        [Fact]
        public void The_vector_file_is_the_one_in_the_repo_and_has_cases()
        {
            var v = Json.Parse(Repo.Text("contract", "vectors", "playnite.json"));
            Assert.Equal("playnite", v["connector"].AsString());
            Assert.True(v["cases"].Items.Count >= 9);
        }

        [Fact]
        public void Every_recorded_game_fixture_maps_without_throwing_and_keeps_its_title()
        {
            var root = Path.Combine(Repo.Root, "contract", "fixtures");
            var n = 0;
            foreach (var path in Directory.EnumerateFiles(root, "record-*.json", SearchOption.AllDirectories))
            {
                var rec = AtlasReader.ReadRecord(Json.Parse(File.ReadAllText(path)));
                var meta = GameMapper.Map(rec, "https://atlas.example.org/");   // books too: shared fields only, never a crash
                Assert.Equal(rec.Title, meta.Name);
                n++;
            }
            Assert.True(n >= 3);
        }

        [Fact]
        public void Book_subjects_become_genres_and_the_book_is_not_a_game()
        {
            var rec = AtlasReader.ReadRecord(Json.Parse(Repo.Text("contract", "fixtures", "books", "record-synthetic-lighthouse.json")));
            Assert.Equal(MediaTypeKind.Book, rec.MediaTypeKind);
            var meta = GameMapper.Map(rec, "https://atlas.example.org/");
            Assert.Equal(new[] { "Fiction", "Lighthouses" }, meta.Genres.Select(p => ((MetadataNameProperty)p).Name).ToArray());
            Assert.Equal(1999, meta.ReleaseDate.Value.Year);
            Assert.Null(meta.ReleaseDate.Value.Month);
        }

        [Fact]
        public void Atlas_link_round_trips_and_other_links_are_not_taken_for_ids()
        {
            var link = AtlasLinks.RecordLink("https://atlas.example.org", TestAtlas.PortalId);
            Assert.Equal("https://atlas.example.org/game/atlas:01m70bhdfwzc60xawvc1avtnv7", link.Url);
            Assert.Equal(TestAtlas.PortalId, AtlasLinks.IdFromLink(link, "https://atlas.example.org/"));
            Assert.Null(AtlasLinks.IdFromLink(link, "https://other.example.org/"));
            Assert.Null(AtlasLinks.IdFromLink(new Link("Website", link.Url), "https://atlas.example.org/"));
            Assert.Null(AtlasLinks.IdFromLink(new Link("Atlas", "https://atlas.example.org/game/not-an-atlas-id"), "https://atlas.example.org/"));
            Assert.Null(AtlasLinks.IdFromLink(new Link("Atlas", "https://atlas.example.org/game/atlas:abc/../x"), "https://atlas.example.org/"));
        }
    }
}
