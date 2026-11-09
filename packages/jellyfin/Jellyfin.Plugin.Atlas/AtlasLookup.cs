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
    /// <summary>Jellyfin provider id key to the Atlas <c>provider_ids</c> namespace. PROVISIONAL: the namespaces are not confirmed against a live Atlas.</summary>
    public static readonly IReadOnlyDictionary<string, string> Namespaces = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        { "Isbn", "isbn" },
        { "Tmdb", "tmdb" },
        { "Imdb", "imdb" },
        { "Tvdb", "tvdb" },
        { "AniList", "anilist" },
    };

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
            if (!string.IsNullOrWhiteSpace(kv.Value) && Namespaces.TryGetValue(kv.Key, out var ns))
            {
                map[ns] = kv.Value.Trim();
            }
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
            return new LookupResult { Outcome = LookupOutcome.NotFound };
        }

        var records = (await _client.GetRecordsAsync(cand, ct).ConfigureAwait(false)).Where(r => Accepts(q.Kind, r.MediaTypeKind)).ToList();
        return records.Count == 0
            ? new LookupResult { Outcome = LookupOutcome.NotFound }
            : new LookupResult { Outcome = LookupOutcome.Candidates, Candidates = records };
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

        return Uri.TryCreate(u, UriKind.Absolute, out var abs) ? abs.AbsoluteUri : new Uri(baseUri, u.TrimStart('/')).AbsoluteUri;
    }
}
