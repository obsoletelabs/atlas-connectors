using System.Net;
using Atlas.Client;
using Xunit;

namespace Atlas.Client.Tests;

public class ClientTests
{
    private const string PortalId = "atlas:01m70bhdfwzc60xawvc1avtnv7";

    // ---- request shape --------------------------------------------------------------------------------------

    [Theory, MemberData(nameof(Adapters.Both), MemberType = typeof(Adapters))]
    public async Task Search_sends_identity_headers_and_never_puts_the_token_in_a_url(string adapter)
    {
        using var rig = new Rig(adapter);
        rig.Handler.On("GET", "/api/v2/games", _ => Reply.Json(Fixtures.Text("fixtures/games/search-portal.json")));

        var page = await rig.Client.SearchAsync("portal 2 & more", mediaType: "game", limit: 500, offset: 10, author: "herb");

        Assert.Equal("Portal 2", Assert.Single(page.Results).Title);
        var req = rig.Api.Single();
        Assert.Equal("Bearer " + Rig.Token, req.Header("Authorization"));
        Assert.Equal("atlas-connector-jellyfin/1.2.3", req.Header("User-Agent"));
        Assert.Equal("min=1, max=1", req.Header("Atlas-Wire"));
        Assert.Contains("q=portal%202%20%26%20more", req.Uri.Query);
        Assert.Contains("type=game", req.Uri.Query);
        Assert.Contains("author=herb", req.Uri.Query);
        Assert.Contains("limit=100", req.Uri.Query);          // clamped to what Atlas accepts
        Assert.Contains("offset=10", req.Uri.Query);
        foreach (var r in rig.Handler.Requests) Assert.DoesNotContain("SECRET", r.Uri.ToString());
        Assert.DoesNotContain(rig.Handler.Requests, r => r.Uri.AbsolutePath.EndsWith("/site") && r.HasHeader("Authorization"));   // /site is public
    }

    [Fact]
    public async Task Search_rejects_empty_and_overlong_queries_before_any_request()
    {
        using var rig = new Rig();
        await Assert.ThrowsAsync<ArgumentException>(() => rig.Client.SearchAsync("  "));
        await Assert.ThrowsAsync<ArgumentException>(() => rig.Client.SearchAsync(new string('x', 201)));
        Assert.Empty(rig.Handler.Requests);
    }

    [Fact]
    public async Task Site_is_read_once_per_session()
    {
        using var rig = new Rig();
        rig.Handler.On("GET", "/api/v2/games", _ => Reply.Json("{\"results\":[]}"));
        await rig.Client.SearchAsync("a");
        await rig.Client.SearchAsync("b");
        Assert.Single(rig.Handler.Requests, r => r.Uri.AbsolutePath.EndsWith("/site"));
    }

    [Fact]
    public async Task A_path_prefix_behind_a_reverse_proxy_is_kept()
    {
        using var rig = new Rig(baseUrl: "https://nas.test/atlas/");
        rig.Handler.On("GET", "/atlas/api/v2/games", _ => Reply.Json("{\"results\":[]}"));
        await rig.Client.SearchAsync("a");
        Assert.All(rig.Handler.Requests, r => Assert.StartsWith("/atlas/api/v2/", r.Uri.AbsolutePath));
    }

    [Theory]
    [InlineData("http://atlas.lan/")]
    [InlineData("ftp://atlas.test/")]
    [InlineData("https://user:pw@atlas.test/")]
    [InlineData("https://atlas.test/?x=1")]
    public void Unsafe_base_urls_are_refused(string url)
    {
        var o = new AtlasClientOptions { BaseUri = new Uri(url) };
        Assert.Throws<ArgumentException>(() => new AtlasClient(o, new SystemTextJsonAdapter()));
    }

    [Theory]
    [InlineData("http://127.0.0.1:8000/", false)]
    [InlineData("http://localhost:8000/", false)]
    [InlineData("http://atlas.lan/", true)]
    public void Plain_http_only_for_loopback_or_when_opted_in(string url, bool optIn)
    {
        var o = new AtlasClientOptions { BaseUri = new Uri(url), AllowPlainHttp = optIn };
        using var c = new AtlasClient(o, new SystemTextJsonAdapter());
    }

    [Fact]
    public void Host_name_must_be_a_plain_lowercase_word()
    {
        var o = new AtlasClientOptions { BaseUri = new Uri("https://atlas.test/"), HostName = "Jelly fin" };
        Assert.Throws<ArgumentException>(() => new AtlasClient(o, new SystemTextJsonAdapter()));
    }

    // ---- records, ETag, merge redirects -----------------------------------------------------------------------

    [Theory, MemberData(nameof(Adapters.Both), MemberType = typeof(Adapters))]
    public async Task Record_fetch_with_etag_then_304(string adapter)
    {
        using var rig = new Rig(adapter);
        rig.Handler.Sequence("GET", "/api/v2/games/" + PortalId,
            _ => Reply.Json(Fixtures.Text("fixtures/games/record-portal.json"), ("ETag", "\"" + PortalId + "-2\"")),
            _ => Reply.Raw(304, null));

        var first = await rig.Client.GetRecordAsync(PortalId);
        Assert.False(first.NotModified);
        Assert.Equal("Portal 2", first.Record.Title);
        Assert.Equal("\"" + PortalId + "-2\"", first.ETag);

        var second = await rig.Client.GetRecordAsync(PortalId, first.ETag);
        Assert.True(second.NotModified);
        Assert.Null(second.Record);
        Assert.Equal(first.ETag, second.ETag);
        Assert.Null(rig.Api.First().Header("If-None-Match"));
        Assert.Equal(first.ETag, rig.Api.Last().Header("If-None-Match"));
        Assert.EndsWith("/games/" + PortalId, Uri.UnescapeDataString(rig.Api.First().Uri.AbsolutePath));
    }

    [Fact]
    public async Task A_merged_id_comes_back_with_redirected_from()
    {
        using var rig = new Rig();
        rig.Handler.On("GET", "/api/v2/games/atlas:old", _ => Reply.Json("{\"id\":\"atlas:new\",\"redirected_from\":\"atlas:old\",\"title\":\"T\"}"));
        var r = await rig.Client.GetRecordAsync("atlas:old");
        Assert.Equal("atlas:new", r.Record.Id);
        Assert.Equal("atlas:old", r.Record.RedirectedFrom);
    }

    [Fact]
    public async Task Ids_are_escaped_as_one_path_segment()
    {
        using var rig = new Rig();
        rig.Handler.On("GET", "/api/v2/games/a/b ?x", _ => Reply.Json("{\"id\":\"a\"}"));
        await rig.Client.GetRecordAsync("a/b ?x");
        Assert.Contains("a%2Fb%20%3Fx", rig.Api.Single().Uri.AbsolutePath);
        await Assert.ThrowsAsync<ArgumentException>(() => rig.Client.GetRecordAsync(""));
    }

    [Fact]
    public async Task Batch_capability_uses_the_batch_endpoint_in_chunks_of_200()
    {
        var site = Rig.SiteWith("{\"version\":\"1.0\",\"min_supported\":\"1.0\",\"capabilities\":[\"batch\",\"etag\"],\"deprecations\":[]}");
        using var rig = new Rig("newtonsoft", site: site);
        var sizes = new List<int>();
        rig.Handler.On("POST", "/api/v2/games/batch", r =>
        {
            var ids = new SystemTextJsonAdapter().Parse(r.Body)["ids"].Items;
            sizes.Add(ids.Count);
            return Reply.Json("{\"results\":[" + string.Join(",", ids.Select(i => "{\"id\":\"" + i.AsString() + "\"}")) + "]}");
        });
        var ids = Enumerable.Range(0, 450).Select(i => "atlas:" + i).Concat(new[] { "atlas:1", "" }).ToList();   // duplicate and blank are dropped
        var records = await rig.Client.GetRecordsAsync(ids);
        Assert.Equal(new[] { 200, 200, 50 }, sizes);
        Assert.Equal(450, records.Count);
    }

    [Fact]
    public async Task Without_the_batch_capability_it_falls_back_to_single_gets_and_skips_unknown_ids()
    {
        using var rig = new Rig();
        rig.Handler.On("GET", "/api/v2/games/atlas:a", _ => Reply.Json("{\"id\":\"atlas:a\"}"));
        var records = await rig.Client.GetRecordsAsync(new[] { "atlas:a", "atlas:missing" });
        Assert.Equal("atlas:a", Assert.Single(records).Id);
        Assert.DoesNotContain(rig.Handler.Requests, r => r.Uri.AbsolutePath.EndsWith("/batch"));
    }

    // ---- resolve -------------------------------------------------------------------------------------------

    private static void ResolveEcho(FakeHandler h, List<int> sizes) => h.On("POST", "/api/v2/resolve", r =>
    {
        var items = new SystemTextJsonAdapter().Parse(r.Body)["items"].Items;
        sizes.Add(items.Count);
        return Reply.Json("{\"results\":[" + string.Join(",", items.Select(_ => "{\"canonical_id\":null,\"method\":\"none\",\"confidence\":0.0,\"candidates\":[]}")) + "]}");
    });

    [Theory, MemberData(nameof(Adapters.Both), MemberType = typeof(Adapters))]
    public async Task Resolve_batches_at_200_and_keeps_input_order(string adapter)
    {
        using var rig = new Rig(adapter);
        var sizes = new List<int>();
        ResolveEcho(rig.Handler, sizes);
        var items = Enumerable.Range(0, 450).Select(i => new ResolveItem { ProviderIds = new Dictionary<string, string> { ["steam"] = i.ToString() } }).ToList();

        var results = await rig.Client.ResolveAsync(items);

        Assert.Equal(new[] { 200, 200, 50 }, sizes);
        Assert.Equal(450, results.Count);
        var first = new SystemTextJsonAdapter().Parse(rig.Api.First().Body);
        Assert.Equal("0", first["items"].Items[0]["provider_ids"]["steam"].AsString());
        Assert.Equal(true, first["include_view"].AsBool());
        Assert.Equal("200", new SystemTextJsonAdapter().Parse(rig.Api.Skip(1).First().Body)["items"].Items[0]["provider_ids"]["steam"].AsString());
    }

    [Fact]
    public async Task Resolve_sends_only_what_the_host_knows()
    {
        using var rig = new Rig();
        rig.Handler.On("POST", "/api/v2/resolve", _ => Reply.Json("{\"results\":[{\"canonical_id\":null,\"method\":\"none\"}]}"));
        await rig.Client.ResolveAsync(new[]
        {
            new ResolveItem { Title = "Doom", ReleaseDate = "1993-12-10", ProviderIds = new Dictionary<string, string> { ["steamsync"] = "2280", ["empty"] = "" } },
        }, includeView: false);
        var sent = new SystemTextJsonAdapter().Parse(rig.Api.Single().Body);
        var item = sent["items"].Items[0];
        Assert.Equal("Doom", item["title"].AsString());
        Assert.False(item.Has("platform"));                       // not sent when unknown
        Assert.Equal("2280", item["provider_ids"]["steamsync"].AsString());
        Assert.False(item["provider_ids"].Has("empty"));
        Assert.Equal(false, sent["include_view"].AsBool());
    }

    [Fact]
    public async Task Resolve_with_a_mismatched_result_count_is_a_protocol_error()
    {
        using var rig = new Rig();
        rig.Handler.On("POST", "/api/v2/resolve", _ => Reply.Json("{\"results\":[]}"));
        await Assert.ThrowsAsync<AtlasProtocolException>(() => rig.Client.ResolveAsync(new[] { new ResolveItem { Title = "x" } }));
    }

    [Fact]
    public async Task Resolve_fixture_round_trip_through_the_client()
    {
        using var rig = new Rig();
        rig.Handler.On("POST", "/api/v2/resolve", _ => Reply.Json(Fixtures.Text("fixtures/games/resolve-by-atlas-id.json")));
        var r = Assert.Single(await rig.Client.ResolveAsync(new[] { new ResolveItem { ProviderIds = new Dictionary<string, string> { ["atlas"] = PortalId } } }));
        Assert.True(r.CanAutoAssign);
        Assert.Equal(PortalId, r.CanonicalId);
    }

    // ---- retry / backoff / rate limit -----------------------------------------------------------------------

    [Fact]
    public async Task A_503_is_retried_with_exponential_backoff_then_succeeds()
    {
        using var rig = new Rig();
        rig.Handler.Sequence("GET", "/api/v2/games",
            _ => Reply.Status(503, new { error = new { code = "unavailable", message = "busy" } }),
            _ => Reply.Status(502, new { }),
            _ => Reply.Json("{\"results\":[]}"));
        await rig.Client.SearchAsync("a");
        Assert.Equal(new[] { TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(800) }, rig.Delays);
        Assert.Equal(3, rig.Api.Count());
    }

    [Fact]
    public async Task Backoff_is_capped_and_retries_are_bounded()
    {
        using var rig = new Rig(configure: o => { o.MaxRetries = 4; o.MaxDelay = TimeSpan.FromMilliseconds(1000); });
        rig.Handler.On("GET", "/api/v2/games", _ => Reply.Status(503, new { error = new { code = "unavailable", message = "busy" } }));
        var e = await Assert.ThrowsAsync<AtlasApiException>(() => rig.Client.SearchAsync("a"));
        Assert.Equal(503, e.Status);
        Assert.Equal("unavailable", e.Code);
        Assert.Equal(5, rig.Api.Count());                                       // first try + 4 retries
        Assert.Equal(new[] { 400, 800, 1000, 1000 }, rig.Delays.Select(d => (int)d.TotalMilliseconds));
    }

    [Fact]
    public async Task Jitter_scales_the_backoff_into_half_to_full()
    {
        using var rig = new Rig(configure: o => o.Jitter = () => 0.0);
        rig.Handler.Sequence("GET", "/api/v2/games", _ => Reply.Status(503, new { }), _ => Reply.Json("{\"results\":[]}"));
        await rig.Client.SearchAsync("a");
        Assert.Equal(TimeSpan.FromMilliseconds(200), Assert.Single(rig.Delays));
    }

    [Fact]
    public async Task Retry_After_seconds_is_honoured_for_429()
    {
        using var rig = new Rig();
        rig.Handler.Sequence("GET", "/api/v2/games",
            _ => Reply.Status(429, new { error = new { code = "rate_limited", message = "slow" } }, ("Retry-After", "3")),
            _ => Reply.Json("{\"results\":[]}"));
        await rig.Client.SearchAsync("a");
        Assert.Equal(TimeSpan.FromSeconds(3), Assert.Single(rig.Delays));
    }

    [Fact]
    public async Task Retry_After_http_date_is_honoured()
    {
        using var rig = new Rig();
        var when = DateTimeOffset.UtcNow.AddSeconds(30).ToString("R");
        rig.Handler.Sequence("GET", "/api/v2/games", _ => Reply.Status(503, new { }, ("Retry-After", when)), _ => Reply.Json("{\"results\":[]}"));
        await rig.Client.SearchAsync("a");
        var d = Assert.Single(rig.Delays);
        Assert.InRange(d.TotalSeconds, 25, 31);
    }

    [Fact]
    public async Task A_retry_after_longer_than_the_cap_is_thrown_not_slept()
    {
        using var rig = new Rig();
        rig.Handler.On("GET", "/api/v2/games", _ => Reply.Status(429, new { error = new { code = "rate_limited", message = "slow" } }, ("Retry-After", "120")));
        var e = await Assert.ThrowsAsync<AtlasRateLimitedException>(() => rig.Client.SearchAsync("a"));
        Assert.Equal(TimeSpan.FromSeconds(120), e.RetryAfter);
        Assert.Equal(429, e.Status);
        Assert.Empty(rig.Delays);                                               // the UI thread was not made to wait
        Assert.Single(rig.Api);
    }

    [Fact]
    public async Task Other_errors_are_not_retried_and_carry_the_stable_code()
    {
        using var rig = new Rig();
        rig.Handler.On("GET", "/api/v2/games/atlas:nope", _ => Reply.Status(404, new { error = new { code = "not_found", message = "No such record.", request_id = "r-1" } }));
        var e = await Assert.ThrowsAsync<AtlasApiException>(() => rig.Client.GetRecordAsync("atlas:nope"));
        Assert.Equal(404, e.Status);
        Assert.Equal("not_found", e.Code);
        Assert.Equal("r-1", e.RequestId);
        Assert.Equal("No such record.", e.Message);
        Assert.Empty(rig.Delays);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(500)]
    [InlineData(422)]
    public async Task Auth_and_server_errors_fail_at_once_with_a_readable_message(int status)
    {
        using var rig = new Rig();
        rig.Handler.On("GET", "/api/v2/games", _ => Reply.Raw(status, "<html>proxy page</html>", "text/html"));
        var e = await Assert.ThrowsAsync<AtlasApiException>(() => rig.Client.SearchAsync("a"));
        Assert.Equal(status, e.Status);
        Assert.False(string.IsNullOrWhiteSpace(e.Message));
        Assert.DoesNotContain("SECRET", e.Message);
        Assert.Empty(rig.Delays);
    }

    [Fact]
    public async Task FastAPI_detail_strings_are_used_as_the_message()
    {
        using var rig = new Rig();
        rig.Handler.On("POST", "/api/v2/resolve", _ => Reply.Status(413, new { detail = "at most 200 items per request" }));
        var e = await Assert.ThrowsAsync<AtlasApiException>(() => rig.Client.ResolveAsync(new[] { new ResolveItem { Title = "x" } }));
        Assert.Equal("at most 200 items per request", e.Message);
    }

    [Fact]
    public async Task Network_failures_are_retried_for_reads_then_reported_without_the_url_secret()
    {
        using var rig = new Rig();
        var calls = 0;
        rig.Handler.On("GET", "/api/v2/games", _ => { calls++; throw new HttpRequestException("connection reset"); });
        var e = await Assert.ThrowsAsync<AtlasNetworkException>(() => rig.Client.SearchAsync("a"));
        Assert.Equal(4, calls);                                                  // 1 + 3 retries
        Assert.IsType<HttpRequestException>(e.InnerException);
        Assert.DoesNotContain("SECRET", e.Message);
    }

    [Fact]
    public async Task A_timeout_is_a_network_error_but_the_callers_cancellation_is_not()
    {
        using var rig = new Rig(configure: o => { o.RequestTimeout = TimeSpan.FromMilliseconds(30); o.MaxRetries = 0; });
        rig.Handler.On("GET", "/api/v2/games", _ => throw new TaskCanceledException("timeout"));
        await Assert.ThrowsAsync<AtlasNetworkException>(() => rig.Client.SearchAsync("a"));

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Client.SearchAsync("a", ct: cts.Token));
    }

    [Fact]
    public async Task Read_only_post_resolve_is_retried()
    {
        using var rig = new Rig();
        rig.Handler.Sequence("POST", "/api/v2/resolve", _ => Reply.Status(503, new { }),
            _ => Reply.Json("{\"results\":[{\"canonical_id\":null,\"method\":\"none\"}]}"));
        await rig.Client.ResolveAsync(new[] { new ResolveItem { Title = "x" } });
        Assert.Equal(2, rig.Api.Count());
    }

    [Fact]
    public async Task A_redirect_on_an_api_call_is_never_followed()
    {
        using var rig = new Rig();
        rig.Handler.On("GET", "/api/v2/games", _ => Reply.Raw(302, null, headers: ("Location", "https://evil.example/steal")));
        await Assert.ThrowsAsync<AtlasProtocolException>(() => rig.Client.SearchAsync("a"));
        Assert.DoesNotContain(rig.Handler.Requests, r => r.Uri.Host == "evil.example");
    }

    [Fact]
    public async Task Invalid_json_body_is_a_protocol_error()
    {
        using var rig = new Rig();
        rig.Handler.On("GET", "/api/v2/games", _ => Reply.Raw(200, "<html>captive portal</html>", "text/html"));
        await Assert.ThrowsAsync<AtlasProtocolException>(() => rig.Client.SearchAsync("a"));
    }

    [Fact]
    public async Task An_oversize_json_body_is_refused()
    {
        using var rig = new Rig();
        rig.Handler.On("GET", "/api/v2/games", _ => Reply.Bytes(200, new byte[70 * 1024 * 1024], "application/json"));
        await Assert.ThrowsAsync<AtlasProtocolException>(() => rig.Client.SearchAsync("a"));
    }

    // ---- client-api compatibility ----------------------------------------------------------------------------

    [Fact]
    public async Task A_different_client_api_major_is_refused_with_a_clear_message()
    {
        using var rig = new Rig(site: Rig.SiteWith("{\"version\":\"2.0\",\"min_supported\":\"2.0\",\"capabilities\":[],\"deprecations\":[]}"));
        var e = await Assert.ThrowsAsync<AtlasIncompatibleException>(() => rig.Client.SearchAsync("a"));
        Assert.Contains("2.0", e.Message);
        Assert.DoesNotContain(rig.Api, _ => true);                               // nothing but /site was asked
    }

    [Fact]
    public async Task A_missing_required_capability_is_refused_and_named()
    {
        using var rig = new Rig(configure: o => o.RequiredCapabilities.Add("books"),
            site: Rig.SiteWith("{\"version\":\"1.0\",\"min_supported\":\"1.0\",\"capabilities\":[\"etag\"],\"deprecations\":[]}"));
        var e = await Assert.ThrowsAsync<AtlasIncompatibleException>(() => rig.Client.SearchAsync("a"));
        Assert.Equal(new[] { "books" }, e.MissingCapabilities);
        Assert.Contains("books", e.Message);
    }

    [Fact]
    public async Task A_required_capability_is_present_so_the_call_goes_through()
    {
        using var rig = new Rig(configure: o => o.RequiredCapabilities.Add("books"),
            site: Rig.SiteWith("{\"version\":\"1.4\",\"min_supported\":\"1.0\",\"capabilities\":[\"books\",\"future_thing\"],\"deprecations\":[]}"));
        rig.Handler.On("GET", "/api/v2/games", _ => Reply.Json("{\"results\":[]}"));
        await rig.Client.SearchAsync("a");
    }

    [Fact]
    public async Task An_atlas_without_client_api_works_by_default_and_is_refused_when_required()
    {
        using (var lenient = new Rig())
        {
            lenient.Handler.On("GET", "/api/v2/games", _ => Reply.Json("{\"results\":[]}"));
            await lenient.Client.SearchAsync("a");                              // today's Atlas (fixtures/games/site.json has no client_api)
        }
        using var strict = new Rig(configure: o => o.RequireClientApi = true);
        var e = await Assert.ThrowsAsync<AtlasIncompatibleException>(() => strict.Client.SearchAsync("a"));
        Assert.Contains("client_api", e.Message);
    }

    [Fact]
    public async Task A_required_capability_on_an_atlas_that_cannot_say_is_refused()
    {
        using var rig = new Rig(configure: o => o.RequiredCapabilities.Add("books"));
        await Assert.ThrowsAsync<AtlasIncompatibleException>(() => rig.Client.SearchAsync("a"));
    }

    // ---- relations / artwork list -----------------------------------------------------------------------------

    [Fact]
    public async Task Relations_and_artwork_endpoints()
    {
        using var rig = new Rig();
        rig.Handler.On("GET", "/api/v2/games/" + PortalId + "/relations", _ => Reply.Json(Fixtures.Text("fixtures/games/relations-portal.json")));
        rig.Handler.On("GET", "/api/v2/games/" + PortalId + "/artwork", _ => Reply.Json("{\"id\":\"" + PortalId + "\",\"results\":[{\"id\":\"1\",\"kind\":\"cover\"}]}"));
        Assert.Single((await rig.Client.GetRelationsAsync(PortalId)).Nodes);
        Assert.Equal("cover", Assert.Single((await rig.Client.GetArtworkAsync(PortalId)).Items).Kind);
    }
}
