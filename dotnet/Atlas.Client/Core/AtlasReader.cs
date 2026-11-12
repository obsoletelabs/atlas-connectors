using System;
using System.Collections.Generic;

namespace Atlas.Client
{
    /// <summary>
    /// The tolerant reader (client-api guarantees 1 and 2): unknown members are ignored, unknown vocabulary values are kept
    /// as text, a member of the wrong type reads as absent. Nothing here throws on a shape Atlas may add later.
    /// </summary>
    public static class AtlasReader
    {
        public static SiteInfo ReadSite(JsonNode n)
        {
            if (n.Kind != JsonKind.Object) return null;
            var site = new SiteInfo
            {
                Name = n["name"].AsString(),
                Role = n["role"].AsString(),
                Kind = n["kind"].AsString(),
                NodeId = n["node_id"].AsString(),
                MediaTypes = Strings(n["media_types"]),
                Contract = n["contract"].AsLong(),
            };
            var ca = n["client_api"];
            if (ca.Kind == JsonKind.Object)
                site.ClientApi = new ClientApiInfo
                {
                    Version = ca["version"].AsString(),
                    MinSupported = ca["min_supported"].AsString(),
                    Capabilities = Strings(ca["capabilities"]),
                    Deprecations = Strings(ca["deprecations"]),
                };
            return site;
        }

        public static AtlasRecord ReadRecord(JsonNode n)
        {
            if (n.Kind != JsonKind.Object) return null;
            var facts = n["facts"];
            var rec = new AtlasRecord
            {
                Id = n["id"].AsString(),
                Version = n["version"].AsLong(),
                MediaType = n["media_type"].AsString(),
                Title = n["title"].AsString(),
                Description = n["description"].AsString() ?? facts["description"].AsString(),
                Developer = FirstString(n["developer"]) ?? FirstString(facts["developer"]),
                Publisher = FirstString(n["publisher"]) ?? FirstString(facts["publisher"]),
                Subjects = Topics(n),
                ProviderIds = StringMap(n["provider_ids"]),
                Sources = Strings(n["sources"]),
                RedirectedFrom = n["redirected_from"].AsString(),
                AchievementsApplicable = n["achievements"]["applicable"].AsBool(),
                UpdatedAt = n["updated_at"].AsString(),
                Raw = n,
            };

            var authors = new List<Author>();
            foreach (var a in n["authors"].Items)
            {
                if (a.Kind == JsonKind.String) authors.Add(new Author { Name = a.AsString() });
                else if (a.Kind == JsonKind.Object && a["name"].AsString() != null)
                    authors.Add(new Author { Name = a["name"].AsString(), Id = a["id"].AsString(), Role = a["role"].AsString() });
            }
            rec.Authors = authors;

            rec.Editions = Editions(n["editions"]);

            var series = n["series"].Kind == JsonKind.Object ? n["series"] : facts["series"];
            if (series.Kind == JsonKind.Object)
                rec.Series = new SeriesInfo { Name = series["name"].AsString(), Position = Text(series["position"]) };
            else
            {
                // client-api 1.0: a book's series is a relation (members_of, type series_member), with the position on the edge
                foreach (var m in n["relations"]["members_of"].Items)
                {
                    if (m.Kind != JsonKind.Object || m["title"].AsString() == null) continue;
                    var type = m["type"].AsString();
                    if (type != null ? type != "series_member" : m["media_type"].AsString() != "series") continue;
                    rec.Series = new SeriesInfo { Name = m["title"].AsString(), Position = Text(m["position"]), Id = m["id"].AsString() };
                    break;
                }
            }

            PartialDate date;
            var fr = n["first_release"];
            if (fr.Kind == JsonKind.Object && PartialDate.TryParse(fr["date"].AsString(), fr["precision"].AsString(), out date))
                rec.FirstRelease = date;
            else if (PartialDate.TryParse(n["release_date"].AsString(), null, out date))
                rec.FirstRelease = date;
            else if (n["release_year"].AsLong().HasValue && n["release_year"].AsLong().Value >= 1 && n["release_year"].AsLong().Value <= 9999)
                rec.FirstRelease = new PartialDate((int)n["release_year"].AsLong().Value, null, null);
            return rec;
        }

        public static SearchPage ReadSearch(JsonNode n)
        {
            if (n.Kind != JsonKind.Object) return null;
            var list = n["results"].Kind == JsonKind.Array ? n["results"] : n["items"];
            var results = new List<AtlasRecord>();
            foreach (var item in list.Items)
            {
                var r = ReadRecord(item);
                if (r != null) results.Add(r);
            }
            return new SearchPage { Results = results, Total = n["total"].AsLong(), Limit = n["limit"].AsLong(), Offset = n["offset"].AsLong() };
        }

        public static ResolveResult ReadResolveResult(JsonNode n)
        {
            if (n.Kind != JsonKind.Object) return null;
            var res = new ResolveResult
            {
                CanonicalId = n["canonical_id"].AsString(),
                Method = n["method"].AsString(),
                Reason = n["reason"].AsString(),
                Tier = n["tier"].AsString(),
                Assigned = n["assigned"].AsBool(),
                Confidence = n["confidence"].AsDouble(),
                RedirectedFrom = n["redirected_from"].AsString(),
                // `game` is the field name today; `record` is accepted for when /records becomes the primary name
                Record = ReadRecord(n["game"].Kind == JsonKind.Object ? n["game"] : n["record"]),
            };
            var cands = new List<ResolveCandidate>();
            foreach (var c in n["candidates"].Items)
                if (c.Kind == JsonKind.Object && c["id"].AsString() != null)
                    cands.Add(new ResolveCandidate { Id = c["id"].AsString(), Score = c["score"].AsDouble() });
            res.Candidates = cands;
            return res;
        }

        /// <summary>
        /// <c>POST /games/batch</c> (client-api 1.0): <c>{"results": [{"requested", "status": "ok" | "not_found", "game"}]}</c>, in
        /// request order. Only <c>ok</c> items with a record are returned; <c>not_found</c> and any status added later are skipped.
        /// A bare record in the list (a pre-release draft shape) is still read.
        /// </summary>
        public static IReadOnlyList<AtlasRecord> ReadBatch(JsonNode n)
        {
            var list = new List<AtlasRecord>();
            foreach (var item in (n["results"].Kind == JsonKind.Array ? n["results"] : n["items"]).Items)
            {
                if (item.Kind != JsonKind.Object) continue;
                AtlasRecord r;
                var status = item["status"].AsString();
                if (status != null || item.Has("requested"))
                {
                    if (status != "ok") continue;
                    r = ReadRecord(item["game"].Kind == JsonKind.Object ? item["game"] : item["record"]);
                }
                else r = ReadRecord(item);
                if (r != null) list.Add(r);
            }
            return list;
        }

        public static IReadOnlyList<ResolveResult> ReadResolve(JsonNode n)
        {
            var list = new List<ResolveResult>();
            foreach (var item in n["results"].Items) list.Add(ReadResolveResult(item) ?? new ResolveResult());
            return list;
        }

        public static RelationGraph ReadRelations(JsonNode n)
        {
            if (n.Kind != JsonKind.Object) return null;
            var g = new RelationGraph
            {
                Id = n["id"].AsString(),
                RedirectedFrom = n["redirected_from"].AsString(),
                Truncated = n["truncated"].AsBool() ?? false,
                Editions = Editions(n["editions"]),
            };
            var nodes = new List<RelationNode>();
            foreach (var x in n["nodes"].Items)
                if (x.Kind == JsonKind.Object)
                    nodes.Add(new RelationNode { Id = x["id"].AsString(), MediaType = x["media_type"].AsString(), Title = x["title"].AsString() });
            g.Nodes = nodes;
            var edges = new List<RelationEdge>();
            foreach (var x in n["edges"].Items)
                if (x.Kind == JsonKind.Object)
                    edges.Add(new RelationEdge
                    {
                        Source = x["from"].AsString() ?? x["source"].AsString(),
                        Target = x["to"].AsString() ?? x["target"].AsString(),
                        Type = x["type"].AsString(),
                        Position = Text(x["position"]),
                        Raw = x,
                    });
            g.Edges = edges;
            return g;
        }

        public static ArtworkList ReadArtwork(JsonNode n)
        {
            if (n.Kind != JsonKind.Object) return null;
            var items = new List<Artwork>();
            foreach (var x in (n["results"].Kind == JsonKind.Array ? n["results"] : n["items"]).Items)
            {
                if (x.Kind != JsonKind.Object) continue;
                items.Add(new Artwork
                {
                    Id = x["id"].AsString() ?? Text(x["id"]),
                    Kind = x["kind"].AsString(),
                    Url = x["url"].AsString(),
                    Width = x["width"].AsLong(),
                    Height = x["height"].AsLong(),
                    Source = x["source"].AsString(),
                    Preferred = x["preferred"].AsBool() ?? false,
                    Cached = x["cached"].AsBool() ?? false,
                    MediaUrl = x["media_url"].AsString(),
                    ImageUrl = x["image_url"].AsString(),
                });
            }
            // client-api 1.0: primary_by_role maps a ROLE (cover, background, icon, logo) to an artwork id or null, already ranked.
            // `primary` is keyed by artwork KIND (hero, key_art, ...) and is only a fallback for an Atlas without primary_by_role;
            // a kind that is not also a role name is never taken for one, because callers look roles up by name.
            var primary = new Dictionary<string, string>();
            var byRole = n["primary_by_role"];
            foreach (var m in (byRole.Kind == JsonKind.Object ? byRole : n["primary"]).Members)
            {
                var id = m.Value.AsString() ?? (m.Value.Kind == JsonKind.Object ? m.Value["id"].AsString() : null) ?? Text(m.Value);
                if (id != null) primary[m.Key] = id;
            }
            return new ArtworkList { Id = n["id"].AsString(), RedirectedFrom = n["redirected_from"].AsString(), Items = items, PrimaryByRole = primary };
        }

        // ---- helpers -----------------------------------------------------------------------------------------

        /// <summary>A string, or the digits of a number ("2", "2.5"), else null.</summary>
        internal static string Text(JsonNode n) => n.AsString() ?? n.RawNumber;

        private static string FirstString(JsonNode n)
        {
            if (n.Kind == JsonKind.String) return n.AsString();
            if (n.Kind == JsonKind.Array)
                foreach (var x in n.Items) if (x.Kind == JsonKind.String) return x.AsString();
            return null;
        }

        internal static IReadOnlyList<string> Strings(JsonNode n)
        {
            var list = new List<string>();
            foreach (var x in n.Items) if (x.Kind == JsonKind.String) list.Add(x.AsString());
            return list;
        }

        internal static IReadOnlyDictionary<string, string> StringMap(JsonNode n)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var m in n.Members)
            {
                var v = Text(m.Value);
                if (v != null) map[m.Key] = v;
            }
            return map;
        }

        /// <summary>subjects, else genres, else (books only) features: Atlas files Open Library subjects under features.</summary>
        private static IReadOnlyList<string> Topics(JsonNode n)
        {
            var subjects = Strings(n["subjects"]);
            if (subjects.Count > 0) return subjects;
            var genres = Strings(n["genres"]);
            if (genres.Count > 0) return genres;
            return MediaTypes.Parse(n["media_type"].AsString()) == MediaTypeKind.Book ? Strings(n["features"]) : genres;
        }

        private static IReadOnlyList<Edition> Editions(JsonNode n)
        {
            var list = new List<Edition>();
            foreach (var e in n.Items)
            {
                if (e.Kind != JsonKind.Object) continue;
                var ed = new Edition
                {
                    Name = e["name"].AsString(),
                    Format = e["format"].AsString(),
                    Isbn13 = Text(e["isbn_13"]),
                    Isbn10 = Text(e["isbn_10"]),
                    Language = e["language"].AsString(),
                    Publisher = FirstString(e["publisher"]),
                    ProviderIds = StringMap(e["provider_ids"]),
                };
                PartialDate published;
                var pub = e["published"];
                if (pub.Kind == JsonKind.Object && PartialDate.TryParse(pub["date"].AsString(), pub["precision"].AsString(), out published))
                    ed.Published = published;
                list.Add(ed);
            }
            return list;
        }

        // ---- request bodies ----------------------------------------------------------------------------------

        public static JsonNode WriteResolveRequest(IEnumerable<ResolveItem> items, bool includeView)
        {
            var arr = new List<JsonNode>();
            foreach (var i in items)
            {
                var m = new List<KeyValuePair<string, JsonNode>>();
                Add(m, "namespace", i.Namespace);
                Add(m, "external_id", i.ExternalId);
                Add(m, "media_type", i.MediaType);
                Add(m, "title", i.Title);
                Add(m, "platform", i.Platform);
                Add(m, "release_date", i.ReleaseDate);
                Add(m, "developer", i.Developer);
                if (i.ProviderIds != null && i.ProviderIds.Count > 0)
                {
                    var ids = new List<KeyValuePair<string, JsonNode>>();
                    foreach (var kv in i.ProviderIds)
                        if (!string.IsNullOrEmpty(kv.Key) && !string.IsNullOrEmpty(kv.Value))
                            ids.Add(new KeyValuePair<string, JsonNode>(kv.Key, JsonNode.Str(kv.Value)));
                    m.Add(new KeyValuePair<string, JsonNode>("provider_ids", JsonNode.Object(ids)));
                }
                arr.Add(JsonNode.Object(m));
            }
            return JsonNode.Object(new[]
            {
                new KeyValuePair<string, JsonNode>("items", JsonNode.Array(arr)),
                new KeyValuePair<string, JsonNode>("include_view", JsonNode.Bool(includeView)),
            });
        }

        private static void Add(List<KeyValuePair<string, JsonNode>> m, string key, string value)
        {
            if (!string.IsNullOrEmpty(value)) m.Add(new KeyValuePair<string, JsonNode>(key, JsonNode.Str(value)));
        }
    }
}
