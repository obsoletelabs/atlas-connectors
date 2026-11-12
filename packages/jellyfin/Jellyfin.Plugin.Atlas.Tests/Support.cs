using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Jellyfin.Plugin.Atlas.Configuration;

namespace Jellyfin.Plugin.Atlas.Tests;

public static class Repo
{
    public static readonly string Root = Find();

    private static string Find()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "contract", "fixtures")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("contract/fixtures not found above " + AppContext.BaseDirectory);
    }

    public static string Text(params string[] parts) => File.ReadAllText(Path.Combine(Root, Path.Combine(parts)), Encoding.UTF8);
}

public sealed class Seen
{
    public HttpMethod Method;
    public Uri Uri;
    public Dictionary<string, string> Headers;
    public string Body;

    public string Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;
}

/// <summary>A scripted HTTP handler: no sockets. Records what the plugin sent.</summary>
public sealed class FakeHandler : HttpMessageHandler
{
    public const string Wire = "use=1, min=1, max=1";

    private readonly List<(Func<Seen, bool> Match, Func<Seen, HttpResponseMessage> Respond)> _routes = new();

    public List<Seen> Requests { get; } = new();

    public bool Offline { get; set; }

    public FakeHandler On(string method, string path, Func<Seen, HttpResponseMessage> respond)
    {
        _routes.Add((s => s.Method.Method == method && Uri.UnescapeDataString(s.Uri.AbsolutePath) == path, respond));
        return this;
    }

    public FakeHandler OnHost(string host, Func<Seen, HttpResponseMessage> respond)
    {
        _routes.Add((s => s.Uri.Host == host, respond));
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in request.Headers)
        {
            headers[h.Key] = string.Join(",", h.Value);
        }

        var body = request.Content == null ? null : await request.Content.ReadAsStringAsync(ct);
        var seen = new Seen { Method = request.Method, Uri = request.RequestUri, Headers = headers, Body = body };
        Requests.Add(seen);
        if (Offline)
        {
            throw new HttpRequestException("simulated: Atlas is down");
        }

        for (var i = _routes.Count - 1; i >= 0; i--)
        {
            if (_routes[i].Match(seen))
            {
                return _routes[i].Respond(seen);
            }
        }

        return Json(404, "{\"error\":{\"code\":\"not_found\",\"message\":\"no route in the test\"}}");
    }

    public static HttpResponseMessage Json(int status, string body, params string[] headerPairs)
    {
        var r = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        r.Headers.TryAddWithoutValidation("Atlas-Wire", Wire);
        for (var i = 0; i + 1 < headerPairs.Length; i += 2)
        {
            r.Headers.TryAddWithoutValidation(headerPairs[i], headerPairs[i + 1]);
        }

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

public sealed class Harness : IDisposable
{
    public const string BaseUrl = "https://atlas.example.org/";
    public const string Token = "tok-synthetic-0000";
    public const string BookId = "atlas:01kdvdna9c000000000000000y";   // the-lamp-keeper in contract/fixtures/MANIFEST.json

    public const string BookTitle = "The Lamp Keeper";

    public const string BookIsbn = "9780000000118";                   // its first edition (hardcover)

    public Harness(FakeHandler handler, PluginConfiguration config = null, bool withToken = true)
    {
        Dir = Path.Combine(Path.GetTempPath(), "atlas-jellyfin-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Dir);
        Handler = handler;
        Config = config ?? new PluginConfiguration { BaseUrl = BaseUrl };
        Tokens = new MemoryTokenStore();
        if (withToken)
        {
            Tokens.Write(Token);
        }

        Log = new List<string>();
        Runtime = new AtlasRuntime(() => Config, Tokens, Path.Combine(Dir, "cache"), () => handler, Log.Add);
    }

    public string Dir { get; }

    public FakeHandler Handler { get; }

    public PluginConfiguration Config { get; }

    public MemoryTokenStore Tokens { get; }

    public List<string> Log { get; }

    public AtlasRuntime Runtime { get; }

    public static string Site => Repo.Text("contract", "fixtures", "games", "site.json");

    public static string Book => Repo.Text("contract", "fixtures", "books", "record-the-lamp-keeper.json");

    /// <summary>Serves /site, one book by id (with an ETag), and /resolve answering "id" for the book.</summary>
    public static FakeHandler BookHandler()
    {
        return new FakeHandler()
            .On("GET", "/api/v2/site", s => FakeHandler.Json(200, Site))
            .On("GET", "/api/v2/games/" + BookId, s =>
                s.Header("If-None-Match") == "\"b1\"" ? FakeHandler.Json(304, string.Empty, "ETag", "\"b1\"") : FakeHandler.Json(200, Book, "ETag", "\"b1\""))
            .On("POST", "/api/v2/resolve", s => FakeHandler.Json(200, "{\"results\":[{\"assigned\":true,\"canonical_id\":\"" + BookId + "\",\"reason\":\"id\",\"method\":\"id\",\"record\":" + Book + "}]}"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Dir, true);
        }
        catch (IOException)
        {
        }
    }
}
