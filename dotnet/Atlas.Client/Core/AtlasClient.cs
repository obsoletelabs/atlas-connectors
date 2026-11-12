using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Atlas.Client
{
    /// <summary>
    /// Read-only client for the <c>client</c>-tagged subset of Atlas <c>/api/v2</c> (client-api v1). Compiled into each connector
    /// as source. Every await uses ConfigureAwait(false): a host may call this from a UI thread.
    /// </summary>
    public sealed partial class AtlasClient : IDisposable
    {
        private const long MaxJsonBytes = 64L * 1024 * 1024;
        private const int MaxBatch = 200;

        private readonly AtlasClientOptions _o;
        private readonly IJsonAdapter _json;
        private readonly HttpClient _http;
        private readonly Uri _origin;      // scheme://host:port/ (no path)
        private readonly string _prefix;   // path prefix of the node without trailing slash ("" or "/atlas")
        private readonly Uri _api;         // <base>/api/v2/
        private readonly string _userAgent;
        private SiteInfo _site;
        private bool _compatChecked;
        private WireInfo _lastWire;

        /// <param name="options">Settings; see <see cref="AtlasClientOptions"/>.</param>
        /// <param name="json">The host's serializer adapter.</param>
        /// <param name="handler">Optional (tests, or a host that wants its own proxy settings). It must not follow redirects by
        /// itself: this client follows the one redirect it allows (artwork bytes) manually, without the token. An
        /// <see cref="HttpClientHandler"/> is switched to AllowAutoRedirect=false when possible.</param>
        public AtlasClient(AtlasClientOptions options, IJsonAdapter json, HttpMessageHandler handler = null)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (json == null) throw new ArgumentNullException(nameof(json));
            var b = options.BaseUri;
            if (b == null || !b.IsAbsoluteUri) throw new ArgumentException("BaseUri must be an absolute URL.", nameof(options));
            if (!string.IsNullOrEmpty(b.UserInfo)) throw new ArgumentException("BaseUri must not contain credentials.", nameof(options));
            if (!string.IsNullOrEmpty(b.Query) || !string.IsNullOrEmpty(b.Fragment)) throw new ArgumentException("BaseUri must not have a query or fragment.", nameof(options));
            if (b.Scheme == Uri.UriSchemeHttps) { }
            else if (b.Scheme == Uri.UriSchemeHttp)
            {
                if (!b.IsLoopback && !options.AllowPlainHttp)
                    throw new ArgumentException("Plain http would send the token in clear text. Use https, or set AllowPlainHttp for a trusted LAN.", nameof(options));
            }
            else throw new ArgumentException("BaseUri must be http or https.", nameof(options));
            if (options.MaxRetries < 0) throw new ArgumentOutOfRangeException(nameof(options), "MaxRetries must not be negative.");
            var host = options.HostName ?? "other";
            foreach (var c in host) if (!(c >= 'a' && c <= 'z')) throw new ArgumentException("HostName must be lowercase letters, e.g. playnite.", nameof(options));

            _o = options;
            _json = json;
            _prefix = b.AbsolutePath.TrimEnd('/');
            _origin = new Uri(b.GetLeftPart(UriPartial.Authority) + "/");
            _api = new Uri(b.GetLeftPart(UriPartial.Authority) + _prefix + "/api/v2/");
            _userAgent = "atlas-connector-" + host + "/" + (options.HostVersion ?? "0.0.0");

            var h = handler;
            if (h == null) h = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
            else
            {
                var hc = h as HttpClientHandler;
                if (hc != null) { try { hc.AllowAutoRedirect = false; } catch (InvalidOperationException) { } }
            }
            _http = new HttpClient(h, handler == null) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        }

        public void Dispose() => _http.Dispose();

        /// <summary>What the last Atlas answer said about the wire version. Null before the first answer.</summary>
        public WireInfo LastWire => _lastWire;

        /// <summary>The User-Agent this client sends.</summary>
        public string UserAgent => _userAgent;

        // ---- site and compatibility ---------------------------------------------------------------------------

        /// <summary><c>GET /site</c> (public). Cached for the life of the client unless <paramref name="refresh"/>.</summary>
        public async Task<SiteInfo> GetSiteAsync(CancellationToken ct = default(CancellationToken), bool refresh = false)
        {
            if (_site != null && !refresh) return _site;
            var reply = await SendAsync(new Req { Path = "site", Auth = false }, ct).ConfigureAwait(false);
            var site = AtlasReader.ReadSite(Parse(reply));
            if (site == null) throw new AtlasProtocolException("Atlas answered /site with something that is not a JSON object.");
            _site = site;
            return site;
        }

        /// <summary>
        /// Read <c>/site</c> once and refuse, with a clear message, an Atlas whose <c>client_api</c> major differs or whose
        /// capabilities lack a required one. Called automatically before the first data request.
        /// </summary>
        public async Task<SiteInfo> EnsureCompatibleAsync(CancellationToken ct = default(CancellationToken))
        {
            var site = await GetSiteAsync(ct).ConfigureAwait(false);
            if (_compatChecked) return site;
            var ca = site.ClientApi;
            if (ca == null)
            {
                if (_o.RequireClientApi)
                    throw new AtlasIncompatibleException("This Atlas does not offer the client API (client_api is missing from /site). Update Atlas.");
                if (_o.RequiredCapabilities.Count > 0)
                    throw new AtlasIncompatibleException("This Atlas does not say which client capabilities it has, and this connector needs: "
                        + string.Join(", ", _o.RequiredCapabilities) + ". Update Atlas.", new List<string>(_o.RequiredCapabilities));
            }
            else
            {
                if (ca.Major != _o.ClientApiMajor)
                    throw new AtlasIncompatibleException("This Atlas speaks client API " + (ca.Version ?? "?") + ", this connector needs major "
                        + _o.ClientApiMajor.ToString(CultureInfo.InvariantCulture) + ". Update the connector or Atlas.");
                var missing = new List<string>();
                foreach (var need in _o.RequiredCapabilities) if (!Contains(ca.Capabilities, need)) missing.Add(need);
                if (missing.Count > 0)
                    throw new AtlasIncompatibleException("This Atlas lacks the capabilities this connector needs: " + string.Join(", ", missing) + ". Update Atlas.", missing);
            }
            _compatChecked = true;
            return site;
        }

        private static bool Contains(IReadOnlyList<string> list, string v)
        {
            foreach (var s in list) if (string.Equals(s, v, StringComparison.Ordinal)) return true;
            return false;
        }

        // ---- reading -------------------------------------------------------------------------------------------

        /// <summary><c>GET /games?q=...</c>. <paramref name="limit"/> is clamped to 1..100 (Atlas refuses more).</summary>
        public async Task<SearchPage> SearchAsync(string query, string mediaType = null, int limit = 25, int offset = 0,
            string author = null, string releasedFrom = null, string releasedTo = null, string sort = null,
            CancellationToken ct = default(CancellationToken))
        {
            if (string.IsNullOrWhiteSpace(query)) throw new ArgumentException("query must not be empty.", nameof(query));
            if (query.Length > 200) throw new ArgumentException("query is longer than the 200 characters Atlas accepts.", nameof(query));
            await EnsureCompatibleAsync(ct).ConfigureAwait(false);
            var sb = new StringBuilder("games?q=").Append(Uri.EscapeDataString(query));
            AppendParam(sb, "type", mediaType);
            AppendParam(sb, "author", author);
            AppendParam(sb, "released_from", releasedFrom);
            AppendParam(sb, "released_to", releasedTo);
            AppendParam(sb, "sort", sort);
            sb.Append("&limit=").Append(Math.Max(1, Math.Min(100, limit)).ToString(CultureInfo.InvariantCulture));
            sb.Append("&offset=").Append(Math.Max(0, offset).ToString(CultureInfo.InvariantCulture));
            var reply = await SendAsync(new Req { Path = sb.ToString(), Retryable = true }, ct).ConfigureAwait(false);
            return AtlasReader.ReadSearch(Parse(reply)) ?? new SearchPage();
        }

        /// <summary>
        /// <c>GET /games/{id}</c>. Pass the ETag you stored to get a cheap <see cref="RecordResponse.NotModified"/>
        /// (Atlas answers 304 with the <c>etag</c> capability; an older Atlas ignores the header and returns the record).
        /// When the id was merged, <see cref="AtlasRecord.RedirectedFrom"/> is set: store <see cref="AtlasRecord.Id"/> instead.
        /// </summary>
        public async Task<RecordResponse> GetRecordAsync(string id, string ifNoneMatch = null, CancellationToken ct = default(CancellationToken))
        {
            await EnsureCompatibleAsync(ct).ConfigureAwait(false);
            var reply = await SendAsync(new Req { Path = "games/" + Seg(id), Retryable = true, IfNoneMatch = ifNoneMatch, Allow304 = true }, ct).ConfigureAwait(false);
            if (reply.Status == 304) return new RecordResponse { NotModified = true, ETag = reply.ETag ?? ifNoneMatch };
            var rec = AtlasReader.ReadRecord(Parse(reply));
            if (rec == null) throw new AtlasProtocolException("Atlas answered a record request with something that is not a JSON object.");
            return new RecordResponse { Record = rec, ETag = reply.ETag };
        }

        /// <summary>
        /// Many records. With the <c>batch</c> capability this is <c>POST /games/batch</c> (at most 200 ids per call); on an Atlas
        /// without it, one GET per id. Ids Atlas does not know (<c>not_found</c>) are simply absent from the result; a merged id
        /// answers the surviving record (with <see cref="AtlasRecord.RedirectedFrom"/>), so two ids can give the same record.
        /// </summary>
        public async Task<IReadOnlyList<AtlasRecord>> GetRecordsAsync(IEnumerable<string> ids, CancellationToken ct = default(CancellationToken))
        {
            if (ids == null) throw new ArgumentNullException(nameof(ids));
            var site = await EnsureCompatibleAsync(ct).ConfigureAwait(false);
            var unique = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in ids) if (!string.IsNullOrEmpty(id) && seen.Add(id)) unique.Add(id);
            var all = new List<AtlasRecord>();
            var batch = site.ClientApi != null && Contains(site.ClientApi.Capabilities, "batch");
            if (batch)
            {
                for (var i = 0; i < unique.Count; i += MaxBatch)
                {
                    var chunk = unique.GetRange(i, Math.Min(MaxBatch, unique.Count - i));
                    var idNodes = new List<JsonNode>();
                    foreach (var c in chunk) idNodes.Add(JsonNode.Str(c));
                    var body = JsonNode.Object(new[] { new KeyValuePair<string, JsonNode>("ids", JsonNode.Array(idNodes)) });
                    var reply = await SendAsync(new Req { Method = HttpMethod.Post, Path = "games/batch", Body = body, Retryable = true }, ct).ConfigureAwait(false);
                    all.AddRange(AtlasReader.ReadBatch(Parse(reply)));
                }
            }
            else
            {
                foreach (var id in unique)
                {
                    try
                    {
                        var one = await GetRecordAsync(id, null, ct).ConfigureAwait(false);
                        if (one.Record != null) all.Add(one.Record);
                    }
                    catch (AtlasApiException e) when (e.Status == 404) { }
                }
            }
            return all;
        }

        /// <summary>
        /// <c>POST /resolve</c>, at most 200 items per request (longer input is split), results in input order.
        /// Identity first: put the ids the host knows in <see cref="ResolveItem.ProviderIds"/>; a title is only evidence.
        /// Auto-assign only when <see cref="ResolveResult.CanAutoAssign"/>; show everything else to the user.
        /// </summary>
        public async Task<IReadOnlyList<ResolveResult>> ResolveAsync(IEnumerable<ResolveItem> items, bool includeView = true, CancellationToken ct = default(CancellationToken))
        {
            if (items == null) throw new ArgumentNullException(nameof(items));
            await EnsureCompatibleAsync(ct).ConfigureAwait(false);
            var input = new List<ResolveItem>(items);
            var output = new List<ResolveResult>(input.Count);
            for (var i = 0; i < input.Count; i += MaxBatch)
            {
                var chunk = input.GetRange(i, Math.Min(MaxBatch, input.Count - i));
                var reply = await SendAsync(new Req
                {
                    Method = HttpMethod.Post, Path = "resolve", Body = AtlasReader.WriteResolveRequest(chunk, includeView), Retryable = true,
                }, ct).ConfigureAwait(false);
                var results = AtlasReader.ReadResolve(Parse(reply));
                if (results.Count != chunk.Count)
                    throw new AtlasProtocolException("Atlas answered " + results.Count.ToString(CultureInfo.InvariantCulture) + " results for "
                        + chunk.Count.ToString(CultureInfo.InvariantCulture) + " items; results cannot be matched to inputs.");
                output.AddRange(results);
            }
            return output;
        }

        public async Task<RelationGraph> GetRelationsAsync(string id, CancellationToken ct = default(CancellationToken))
        {
            await EnsureCompatibleAsync(ct).ConfigureAwait(false);
            var reply = await SendAsync(new Req { Path = "games/" + Seg(id) + "/relations", Retryable = true }, ct).ConfigureAwait(false);
            return AtlasReader.ReadRelations(Parse(reply)) ?? new RelationGraph();
        }

        public async Task<ArtworkList> GetArtworkAsync(string id, CancellationToken ct = default(CancellationToken))
        {
            await EnsureCompatibleAsync(ct).ConfigureAwait(false);
            var reply = await SendAsync(new Req { Path = "games/" + Seg(id) + "/artwork", Retryable = true }, ct).ConfigureAwait(false);
            return AtlasReader.ReadArtwork(Parse(reply)) ?? new ArtworkList();
        }

        /// <summary>
        /// Download one image's bytes (the connector hands the bytes to the host; the token never goes into a URL).
        /// Prefers the public immutable <c>media_url</c>. Otherwise <c>/artwork/{id}/image</c> with the token, and if Atlas
        /// answers 307/308 (a provider row not cached yet) that redirect is followed at most once, <b>without</b> Authorization,
        /// only to https, and only for an <c>image/*</c> answer within <see cref="AtlasClientOptions.MaxImageBytes"/>.
        /// </summary>
        public async Task<ImageData> DownloadArtworkAsync(Artwork art, CancellationToken ct = default(CancellationToken))
        {
            if (art == null) throw new ArgumentNullException(nameof(art));
            await EnsureCompatibleAsync(ct).ConfigureAwait(false);
            Uri first;
            bool auth;
            if (!string.IsNullOrEmpty(art.MediaUrl)) { first = SameOrigin(art.MediaUrl); auth = false; }
            else if (!string.IsNullOrEmpty(art.ImageUrl)) { first = SameOrigin(art.ImageUrl); auth = true; }
            else if (!string.IsNullOrEmpty(art.Id)) { first = new Uri(_api, "artwork/" + Seg(art.Id) + "/image"); auth = true; }
            else throw new ArgumentException("The artwork has no id and no URL.", nameof(art));

            var reply = await SendAsync(new Req
            {
                Url = first, Auth = auth, Retryable = true, AllowRedirect = auth, MaxBytes = _o.MaxImageBytes, Accept = "image/*",
            }, ct).ConfigureAwait(false);

            if (reply.Status == 307 || reply.Status == 308)
            {
                // one hop, never carrying the token, never to anything but https
                Uri target;
                if (reply.Location == null || !Uri.TryCreate(first, reply.Location, out target))
                    throw new AtlasProtocolException("Atlas redirected an image request without a usable Location.");
                if (target.Scheme != Uri.UriSchemeHttps)
                    throw new AtlasProtocolException("Refusing to follow an image redirect to a non-https address.");
                var hop = await SendAsync(new Req
                {
                    Url = target, Auth = false, AtlasOrigin = false, Retryable = true, MaxBytes = _o.MaxImageBytes, Accept = "image/*",
                }, ct).ConfigureAwait(false);
                reply = hop;
            }
            else if (reply.Status >= 300 && reply.Status < 400)
                throw new AtlasProtocolException("Atlas answered an image request with an unexpected redirect (" + reply.Status.ToString(CultureInfo.InvariantCulture) + ").");

            var type = reply.ContentType;
            if (type == null || !type.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                throw new AtlasProtocolException("The image request did not answer an image (" + (type ?? "no content type") + ").");
            return new ImageData { Bytes = reply.Body, ContentType = type };
        }

        // ---- helpers -----------------------------------------------------------------------------------------

        private static void AppendParam(StringBuilder sb, string name, string value)
        {
            if (!string.IsNullOrEmpty(value)) sb.Append('&').Append(name).Append('=').Append(Uri.EscapeDataString(value));
        }

        /// <summary>One path segment. Atlas ids look like <c>atlas:01abc...</c>; the colon is legal in a segment, so it stays readable.</summary>
        private static string Seg(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("id must not be empty.", nameof(id));
            return Uri.EscapeDataString(id).Replace("%3A", ":");
        }

        /// <summary>An artwork URL from Atlas: relative to the node, or absolute on the node's own origin. Anything else is refused.</summary>
        private Uri SameOrigin(string url)
        {
            // A path is checked first: on Linux/macOS, Uri.TryCreate("/media/x", UriKind.Absolute) succeeds as file:///media/x,
            // so testing "absolute?" first would refuse every relative artwork URL there (Jellyfin servers, the CI runner).
            if (url.StartsWith("/", StringComparison.Ordinal))
            {
                if (url.StartsWith("//", StringComparison.Ordinal) || url.StartsWith("/\\", StringComparison.Ordinal))
                    throw new AtlasProtocolException("Atlas listed an artwork URL that is not a path on the node.");
                var path = _prefix.Length > 0 && !url.StartsWith(_prefix + "/", StringComparison.Ordinal) ? _prefix + url : url;
                return new Uri(_origin, path);
            }
            Uri abs;
            if (!Uri.TryCreate(url, UriKind.Absolute, out abs) || (abs.Scheme != Uri.UriSchemeHttps && abs.Scheme != Uri.UriSchemeHttp))
                throw new AtlasProtocolException("Atlas listed an artwork URL that is not a path on the node.");
            if (!string.Equals(abs.GetLeftPart(UriPartial.Authority), _origin.GetLeftPart(UriPartial.Authority).TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
                throw new AtlasProtocolException("Atlas listed an artwork URL on a different host; it will not be fetched with the token.");
            return abs;
        }

        private JsonNode Parse(Reply reply)
        {
            string text;
            try { text = new UTF8Encoding(false, false).GetString(reply.Body); }
            catch (ArgumentException e) { throw new AtlasProtocolException("Atlas sent a body that is not valid text.", e); }
            return ParseText(text);
        }

        private JsonNode ParseText(string text)
        {
            try { return _json.Parse(text); }
            catch (AtlasProtocolException) { throw; }
            catch (Exception e) { throw new AtlasProtocolException("Atlas sent invalid JSON.", e); }
        }
    }
}
