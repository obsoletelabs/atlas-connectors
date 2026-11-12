using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Atlas.Client;
using Atlas.Playnite;

namespace Atlas.Playnite.Tests
{
    public static class Repo
    {
        public static readonly string Root = Find();

        private static string Find()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, "contract", "fixtures"))) return dir.FullName;
                dir = dir.Parent;
            }
            throw new InvalidOperationException("contract/fixtures not found above " + AppContext.BaseDirectory);
        }

        public static string Text(params string[] parts)
        {
            return File.ReadAllText(Path.Combine(Root, Path.Combine(parts)), Encoding.UTF8);
        }
    }

    public sealed class Seen
    {
        public HttpMethod Method;
        public Uri Uri;
        public Dictionary<string, string> Headers;
        public string Body;
        public string Header(string name) { string v; return Headers.TryGetValue(name, out v) ? v : null; }
    }

    /// <summary>A scripted HTTP handler: no sockets. Records what the client sent.</summary>
    public sealed class FakeHandler : HttpMessageHandler
    {
        private readonly List<KeyValuePair<Func<Seen, bool>, Func<Seen, HttpResponseMessage>>> _routes =
            new List<KeyValuePair<Func<Seen, bool>, Func<Seen, HttpResponseMessage>>>();
        public List<Seen> Requests { get; } = new List<Seen>();
        public bool Offline { get; set; }

        public FakeHandler On(string method, string path, Func<Seen, HttpResponseMessage> respond)
        {
            _routes.Add(new KeyValuePair<Func<Seen, bool>, Func<Seen, HttpResponseMessage>>(
                s => s.Method.Method == method && Uri.UnescapeDataString(s.Uri.AbsolutePath) == path, respond));
            return this;
        }

        public FakeHandler OnHost(string host, Func<Seen, HttpResponseMessage> respond)
        {
            _routes.Add(new KeyValuePair<Func<Seen, bool>, Func<Seen, HttpResponseMessage>>(s => s.Uri.Host == host, respond));
            return this;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in request.Headers) headers[h.Key] = string.Join(",", h.Value);
            var body = request.Content == null ? null : await request.Content.ReadAsStringAsync().ConfigureAwait(false);
            var seen = new Seen { Method = request.Method, Uri = request.RequestUri, Headers = headers, Body = body };
            Requests.Add(seen);
            if (Offline) throw new HttpRequestException("simulated: Atlas is down");
            for (var i = _routes.Count - 1; i >= 0; i--)
                if (_routes[i].Key(seen)) return _routes[i].Value(seen);
            return Json(404, "{\"error\":{\"code\":\"not_found\",\"message\":\"no route in the test\"}}");
        }

        public const string Wire = "use=1, min=1, max=1";

        public static HttpResponseMessage Json(int status, string body, params string[] headerPairs)
        {
            var r = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            r.Headers.TryAddWithoutValidation("Atlas-Wire", Wire);
            for (var i = 0; i + 1 < headerPairs.Length; i += 2) r.Headers.TryAddWithoutValidation(headerPairs[i], headerPairs[i + 1]);
            return r;
        }

        public static HttpResponseMessage Bytes(byte[] bytes, string contentType)
        {
            var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
            r.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            r.Headers.TryAddWithoutValidation("Atlas-Wire", Wire);
            return r;
        }

        public static HttpResponseMessage Redirect(int status, string location)
        {
            var r = new HttpResponseMessage((HttpStatusCode)status);
            r.Headers.TryAddWithoutValidation("Location", location);
            r.Headers.TryAddWithoutValidation("Atlas-Wire", Wire);
            return r;
        }
    }

    /// <summary>A reversible stand-in for DPAPI so tests run the same everywhere; DPAPI itself has its own tests.</summary>
    public sealed class ReverseProtector : ITokenProtector
    {
        public string Protect(string token)
        {
            if (string.IsNullOrEmpty(token)) return string.Empty;
            var a = token.ToCharArray(); Array.Reverse(a);
            return "protected:" + new string(a);
        }

        public string Unprotect(string stored)
        {
            if (string.IsNullOrEmpty(stored) || !stored.StartsWith("protected:", StringComparison.Ordinal)) return null;
            var a = stored.Substring("protected:".Length).ToCharArray(); Array.Reverse(a);
            return new string(a);
        }
    }

    public static class TestAtlas
    {
        public const string BaseUrl = "https://atlas.example.org/";
        /// <summary>lanternfall in contract/fixtures/MANIFEST.json (the merge survivor of lanternfall-duplicate).</summary>
        public const string RecordId = "atlas:01kdvdna5a000000000000000h";
        public const string DuplicateId = "atlas:01kdvdna84000000000000000t";
        private static readonly IJsonAdapter Json = new NewtonsoftJsonAdapter();

        public static string Record { get { return Repo.Text("contract", "fixtures", "games", "record-lanternfall.json"); } }

        /// <summary>The recorded title (whatever Atlas recorded for the survivor).</summary>
        public static string Title { get { return Json.Parse(Record)["title"].AsString(); } }

        /// <summary>A one-item /resolve answer: the first result of the recorded games/resolve-by-ids.json (an id match).</summary>
        public static string ResolveAnswer()
        {
            var recorded = Json.Parse(Repo.Text("contract", "fixtures", "games", "resolve-by-ids.json"));
            var first = recorded["response"]["results"].Items[0];
            return Json.Write(JsonNode.Object(new[] { new KeyValuePair<string, JsonNode>("results", JsonNode.Array(new[] { first })) }));
        }

        /// <summary>A /games/batch answer in the client-api 1.0 shape: the record for <see cref="RecordId"/>, not_found for the rest.</summary>
        public static string BatchAnswer(string requestBody)
        {
            var items = new List<JsonNode>();
            foreach (var id in Json.Parse(requestBody)["ids"].Items)
            {
                var known = id.AsString() == RecordId;
                items.Add(JsonNode.Object(new[]
                {
                    new KeyValuePair<string, JsonNode>("requested", id),
                    new KeyValuePair<string, JsonNode>("status", JsonNode.Str(known ? "ok" : "not_found")),
                    new KeyValuePair<string, JsonNode>("game", known ? Json.Parse(Record) : JsonNode.Null),
                }));
            }
            return Json.Write(JsonNode.Object(new[] { new KeyValuePair<string, JsonNode>("results", JsonNode.Array(items)) }));
        }
        public const string Token = "tok-synthetic-0000";

        public static string TempDir()
        {
            var d = Path.Combine(Path.GetTempPath(), "atlas-playnite-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(d);
            return d;
        }

        /// <summary>A handler that serves the recorded connectors-v1 fixtures (client-api 1.0) like the real routes do.</summary>
        public static FakeHandler RecordHandler()
        {
            var record = Record;
            return new FakeHandler()
                .On("GET", "/api/v2/site", s => FakeHandler.Json(200, Repo.Text("contract", "fixtures", "games", "site.json")))
                .On("POST", "/api/v2/resolve", s => FakeHandler.Json(200, ResolveAnswer()))
                .On("POST", "/api/v2/games/batch", s => FakeHandler.Json(200, BatchAnswer(s.Body)))
                .On("GET", "/api/v2/games/" + RecordId, s =>
                    s.Header("If-None-Match") == "\"v2\""
                        ? FakeHandler.Json(304, "", "ETag", "\"v2\"")
                        : FakeHandler.Json(200, record, "ETag", "\"v2\""));
        }

        public static AtlasServices Services(FakeHandler handler, string dir, ITokenProtector protector = null, AtlasSettings settings = null)
        {
            protector = protector ?? new ReverseProtector();
            settings = settings ?? new AtlasSettings { BaseUrl = BaseUrl, ProtectedToken = protector.Protect(Token) };
            return new AtlasServices(settings, protector, dir, () => handler);
        }
    }
}
