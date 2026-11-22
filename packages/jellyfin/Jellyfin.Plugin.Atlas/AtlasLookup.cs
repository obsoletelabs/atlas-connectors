using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Atlas.Client;

namespace Jellyfin.Plugin.Atlas;

public enum ItemKind
{
    Book,
    Movie,
    Series,
}

/// <summary>What Jellyfin knows about one item. Identity first: ids before title.</summary>
public sealed class ItemQuery
{
    public ItemKind Kind { get; set; }

    public string Name { get; set; }

    public int? Year { get; set; }

    /// <summary>Jellyfin <c>ProviderIds</c> (keys such as Atlas, Isbn, Tmdb, Imdb, Tvdb, AniList).</summary>
    public IDictionary<string, string> ProviderIds { get; set; } = new Dictionary<string, string>();
}

public enum LookupOutcome
{
    Found,
    Candidates,
    NotFound,
    Unavailable,
}

public sealed class LookupResult
{
    public LookupOutcome Outcome { get; set; }

    public AtlasRecord Record { get; set; }

    public bool FromCache { get; set; }

    /// <summary>The stored Atlas id was merged away: the item's Atlas id must become <see cref="AtlasRecord.Id"/>.</summary>
    public bool IdChanged { get; set; }

    public IReadOnlyList<AtlasRecord> Candidates { get; set; } = Array.Empty<AtlasRecord>();

    public string Message { get; set; }

    public static LookupResult Unavailable(string message) => new LookupResult { Outcome = LookupOutcome.Unavailable, Message = message };
}

/// <summary>
/// Finds the Atlas record for a Jellyfin item. No Jellyfin types, so fake-HTTP tests can drive it. Never writes to Atlas. Network
/// trouble is a result, not an exception: a library scan must not log a stack trace per item while Atlas is down.
/// A title alone is never enough to apply a record (CLIENT-CONTRACT 5): it yields candidates for the Identify dialog only.
/// </summary>
public sealed class AtlasLookup
{
    /// <summary>
    /// Jellyfin provider id key to the Atlas <c>provider_ids</c> namespace sent to <c>/resolve</c>. <c>isbn</c> is the Atlas crosswalk
    /// namespace of ISBN-13s (C15 §2.5, Atlas <c>books.ISBN_NAMESPACE</c>): it covers edition ISBNs and work keys alike, but only as a bare
    /// ISBN-13, so the ISBN is normalised first (<see cref="IsbnText.ToIsbn13"/>). When resolve matches nothing, a book's ISBN is also tried
    /// as <c>GET /games?q=&lt;isbn&gt;&amp;type=book</c>, which the client-api 1.0 OpenAPI defines to answer that book alone.
    /// <c>tmdb</c>, <c>imdb</c>, <c>tvdb</c> and <c>anilist</c> are still PROVISIONAL: Atlas a6bbc2a has no provider writing those namespaces.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Namespaces = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        { "Isbn", "isbn" },
        { "Tmdb", "tmdb" },
        { "Imdb", "imdb" },
        { "Tvdb", "tvdb" },
        { "AniList", "anilist" },
    };

    public const string IsbnNamespace = "isbn";

    private readonly AtlasClient _client;
    private readonly RecordCache _cache;
    private readonly TimeSpan _ttl;
    private readonly Action<string> _log;

    public AtlasLookup(AtlasClient client, RecordCache cache, TimeSpan ttl, Action<string> log = null)
    {
        _client = client;
        _cache = cache;
        _ttl = ttl;
        _log = log ?? (s => { });
    }

    public static bool Accepts(ItemKind kind, MediaTypeKind media)
    {
        switch (kind)
        {
            case ItemKind.Book: return media == MediaTypeKind.Book;
            case ItemKind.Movie: return media == MediaTypeKind.Movie;
            default: return media == MediaTypeKind.TvShow || media == MediaTypeKind.Series || media == MediaTypeKind.Anime;
        }
    }

    public static string AtlasIdOf(IDictionary<string, string> ids)
    {
        if (ids == null)
        {
            return null;
        }

        foreach (var kv in ids)
        {
            if (string.Equals(kv.Key, ItemMapper.AtlasKey, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(kv.Value))
            {
                return kv.Value.Trim();
            }
        }

        return null;
    }

    /// <summary>The ids Atlas knows a namespace for, as <c>provider_ids</c>.</summary>
    public static Dictionary<string, string> MappedIds(IDictionary<string, string> ids)
    {
        var map = new Dictionary<string, string>();
        if (ids == null)
        {
            return map;
        }

        foreach (var kv in ids)
        {
            if (string.IsNullOrWhiteSpace(kv.Value) || !Namespaces.TryGetValue(kv.Key, out var ns))
            {
                continue;
            }

            if (ns == IsbnNamespace)
            {
                // Atlas keys ISBNs as bare ISBN-13s: "0-00-000010-8" would never match, and an invalid ISBN is no identity at all
                var isbn = IsbnText.ToIsbn13(kv.Value);
                if (isbn != null)
                {
                    map[ns] = isbn;
                }

                continue;
            }

            map[ns] = kv.Value.Trim();
        }

        return map;
    }

    /// <summary>Metadata download: the stored Atlas id, else the known external ids. Never a title-only automatic match.</summary>
    public async Task<LookupResult> FindAsync(ItemQuery q, CancellationToken ct)
    {
        var known = AtlasIdOf(q.ProviderIds);
        try
        {
            if (known != null)
            {
                var r = await RecordByIdAsync(q.Kind, known, ct).ConfigureAwait(false);
                if (r != null)
                {
                    return r;
                }
            }

            var ids = MappedIds(q.ProviderIds);
            if (ids.Count == 0)
            {
                return new LookupResult { Outcome = LookupOutcome.NotFound, Message = "No id Atlas understands; use Identify to pick a match." };
            }

            return await ResolveAsync(q, ids, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (IsOutage(e))
        {
            return Offline(known, e);
        }
        catch (AtlasIncompatibleException e)
        {
            return LookupResult.Unavailable(e.Message);
        }
        catch (AtlasWireUnsupportedException e)
        {
            return LookupResult.Unavailable(e.Detail ?? e.Message);
        }
        catch (AtlasApiException e) when (e.Status == 401 || e.Status == 403)
        {
            return LookupResult.Unavailable("Atlas refused the token (" + e.Status.ToString(CultureInfo.InvariantCulture) + "). Pair again on the Atlas plugin page.");
        }
        catch (AtlasException e)
        {
            return LookupResult.Unavailable(e.Message);
        }
    }

    /// <summary>Manual search (Identify): the record for a known id, else resolve candidates, then a title search. Only the right media type.</summary>
    public async Task<IReadOnlyList<AtlasRecord>> SearchAsync(ItemQuery q, CancellationToken ct)
    {
        var found = new List<AtlasRecord>();
        try
        {
            var known = AtlasIdOf(q.ProviderIds);
            if (known != null)
            {
                var r = await RecordByIdAsync(q.Kind, known, ct).ConfigureAwait(false);
                if (r != null && r.Record != null)
                {
                    found.Add(r.Record);
                }
            }

            var ids = MappedIds(q.ProviderIds);
            if (ids.Count > 0)
            {
                var r = await ResolveAsync(q, ids, ct).ConfigureAwait(false);
                if (r.Record != null)
                {
                    found.Add(r.Record);
                }

                found.AddRange(r.Candidates);
            }

            if (q.Kind == ItemKind.Book && ids.TryGetValue(IsbnNamespace, out var isbn))
            {
                var hit = await _client.SearchAsync(isbn, "book", 2, 0, ct: ct).ConfigureAwait(false);
                found.AddRange(hit.Results);
            }

            if (!string.IsNullOrWhiteSpace(q.Name))
            {
                var text = q.Name.Trim().Length > 200 ? q.Name.Trim().Substring(0, 200) : q.Name.Trim();
                var page = await _client.SearchAsync(text, MediaTypeFor(q.Kind), 20, 0, ct: ct).ConfigureAwait(false);
                found.AddRange(page.Results);
            }
        }
        catch (AtlasException e)
        {
            _log("Atlas search failed: " + e.Message);
        }

        return found.Where(r => !string.IsNullOrEmpty(r.Id) && Accepts(q.Kind, r.MediaTypeKind))
            .GroupBy(r => r.Id).Select(g => g.First()).Take(20).ToList();
    }

    private static string MediaTypeFor(ItemKind k) => k == ItemKind.Book ? "book" : k == ItemKind.Movie ? "movie" : null;

    private async Task<LookupResult> RecordByIdAsync(ItemKind kind, string id, CancellationToken ct)
    {
        var cached = _cache.GetRecord(id);
        RecordResponse resp;
        try
        {
            resp = await _client.GetRecordAsync(id, cached?.ETag, ct).ConfigureAwait(false);
        }
        catch (AtlasApiException e) when (e.Status == 404)
        {
            return null;
        }

        AtlasRecord rec;
        if (resp.NotModified && cached != null)
        {
            rec = cached.Record;
            _cache.PutRecord(rec, cached.ETag);
        }
        else if (resp.Record != null)
        {
            rec = resp.Record;
            _cache.PutRecord(rec, resp.ETag);
        }
        else
        {
            return null;
        }

        if (!Accepts(kind, rec.MediaTypeKind))
        {
            return new LookupResult { Outcome = LookupOutcome.NotFound, Message = "The stored Atlas id belongs to a different media type." };
        }

        return new LookupResult
        {
            Outcome = LookupOutcome.Found,
            Record = rec,
            IdChanged = !string.IsNullOrEmpty(rec.Id) && !string.Equals(rec.Id, id, StringComparison.Ordinal),
        };
    }

    private async Task<LookupResult> ResolveAsync(ItemQuery q, Dictionary<string, string> ids, CancellationToken ct)
    {
        var first = ids.First();
        var item = new ResolveItem
        {
            MediaType = MediaTypeFor(q.Kind),
            Title = q.Name,
            ReleaseDate = q.Year.HasValue ? q.Year.Value.ToString("0000", CultureInfo.InvariantCulture) : null,
            Namespace = first.Key,
            ExternalId = first.Value,
            ProviderIds = ids,
        };
        var answers = await _client.ResolveAsync(new[] { item }, true, ct).ConfigureAwait(false);
        var a = answers.Count > 0 ? answers[0] : null;
        if (a == null)
        {
            return new LookupResult { Outcome = LookupOutcome.NotFound };
        }

        if (a.CanAutoAssign)
        {
            var rec = a.Record;
            if (rec == null || string.IsNullOrEmpty(rec.Id))
            {
                var one = await RecordByIdAsync(q.Kind, a.CanonicalId, ct).ConfigureAwait(false);
                rec = one?.Record;
            }
            else
            {
                _cache.PutRecord(rec, null);
            }

            if (rec != null && Accepts(q.Kind, rec.MediaTypeKind))
            {
                return new LookupResult
                {
                    Outcome = LookupOutcome.Found,
                    Record = rec,
                    IdChanged = !string.IsNullOrEmpty(a.RedirectedFrom) || !string.IsNullOrEmpty(rec.RedirectedFrom),
                };
            }
        }

        var cand = a.Candidates.Where(c => !string.IsNullOrEmpty(c.Id)).Select(c => c.Id).Distinct().Take(5).ToList();
        if (cand.Count == 0)
        {
            // Nothing matched at all (not a conflict, not another type): a book's ISBN still names its work through the search
            if (string.IsNullOrEmpty(a.Reason) || a.Reason == "none")
            {
                var byIsbn = await IsbnSearchAsync(q, ids, ct).ConfigureAwait(false);
                if (byIsbn != null)
                {
                    return byIsbn;
                }
            }

            return new LookupResult { Outcome = LookupOutcome.NotFound };
        }

        // One survivor can answer several candidate ids (merges): list it once
        var records = (await _client.GetRecordsAsync(cand, ct).ConfigureAwait(false)).Where(r => Accepts(q.Kind, r.MediaTypeKind))
            .GroupBy(r => r.Id, StringComparer.Ordinal).Select(g => g.First()).ToList();
        return records.Count == 0
            ? new LookupResult { Outcome = LookupOutcome.NotFound }
            : new LookupResult { Outcome = LookupOutcome.Candidates, Candidates = records };
    }

    /// <summary>
    /// <c>GET /games?q=&lt;ISBN-13&gt;&amp;type=book</c>: Atlas answers the one work that edition belongs to, or nothing (OpenAPI <c>q</c>;
    /// fixture <c>books/search-isbn.json</c>). An ISBN is identity evidence, so a single book answer is a match. Anything else is not.
    /// </summary>
    private async Task<LookupResult> IsbnSearchAsync(ItemQuery q, Dictionary<string, string> ids, CancellationToken ct)
    {
        if (q.Kind != ItemKind.Book || !ids.TryGetValue(IsbnNamespace, out var isbn))
        {
            return null;
        }

        var page = await _client.SearchAsync(isbn, "book", 2, 0, ct: ct).ConfigureAwait(false);
        var books = page.Results.Where(r => !string.IsNullOrEmpty(r.Id) && Accepts(q.Kind, r.MediaTypeKind)).ToList();
        if (books.Count != 1)
        {
            return null;
        }

        _cache.PutRecord(books[0], null);
        return new LookupResult { Outcome = LookupOutcome.Found, Record = books[0] };
    }

    private static bool IsOutage(Exception e)
    {
        if (e is AtlasNetworkException || e is AtlasRateLimitedException)
        {
            return true;
        }

        return e is AtlasApiException api && api.Status >= 500;
    }

    private LookupResult Offline(string knownId, Exception why)
    {
        var c = knownId == null ? null : _cache.GetRecord(knownId);
        if (_cache.IsFresh(c, _ttl))
        {
            _log("Atlas is unreachable (" + why.Message + "); serving the cached record for " + knownId + ".");
            return new LookupResult { Outcome = LookupOutcome.Found, Record = c.Record, FromCache = true };
        }

        return LookupResult.Unavailable("Atlas is unreachable and there is no recent cached record: " + why.Message);
    }

    public async Task<ArtworkList> ArtworkAsync(string atlasId, CancellationToken ct)
    {
        try
        {
            return await _client.GetArtworkAsync(atlasId, ct).ConfigureAwait(false);
        }
        catch (AtlasException e)
        {
            _log("No artwork list for " + atlasId + ": " + e.Message);
            return null;
        }
    }

    public Task<ImageData> DownloadAsync(Artwork art, CancellationToken ct) => _client.DownloadArtworkAsync(art, ct);

    /// <summary>The artwork Atlas ranks first for a role (<c>primary</c>), else the one flagged preferred, else the first of that kind. Unknown kinds are never picked.</summary>
    public static Artwork Pick(ArtworkList list, params string[] kinds)
    {
        if (list == null)
        {
            return null;
        }

        foreach (var kind in kinds)
        {
            if (list.PrimaryByRole.TryGetValue(kind, out var pid))
            {
                var hit = list.Items.FirstOrDefault(a => a.Id == pid);
                if (hit != null)
                {
                    return hit;
                }
            }

            var of = list.Items.Where(a => string.Equals(a.Kind, kind, StringComparison.OrdinalIgnoreCase)).ToList();
            var chosen = of.FirstOrDefault(a => a.Preferred) ?? of.FirstOrDefault();
            if (chosen != null)
            {
                return chosen;
            }
        }

        return null;
    }

    /// <summary>The URL a Jellyfin image list will carry for an artwork. Never contains a token.</summary>
    public static string PublicUrlFor(Artwork a, Uri baseUri)
    {
        var u = !string.IsNullOrEmpty(a.MediaUrl) ? a.MediaUrl : !string.IsNullOrEmpty(a.ImageUrl) ? a.ImageUrl : null;
        if (u == null)
        {
            return new Uri(baseUri, "api/v2/artwork/" + Uri.EscapeDataString(a.Id) + "/image").AbsoluteUri;
        }

        // On Linux a path such as "/api/v2/media/x" parses as an absolute file:// URI, so only
        // http(s) URLs count as absolute; everything else is resolved against the Atlas base.
        return Uri.TryCreate(u, UriKind.Absolute, out var abs)
               && (abs.Scheme == Uri.UriSchemeHttp || abs.Scheme == Uri.UriSchemeHttps)
            ? abs.AbsoluteUri
            : new Uri(baseUri, u.TrimStart('/')).AbsoluteUri;
    }
}
