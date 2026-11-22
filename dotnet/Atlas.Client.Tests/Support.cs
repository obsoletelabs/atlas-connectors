using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Atlas.Client;

namespace Atlas.Client.Tests;

public static class Fixtures
{
    public static readonly string Root = FindRoot();

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "contract", "fixtures");
            if (Directory.Exists(candidate)) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("contract/fixtures not found above " + AppContext.BaseDirectory);
    }

    /// <summary>Text of a file under contract/ (for example "fixtures/games/site.json").</summary>
    public static string Text(string relative) => File.ReadAllText(Path.Combine(Root, "contract", relative));

    /// <summary>
    /// The answer body a fixture records. GET answers are stored as the bare body; POST answers (batch, resolve, pairing) as
    /// <c>{"request": ..., "response": ...}</c> (Atlas tools/export_client_fixtures.py), so this returns <c>response</c> then.
    /// </summary>
    public static string Body(string relative) => Part(relative, "response");

    /// <summary>The request a POST fixture was recorded with.</summary>
    public static string Request(string relative) => Part(relative, "request");

    private static string Part(string relative, string member)
    {
        var text = Text(relative);
        using var doc = System.Text.Json.JsonDocument.Parse(text);
        var root = doc.RootElement;
        if (root.ValueKind == System.Text.Json.JsonValueKind.Object && root.TryGetProperty("request", out _)
            && root.TryGetProperty("response", out _))
            return root.GetProperty(member).GetRawText();
        if (member == "request") throw new InvalidOperationException(relative + " records no request");
        return text;
    }

    /// <summary>MANIFEST.json's seed name -> Atlas id map (the synthetic connectors-v1 catalogue).</summary>
    public static string SeedId(string name)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(Text("fixtures/MANIFEST.json"));
        return doc.RootElement.GetProperty("seed_ids").GetProperty(name).GetString();
    }

    public static IEnumerable<string> AllJson() =>
        Directory.EnumerateFiles(Path.Combine(Root, "contract", "fixtures"), "*.json", SearchOption.AllDirectories)
            .Where(p => !p.EndsWith("resolve-contract-v1.json", StringComparison.Ordinal))   // a case list, not a record
            .Select(p => Path.GetRelativePath(Path.Combine(Root, "contract"), p).Replace('\\', '/'));
}

public static class Adapters
{
    public static IJsonAdapter Make(string name) => name switch
    {
        "stj" => new SystemTextJsonAdapter(),
        "newtonsoft" => new NewtonsoftJsonAdapter(),
        _ => throw new ArgumentException(name),
    };

    public static IEnumerable<object[]> Both() { yield return new object[] { "stj" }; yield return new object[] { "newtonsoft" }; }

    /// <summary>Structural equality of two trees (member order matters, numbers compare by value).</summary>
    public static bool Same(JsonNode a, JsonNode b)
    {
        if (a.Kind != b.Kind) return false;
        switch (a.Kind)
        {
            case JsonKind.String: return a.AsString() == b.AsString();
            case JsonKind.Bool: return a.AsBool() == b.AsBool();
            case JsonKind.Number: return a.AsDouble() == b.AsDouble();
            case JsonKind.Array:
                return a.Items.Count == b.Items.Count && a.Items.Zip(b.Items).All(p => Same(p.First, p.Second));
            case JsonKind.Object:
                return a.Members.Count == b.Members.Count
                    && a.Members.Zip(b.Members).All(p => p.First.Key == p.Second.Key && Same(p.First.Value, p.Second.Value));
            default: return true;
        }
    }
}

public sealed record Seen(HttpMethod Method, Uri Uri, Dictionary<string, string> Headers, string Body)
{
    public string Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;
    public bool HasHeader(string name) => Headers.ContainsKey(name);
}

/// <summary>A scripted HTTP handler: no sockets. Records what the client sent.</summary>
public sealed class FakeHandler : HttpMessageHandler
{
    public delegate HttpResponseMessage Responder(Seen request);

    private readonly List<(Func<Seen, bool> Match, Responder Respond)> _routes = new();
    public List<Seen> Requests { get; } = new();

    public FakeHandler On(string method, string path, Responder respond)
    {
        _routes.Add((s => s.Method.Method == method && Uri.UnescapeDataString(s.Uri.AbsolutePath) == path, respond));
        return this;
    }

    public FakeHandler OnHost(string host, Responder respond)
    {
        _routes.Add((s => s.Uri.Host == host, respond));
        return this;
    }

    /// <summary>Answer the same route with each responder in turn, then repeat the last.</summary>
    public FakeHandler Sequence(string method, string path, params Responder[] responders)
    {
        var i = 0;
        return On(method, path, r => responders[Math.Min(i++, responders.Length - 1)](r));
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in request.Headers) headers[h.Key] = string.Join(",", h.Value);
        var body = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var seen = new Seen(request.Method, request.RequestUri, headers, body);
        Requests.Add(seen);
        for (var i = _routes.Count - 1; i >= 0; i--)
            if (_routes[i].Match(seen)) return _routes[i].Respond(seen);
        return Reply.Status(404, new { error = new { code = "not_found", message = "no route in the test" } });
    }
}

public static class Reply
{
    public const string Wire = "use=1, min=1, max=1";

    public static HttpResponseMessage Raw(int status, string body, string contentType = "application/json", string wire = Wire,
        params (string Name, string Value)[] headers)
    {
        var r = new HttpResponseMessage((HttpStatusCode)status);
        if (body != null)
        {
            r.Content = new StringContent(body, Encoding.UTF8);
            r.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        }
        if (wire != null) r.Headers.TryAddWithoutValidation("Atlas-Wire", wire);
        foreach (var (n, v) in headers) r.Headers.TryAddWithoutValidation(n, v);
        return r;
    }

    public static HttpResponseMessage Json(string json, params (string, string)[] headers) => Raw(200, json, headers: headers);

    public static HttpResponseMessage Status(int status, object body, params (string, string)[] headers) =>
        Raw(status, System.Text.Json.JsonSerializer.Serialize(body), headers: headers);

    public static HttpResponseMessage Bytes(int status, byte[] bytes, string contentType, string wire = Wire)
    {
        var r = new HttpResponseMessage((HttpStatusCode)status) { Content = new ByteArrayContent(bytes) };
        r.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        if (wire != null) r.Headers.TryAddWithoutValidation("Atlas-Wire", wire);
        return r;
    }
}

public sealed class Rig : IDisposable
{
    public FakeHandler Handler { get; } = new();
    public List<TimeSpan> Delays { get; } = new();
    public AtlasClientOptions Options { get; }
    public AtlasClient Client { get; }
    public const string Token = "tok-SECRET-123456";

    public Rig(string adapter = "stj", Action<AtlasClientOptions> configure = null, string site = null, string baseUrl = "https://atlas.test/")
    {
        Options = new AtlasClientOptions
        {
            BaseUri = new Uri(baseUrl),
            TokenProvider = () => Token,
            HostName = "jellyfin",
            HostVersion = "1.2.3",
            BaseDelay = TimeSpan.FromMilliseconds(400),
            Jitter = () => 1.0,                                        // factor exactly 1.0 (0.5 + 0.5 * 1.0)
            Delay = (t, ct) => { Delays.Add(t); return Task.CompletedTask; },
        };
        configure?.Invoke(Options);
        var prefix = new Uri(baseUrl).AbsolutePath.TrimEnd('/');
        Handler.On("GET", prefix + "/api/v2/site", _ => Reply.Json(site ?? Fixtures.Text("fixtures/games/site.json")));
        Client = new AtlasClient(Options, Adapters.Make(adapter), Handler);
    }

    public IEnumerable<Seen> Api => Handler.Requests.Where(r => !r.Uri.AbsolutePath.EndsWith("/site", StringComparison.Ordinal));

    public void Dispose() => Client.Dispose();

    /// <summary>The recorded /site with its client_api replaced (or removed, for null).</summary>
    public static string SiteWith(string clientApiJson)
    {
        var site = System.Text.Json.Nodes.JsonNode.Parse(Fixtures.Text("fixtures/games/site.json")).AsObject();
        site.Remove("client_api");
        if (clientApiJson != null) site["client_api"] = System.Text.Json.Nodes.JsonNode.Parse(clientApiJson);
        return site.ToJsonString();
    }

    /// <summary>An Atlas from before client-api 1.0: the recorded /site without client_api.</summary>
    public static string LegacySite => SiteWith(null);
}
