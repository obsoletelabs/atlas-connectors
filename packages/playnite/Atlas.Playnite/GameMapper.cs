using System;
using System.Collections.Generic;
using System.Linq;
using Atlas.Client;
using Playnite.SDK.Models;

namespace Atlas.Playnite
{
    /// <summary>
    /// Atlas record to Playnite <see cref="GameMetadata"/> (the expected fields are in contract/vectors/playnite.json).
    /// Rules (CONTRACT-C15 8.4, CLIENT-CONTRACT 6): a day is never invented, unknown vocabulary and unknown fields are ignored,
    /// subjects fill Genres first (10) and Tags after (D15.12).
    /// </summary>
    public static class GameMapper
    {
        public const int GenreLimit = 10;

        public static GameMetadata Map(AtlasRecord r, string atlasBaseUrl)
        {
            if (r == null) throw new ArgumentNullException(nameof(r));
            var m = new GameMetadata { Name = r.Title };
            if (!string.IsNullOrWhiteSpace(r.Description)) m.Description = r.Description;
            var date = ToReleaseDate(r.FirstRelease);
            if (date.HasValue) m.ReleaseDate = date;
            if (!string.IsNullOrWhiteSpace(r.Developer)) m.Developers = Names(new[] { r.Developer });
            if (!string.IsNullOrWhiteSpace(r.Publisher)) m.Publishers = Names(new[] { r.Publisher });
            if (r.Series != null && !string.IsNullOrWhiteSpace(r.Series.Name)) m.Series = Names(new[] { r.Series.Name });
            var subjects = r.Subjects.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (subjects.Count > 0) m.Genres = Names(subjects.Take(GenreLimit));
            if (subjects.Count > GenreLimit) m.Tags = Names(subjects.Skip(GenreLimit));
            var link = AtlasLinks.RecordLink(atlasBaseUrl, r.Id);
            if (link != null) m.Links = new List<Link> { link };
            return m;
        }

        /// <summary>Year, year-month or full date, exactly as precise as Atlas proved.</summary>
        public static ReleaseDate? ToReleaseDate(PartialDate? d)
        {
            if (!d.HasValue) return null;
            var p = d.Value;
            if (p.Year < 1 || p.Year > 9999) return null;
            if (p.Day.HasValue && p.Month.HasValue) return new ReleaseDate(p.Year, p.Month.Value, p.Day.Value);
            if (p.Month.HasValue) return new ReleaseDate(p.Year, p.Month.Value);
            return new ReleaseDate(p.Year);
        }

        private static HashSet<MetadataProperty> Names(IEnumerable<string> names)
        {
            var set = new HashSet<MetadataProperty>();
            foreach (var n in names) set.Add(new MetadataNameProperty(n));
            return set;
        }
    }

    /// <summary>The "Atlas" link on a Playnite game. It doubles as the stored Atlas id (Playnite has no hidden custom field).</summary>
    public static class AtlasLinks
    {
        public const string LinkName = "Atlas";
        private const string Marker = "/game/";

        public static Link RecordLink(string baseUrl, string atlasId)
        {
            if (string.IsNullOrEmpty(atlasId) || string.IsNullOrWhiteSpace(baseUrl)) return null;
            return new Link(LinkName, baseUrl.TrimEnd('/') + Marker + Uri.EscapeDataString(atlasId).Replace("%3A", ":"));
        }

        /// <summary>The Atlas id from a link made by <see cref="RecordLink"/> on the configured node, else null.</summary>
        public static string IdFromLink(Link link, string baseUrl)
        {
            if (link == null || link.Name != LinkName || string.IsNullOrWhiteSpace(link.Url) || string.IsNullOrWhiteSpace(baseUrl)) return null;
            var prefix = baseUrl.TrimEnd('/') + Marker;
            if (!link.Url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
            var id = Uri.UnescapeDataString(link.Url.Substring(prefix.Length)).Trim();
            return id.StartsWith("atlas:", StringComparison.Ordinal) && id.Length > 6 && id.IndexOf('/') < 0 ? id : null;
        }
    }
}
