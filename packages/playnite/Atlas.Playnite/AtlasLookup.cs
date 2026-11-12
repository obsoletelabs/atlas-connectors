using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Atlas.Client;

namespace Atlas.Playnite
{
    /// <summary>What Playnite knows about one game. Identity first: ids before title.</summary>
    public sealed class GameQuery
    {
        public string Name { get; set; }
        public int? Year { get; set; }
        /// <summary>Playnite's own game id (stable per library entry).</summary>
        public Guid PlayniteGameId { get; set; }
        /// <summary>The library plugin that imported the game, and its id for the game.</summary>
        public Guid LibraryPluginId { get; set; }
        public string LibraryGameId { get; set; }
        /// <summary>From the "Atlas" link written by an earlier run, when present.</summary>
        public string KnownAtlasId { get; set; }
    }

    public sealed class Candidate
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Detail { get; set; }
    }

    public enum LookupOutcome { Found, Candidates, NotFound, Unavailable }

    public sealed class LookupResult
    {
        public LookupOutcome Outcome { get; set; }
        public AtlasRecord Record { get; set; }
        /// <summary>True when Atlas could not be reached and a record within the cache TTL was served.</summary>
        public bool FromCache { get; set; }
        /// <summary>Set when the stored Atlas id was merged away: the link must be rewritten to <see cref="AtlasRecord.Id"/>.</summary>
        public bool IdChanged { get; set; }
        public IReadOnlyList<Candidate> Candidates { get; set; } = new Candidate[0];
        public string Message { get; set; }

        public static LookupResult Unavailable(string message) { return new LookupResult { Outcome = LookupOutcome.Unavailable, Message = message }; }
    }

    /// <summary>
    /// Finds the Atlas record for a Playnite game and fetches its images. No Playnite UI here, so the fake-HTTP tests can drive it.
    /// Never writes to Atlas. Network trouble is a result (<see cref="LookupOutcome.Unavailable"/>), not an exception, because
    /// Playnite would otherwise log a stack trace for every game of a bulk download while the server is down.
    /// </summary>
    public sealed class AtlasLookup
    {
        private readonly AtlasClient _client;
        private readonly RecordCache _cache;
        private readonly TimeSpan _ttl;
        private readonly Action<string> _log;

        public AtlasLookup(AtlasClient client, RecordCache cache, TimeSpan ttl, Action<string> log = null)
        {
            _client = client; _cache = cache; _ttl = ttl; _log = log ?? (s => { });
        }

        public async Task<LookupResult> FindAsync(GameQuery q, CancellationToken ct)
        {
            var key = IdentityKey(q);
            try
            {
                if (!string.IsNullOrEmpty(q.KnownAtlasId))
                {
                    var known = await RecordByIdAsync(q.KnownAtlasId, ct).ConfigureAwait(false);
                    if (known != null) { _cache.PutIdentity(key, known.Record.Id); return known; }
                }
                return await ResolveAsync(q, key, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (IsOutage(e))
            {
                return Offline(q, key, e);
            }
            catch (AtlasIncompatibleException e) { return LookupResult.Unavailable(e.Message); }
            catch (AtlasWireUnsupportedException e) { return LookupResult.Unavailable(e.Detail ?? e.Message); }
            catch (AtlasApiException e) when (e.Status == 401 || e.Status == 403)
            {
                return LookupResult.Unavailable("Atlas refused the token (" + e.Status.ToString(CultureInfo.InvariantCulture) + "). Pair Playnite again in the Atlas settings.");
            }
            catch (AtlasException e) { return LookupResult.Unavailable(e.Message); }
        }

        public async Task<LookupResult> ByIdAsync(string atlasId, CancellationToken ct)
        {
            try
            {
                return await RecordByIdAsync(atlasId, ct).ConfigureAwait(false) ?? new LookupResult { Outcome = LookupOutcome.NotFound };
            }
            catch (Exception e) when (IsOutage(e))
            {
                var c = _cache.GetRecord(atlasId);
                if (_cache.IsFresh(c, _ttl)) return new LookupResult { Outcome = LookupOutcome.Found, Record = c.Record, FromCache = true };
                return LookupResult.Unavailable(e.Message);
            }
            catch (AtlasException e) { return LookupResult.Unavailable(e.Message); }
        }

        // ---- record by Atlas id ---------------------------------------------------------------------------------

        private async Task<LookupResult> RecordByIdAsync(string id, CancellationToken ct)
        {
            var cached = _cache.GetRecord(id);
            RecordResponse resp;
            try { resp = await _client.GetRecordAsync(id, cached != null ? cached.ETag : null, ct).ConfigureAwait(false); }
            catch (AtlasApiException e) when (e.Status == 404) { return null; }
            AtlasRecord rec;
            if (resp.NotModified && cached != null) { rec = cached.Record; _cache.PutRecord(rec, cached.ETag); }
            else if (resp.Record != null) { rec = resp.Record; _cache.PutRecord(rec, resp.ETag); }
            else return null;
            return Found(rec, id);
        }

        private static LookupResult Found(AtlasRecord rec, string asked)
        {
            return new LookupResult
            {
                Outcome = LookupOutcome.Found,
                Record = rec,
                IdChanged = !string.IsNullOrEmpty(rec.Id) && !string.Equals(rec.Id, asked, StringComparison.Ordinal),
            };
        }

        // ---- identity first, title second -----------------------------------------------------------------------

        private async Task<LookupResult> ResolveAsync(GameQuery q, string key, CancellationToken ct)
        {
            var item = ToItem(q);
            var answers = await _client.ResolveAsync(new[] { item }, true, ct).ConfigureAwait(false);
            var a = answers.Count > 0 ? answers[0] : null;
            if (a == null) return new LookupResult { Outcome = LookupOutcome.NotFound };

            if (a.CanAutoAssign)
            {
                var rec = a.Record;
                if (rec == null || string.IsNullOrEmpty(rec.Id)) { var one = await RecordByIdAsync(a.CanonicalId, ct).ConfigureAwait(false); rec = one != null ? one.Record : null; }
                else _cache.PutRecord(rec, null);
                if (rec != null && IsGame(rec))
                {
                    _cache.PutIdentity(key, rec.Id);
                    var changed = !string.IsNullOrEmpty(a.RedirectedFrom) || !string.IsNullOrEmpty(rec.RedirectedFrom);
                    return new LookupResult { Outcome = LookupOutcome.Found, Record = rec, IdChanged = changed };
                }
            }

            // Not safe to assign alone. Offer candidates; the user decides (CLIENT-CONTRACT 5). An exact *title* match comes back
            // as reason "exact" with a canonical id and possibly no candidate list: that id is offered first, never applied.
            var offered = new List<string>();
            if (!string.IsNullOrEmpty(a.CanonicalId)) offered.Add(a.CanonicalId);
            offered.AddRange(a.Candidates.Where(c => !string.IsNullOrEmpty(c.Id)).Select(c => c.Id));
            var ids = offered.Distinct(StringComparer.Ordinal).Take(5).ToList();
            if (ids.Count == 0) return new LookupResult { Outcome = LookupOutcome.NotFound };
            var records = await _client.GetRecordsAsync(ids, ct).ConfigureAwait(false);
            var list = new List<Candidate>();
            foreach (var id in ids)
            {
                var r = records.FirstOrDefault(x => x.Id == id);
                if (r == null || !IsGame(r)) continue;
                list.Add(new Candidate { Id = r.Id, Title = r.Title, Detail = r.FirstRelease.HasValue ? r.FirstRelease.Value.ToString() : null });
            }
            return list.Count == 0 ? new LookupResult { Outcome = LookupOutcome.NotFound } : new LookupResult { Outcome = LookupOutcome.Candidates, Candidates = list };
        }

        /// <summary>Title search for the manual picker. Only games; an outage gives an empty list.</summary>
        public async Task<IReadOnlyList<Candidate>> SearchAsync(string text, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(text)) return new Candidate[0];
            try
            {
                var page = await _client.SearchAsync(text.Trim().Length > 200 ? text.Trim().Substring(0, 200) : text.Trim(), "game", 20, 0, ct: ct).ConfigureAwait(false);
                return page.Results.Where(IsGame).Select(r => new Candidate
                {
                    Id = r.Id, Title = r.Title, Detail = r.FirstRelease.HasValue ? r.FirstRelease.Value.ToString() : null,
                }).ToList();
            }
            catch (AtlasException e) { _log("Search failed: " + e.Message); return new Candidate[0]; }
        }

        /// <summary>Called after the user picked a candidate: remember the choice for next time.</summary>
        public async Task<LookupResult> ChooseAsync(GameQuery q, Candidate chosen, CancellationToken ct)
        {
            var r = await ByIdAsync(chosen.Id, ct).ConfigureAwait(false);
            if (r.Outcome == LookupOutcome.Found) _cache.PutIdentity(IdentityKey(q), r.Record.Id);
            return r;
        }

        internal static ResolveItem ToItem(GameQuery q)
        {
            var mapped = LibraryIds.Map(q.LibraryPluginId, q.LibraryGameId);
            var item = new ResolveItem
            {
                MediaType = "game",
                Title = q.Name,
                ReleaseDate = q.Year.HasValue ? q.Year.Value.ToString("0000", CultureInfo.InvariantCulture) : null,
            };
            if (mapped != null)
            {
                item.Namespace = mapped.Item1; item.ExternalId = mapped.Item2;
                item.ProviderIds = new Dictionary<string, string> { { mapped.Item1, mapped.Item2 } };
            }
            else
            {
                item.Namespace = LibraryIds.PlayniteNamespace; item.ExternalId = q.PlayniteGameId.ToString("D");
            }
            return item;
        }

        internal static string IdentityKey(GameQuery q)
        {
            var mapped = LibraryIds.Map(q.LibraryPluginId, q.LibraryGameId);
            return mapped != null ? mapped.Item1 + ":" + mapped.Item2 : LibraryIds.PlayniteNamespace + ":" + q.PlayniteGameId.ToString("D");
        }

        private static bool IsGame(AtlasRecord r) { return r.MediaTypeKind == MediaTypeKind.Game; }

        // ---- offline --------------------------------------------------------------------------------------------

        private static bool IsOutage(Exception e)
        {
            if (e is AtlasNetworkException || e is AtlasRateLimitedException) return true;
            var api = e as AtlasApiException;
            return api != null && api.Status >= 500;
        }

        private LookupResult Offline(GameQuery q, string key, Exception why)
        {
            var id = q.KnownAtlasId ?? _cache.GetIdentity(key);
            var c = id == null ? null : _cache.GetRecord(id);
            if (_cache.IsFresh(c, _ttl))
            {
                _log("Atlas is unreachable (" + why.Message + "); serving the cached record for " + id + ".");
                return new LookupResult { Outcome = LookupOutcome.Found, Record = c.Record, FromCache = true };
            }
            return LookupResult.Unavailable("Atlas is unreachable and there is no recent cached record: " + why.Message);
        }

        // ---- images ---------------------------------------------------------------------------------------------

        public async Task<ArtworkList> ArtworkAsync(string atlasId, CancellationToken ct)
        {
            try { return await _client.GetArtworkAsync(atlasId, ct).ConfigureAwait(false); }
            catch (AtlasException e) { _log("No artwork list for " + atlasId + ": " + e.Message); return null; }
        }

        public async Task<ImageData> ImageAsync(Artwork art, CancellationToken ct)
        {
            if (art == null) return null;
            try { return await _client.DownloadArtworkAsync(art, ct).ConfigureAwait(false); }
            catch (AtlasException e) { _log("Image " + art.Id + " not downloaded: " + e.Message); return null; }
        }

        /// <summary>
        /// The artwork Atlas ranks first for a role (<c>primary_by_role</c>, client-api 1.0), else the one flagged preferred, else the
        /// first of that kind.
        /// <paramref name="kinds"/> are tried in order (cover then poster). An unknown kind is never picked.
        /// </summary>
        public static Artwork Pick(ArtworkList list, params string[] kinds)
        {
            if (list == null) return null;
            foreach (var kind in kinds)
            {
                string pid;
                if (list.PrimaryByRole.TryGetValue(kind, out pid))
                {
                    var hit = list.Items.FirstOrDefault(a => a.Id == pid);
                    if (hit != null) return hit;
                }
                var of = list.Items.Where(a => string.Equals(a.Kind, kind, StringComparison.OrdinalIgnoreCase)).ToList();
                var chosen = of.FirstOrDefault(a => a.Preferred) ?? of.FirstOrDefault();
                if (chosen != null) return chosen;
            }
            return null;
        }
    }
}
