using System;
using System.Collections.Generic;
using System.Globalization;

namespace Atlas.Client
{
    /// <summary>Known media types. A value Atlas adds later reads as <see cref="Unknown"/>; the raw string is always kept.</summary>
    public enum MediaTypeKind { Unknown, Game, Book, Anime, Movie, TvShow, Series, Collection }

    public static class MediaTypes
    {
        public static MediaTypeKind Parse(string value)
        {
            switch (value)
            {
                case "game": return MediaTypeKind.Game;
                case "book": return MediaTypeKind.Book;
                case "anime": return MediaTypeKind.Anime;
                case "movie": return MediaTypeKind.Movie;
                case "tv_show": return MediaTypeKind.TvShow;
                case "series": return MediaTypeKind.Series;
                case "collection": return MediaTypeKind.Collection;
                default: return MediaTypeKind.Unknown;
            }
        }
    }

    public enum DatePrecision { Year = 1, Month = 2, Day = 3 }

    /// <summary>
    /// A release date that is only as precise as Atlas can prove (C5). <see cref="Month"/> and <see cref="Day"/> are null
    /// when unknown. A day is never invented.
    /// </summary>
    public struct PartialDate : IEquatable<PartialDate>
    {
        public PartialDate(int year, int? month, int? day)
        {
            Year = year;
            Month = month;
            Day = month.HasValue ? day : null;
        }
        public int Year { get; }
        public int? Month { get; }
        public int? Day { get; }
        public DatePrecision Precision => Day.HasValue ? DatePrecision.Day : Month.HasValue ? DatePrecision.Month : DatePrecision.Year;

        /// <summary>
        /// Parse "YYYY", "YYYY-MM" or "YYYY-MM-DD". The declared precision can only reduce what the text says
        /// (precision "year" with "2011-04-19" gives 2011). Unknown precision words are ignored. Anything unparseable gives false.
        /// </summary>
        public static bool TryParse(string date, string precision, out PartialDate result)
        {
            result = default(PartialDate);
            if (string.IsNullOrWhiteSpace(date)) return false;
            var parts = date.Trim().Split('-');
            if (parts.Length > 3) return false;
            int y, m = 0, d = 0;
            if (parts[0].Length != 4 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out y) || y < 1) return false;
            int? month = null, day = null;
            if (parts.Length >= 2)
            {
                if (parts[1].Length != 2 || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out m) || m < 1 || m > 12) return false;
                month = m;
            }
            if (parts.Length == 3)
            {
                if (parts[2].Length != 2 || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out d) || d < 1) return false;
                if (d > DateTime.DaysInMonth(y, m)) return false;
                day = d;
            }
            if (precision == "year") { month = null; day = null; }
            else if (precision == "month") { day = null; }
            result = new PartialDate(y, month, day);
            return true;
        }

        public bool Equals(PartialDate o) => o.Year == Year && o.Month == Month && o.Day == Day;
        public override bool Equals(object obj) => obj is PartialDate && Equals((PartialDate)obj);
        public override int GetHashCode() => (Year * 31 + (Month ?? 0)) * 31 + (Day ?? 0);
        public override string ToString() => Year.ToString("0000", CultureInfo.InvariantCulture)
            + (Month.HasValue ? "-" + Month.Value.ToString("00", CultureInfo.InvariantCulture) : "")
            + (Day.HasValue ? "-" + Day.Value.ToString("00", CultureInfo.InvariantCulture) : "");
    }

    public sealed class Author
    {
        public string Name { get; set; }
        public string Id { get; set; }
        /// <summary>The creator role as Atlas sends it (<c>author</c>, <c>translator</c>, <c>narrator</c>, ...); null when Atlas did
        /// not say, which means author. A role this client does not know is kept as text.</summary>
        public string Role { get; set; }
        /// <summary>True for <c>author</c> or no role: the people who wrote the work (not translators, narrators, editors).</summary>
        public bool IsAuthor => Role == null || string.Equals(Role, "author", StringComparison.Ordinal);
    }

    public sealed class Edition
    {
        public string Name { get; set; }
        /// <summary>The <c>book_format</c> word (<c>paperback</c>, <c>hardcover</c>, <c>ebook</c>, ...); unknown words are kept as text.</summary>
        public string Format { get; set; }
        public string Isbn13 { get; set; }
        public string Isbn10 { get; set; }
        public string Language { get; set; }
        public string Publisher { get; set; }
        public PartialDate? Published { get; set; }
        public IReadOnlyDictionary<string, string> ProviderIds { get; set; } = EmptyMap.Instance;
    }

    public sealed class SeriesInfo
    {
        public string Name { get; set; }
        /// <summary>Kept as text: positions can be "2", "2.5" or "I".</summary>
        public string Position { get; set; }
        /// <summary>The Atlas id of the series record when Atlas names it (relations); null otherwise.</summary>
        public string Id { get; set; }
    }

    /// <summary>One Atlas record (a game or a book or anything Atlas adds later). Fields a media type does not have are null or empty.</summary>
    public sealed class AtlasRecord
    {
        public string Id { get; set; }
        public long? Version { get; set; }
        public string MediaType { get; set; }
        public MediaTypeKind MediaTypeKind => MediaTypes.Parse(MediaType);
        public string Title { get; set; }
        public string Description { get; set; }
        public string Developer { get; set; }
        public string Publisher { get; set; }
        public IReadOnlyList<Author> Authors { get; set; } = Array.Empty<Author>();
        /// <summary>Topic words: <c>subjects</c>, else <c>genres</c>, else (books) <c>features</c>, where Atlas keeps Open Library subjects.</summary>
        public IReadOnlyList<string> Subjects { get; set; } = Array.Empty<string>();
        public IReadOnlyList<Edition> Editions { get; set; } = Array.Empty<Edition>();
        /// <summary>From <c>series</c> or <c>facts.series</c>, else the first <c>relations.members_of</c> entry of type <c>series_member</c>.</summary>
        public SeriesInfo Series { get; set; }
        /// <summary>The first ISBN-13 Atlas knows: <c>provider_ids.isbn</c>, else the first edition's <c>isbn_13</c>. Null for non-books.</summary>
        public string Isbn
        {
            get
            {
                string v;
                if (ProviderIds.TryGetValue("isbn", out v) && !string.IsNullOrWhiteSpace(v)) return v.Trim();
                foreach (var e in Editions) if (!string.IsNullOrWhiteSpace(e.Isbn13)) return e.Isbn13.Trim();
                return null;
            }
        }
        /// <summary>The first release at the precision Atlas can prove; null when Atlas has no date.</summary>
        public PartialDate? FirstRelease { get; set; }
        public IReadOnlyDictionary<string, string> ProviderIds { get; set; } = EmptyMap.Instance;
        public IReadOnlyList<string> Sources { get; set; } = Array.Empty<string>();
        /// <summary>Set when the requested id was merged away: store this record's <see cref="Id"/> instead.</summary>
        public string RedirectedFrom { get; set; }
        /// <summary>True when this record is the answer for <paramref name="requestedId"/>: its own id, or the merged-away id it was asked by.</summary>
        public bool Answers(string requestedId)
        {
            return !string.IsNullOrEmpty(requestedId)
                   && (string.Equals(Id, requestedId, StringComparison.Ordinal) || string.Equals(RedirectedFrom, requestedId, StringComparison.Ordinal));
        }
        /// <summary>For achievements: false means the type has none (books). Null when Atlas did not say.</summary>
        public bool? AchievementsApplicable { get; set; }
        public string UpdatedAt { get; set; }
        /// <summary>The whole tree, so a connector can read a member this client does not model yet.</summary>
        public JsonNode Raw { get; set; }
    }

    public sealed class SearchPage
    {
        public IReadOnlyList<AtlasRecord> Results { get; set; } = Array.Empty<AtlasRecord>();
        public long? Total { get; set; }
        public long? Limit { get; set; }
        public long? Offset { get; set; }
    }

    public sealed class ClientApiInfo
    {
        public string Version { get; set; }
        public string MinSupported { get; set; }
        public IReadOnlyList<string> Capabilities { get; set; } = Array.Empty<string>();
        public IReadOnlyList<string> Deprecations { get; set; } = Array.Empty<string>();
        /// <summary>The major part of <see cref="Version"/>, or null when it does not parse.</summary>
        public int? Major
        {
            get
            {
                if (Version == null) return null;
                var dot = Version.IndexOf('.');
                int m;
                return int.TryParse(dot < 0 ? Version : Version.Substring(0, dot), NumberStyles.None, CultureInfo.InvariantCulture, out m) ? (int?)m : null;
            }
        }
    }

    public sealed class SiteInfo
    {
        public string Name { get; set; }
        public string Role { get; set; }
        public string Kind { get; set; }
        public string NodeId { get; set; }
        public IReadOnlyList<string> MediaTypes { get; set; } = Array.Empty<string>();
        /// <summary>The general /api/v2 marker (stays 1). Connectors do not branch on it; they read <see cref="ClientApi"/>.</summary>
        public long? Contract { get; set; }
        /// <summary>Null on an Atlas older than client-api (before Atlas PR A24).</summary>
        public ClientApiInfo ClientApi { get; set; }
    }

    public sealed class ResolveItem
    {
        public string Namespace { get; set; }
        public string ExternalId { get; set; }
        public string MediaType { get; set; }
        public string Title { get; set; }
        public string Platform { get; set; }
        public string ReleaseDate { get; set; }
        public string Developer { get; set; }
        /// <summary>Whatever ids the host already knows (Steam app id, ISBN, Tmdb...). Identity first: send these before a title.</summary>
        public IDictionary<string, string> ProviderIds { get; set; }
    }

    public sealed class ResolveCandidate
    {
        public string Id { get; set; }
        public double? Score { get; set; }
    }

    public sealed class ResolveResult
    {
        public string CanonicalId { get; set; }
        public string Method { get; set; }
        public string Reason { get; set; }
        public string Tier { get; set; }
        /// <summary>Atlas's own view of whether the result may be assigned. Null on an Atlas that does not send it.</summary>
        public bool? Assigned { get; set; }
        public double? Confidence { get; set; }
        public IReadOnlyList<ResolveCandidate> Candidates { get; set; } = Array.Empty<ResolveCandidate>();
        public AtlasRecord Record { get; set; }
        public string RedirectedFrom { get; set; }

        /// <summary>
        /// True only for the identity reasons a client may assign by itself: id, redirect, crosswalk (CLIENT-CONTRACT section 5),
        /// when a canonical id exists and Atlas does not say <c>assigned: false</c>. Everything else is for the user to choose,
        /// including <c>exact</c>: Atlas uses it for an exact <b>title</b> match (title evidence, atlas/resolve.py step 2), and a
        /// title is never an automatic match for a connector (CONTRACT-C15 section 8.4).
        /// An Atlas that sends no <c>reason</c> (older) is judged by <c>method</c> with the same list.
        /// </summary>
        public bool CanAutoAssign
        {
            get
            {
                if (string.IsNullOrEmpty(CanonicalId) || Assigned == false) return false;
                var why = Reason ?? Method;
                return why == "id" || why == "redirect" || why == "crosswalk";
            }
        }
    }

    public sealed class RelationNode
    {
        public string Id { get; set; }
        public string MediaType { get; set; }
        public string Title { get; set; }
    }

    public sealed class RelationEdge
    {
        public string Source { get; set; }
        public string Target { get; set; }
        public string Type { get; set; }
        /// <summary>Series order for series_member edges (text, as in <see cref="SeriesInfo.Position"/>).</summary>
        public string Position { get; set; }
        public JsonNode Raw { get; set; }
    }

    public sealed class RelationGraph
    {
        public string Id { get; set; }
        public string RedirectedFrom { get; set; }
        public IReadOnlyList<RelationNode> Nodes { get; set; } = Array.Empty<RelationNode>();
        public IReadOnlyList<RelationEdge> Edges { get; set; } = Array.Empty<RelationEdge>();
        public IReadOnlyList<Edition> Editions { get; set; } = Array.Empty<Edition>();
        public bool Truncated { get; set; }
    }

    public sealed class Artwork
    {
        public string Id { get; set; }
        /// <summary>cover, background, icon, logo, poster... an unknown kind is kept as is.</summary>
        public string Kind { get; set; }
        public string Url { get; set; }
        public long? Width { get; set; }
        public long? Height { get; set; }
        public string Source { get; set; }
        public bool Preferred { get; set; }
        public bool Cached { get; set; }
        /// <summary>Public immutable bytes (<c>/media/{digest}</c>) when cached; prefer this over <see cref="ImageUrl"/>.</summary>
        public string MediaUrl { get; set; }
        /// <summary>Needs the read token and may answer a redirect to the provider's host.</summary>
        public string ImageUrl { get; set; }
    }

    public sealed class ArtworkList
    {
        public string Id { get; set; }
        public string RedirectedFrom { get; set; }
        public IReadOnlyList<Artwork> Items { get; set; } = Array.Empty<Artwork>();
        /// <summary>Role (cover, background...) to the id of the artwork Atlas ranks first for it (A24). Empty before.</summary>
        public IReadOnlyDictionary<string, string> PrimaryByRole { get; set; } = EmptyMap.Instance;
    }

    public sealed class ImageData
    {
        public byte[] Bytes { get; set; }
        public string ContentType { get; set; }
    }

    /// <summary>A conditional GET: either <see cref="NotModified"/> (keep the cached copy) or a record and its ETag.</summary>
    public sealed class RecordResponse
    {
        public bool NotModified { get; set; }
        public AtlasRecord Record { get; set; }
        public string ETag { get; set; }
    }

    /// <summary>
    /// ISBN text as hosts store it ("0-00-000011-6", "978 0 00 000011 8", "urn:isbn:...") to the bare ISBN-13 Atlas keys its
    /// <c>isbn</c> crosswalk by (C15 §2.5). Null when the text is not a valid ISBN-10 or ISBN-13 (the check digit is verified).
    /// </summary>
    public static class IsbnText
    {
        public static string ToIsbn13(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var t = text.Trim();
            if (t.StartsWith("urn:isbn:", StringComparison.OrdinalIgnoreCase)) t = t.Substring(9);
            else if (t.StartsWith("isbn", StringComparison.OrdinalIgnoreCase)) t = t.Substring(4).TrimStart(':', ' ', '-');
            var chars = new List<char>();
            foreach (var c in t)
            {
                if (c >= '0' && c <= '9') chars.Add(c);
                else if (c == 'X' || c == 'x') chars.Add('X');
                else if (c != '-' && c != ' ') return null;
            }
            var s = new string(chars.ToArray());
            if (s.Length == 10)
            {
                var sum = 0;
                for (var i = 0; i < 10; i++)
                {
                    int d;
                    if (s[i] == 'X') { if (i != 9) return null; d = 10; }
                    else d = s[i] - '0';
                    sum += d * (10 - i);
                }
                if (sum % 11 != 0) return null;
                var body = "978" + s.Substring(0, 9);
                return body + Check13(body);
            }
            if (s.Length == 13 && s.IndexOf('X') < 0 && (s.StartsWith("978", StringComparison.Ordinal) || s.StartsWith("979", StringComparison.Ordinal)))
                return Check13(s.Substring(0, 12)) == s[12] ? s : null;
            return null;
        }

        private static char Check13(string twelve)
        {
            var sum = 0;
            for (var i = 0; i < 12; i++) sum += (twelve[i] - '0') * (i % 2 == 0 ? 1 : 3);
            return (char)('0' + (10 - sum % 10) % 10);
        }
    }

    internal static class EmptyMap
    {
        public static readonly IReadOnlyDictionary<string, string> Instance = new Dictionary<string, string>();
    }
}
