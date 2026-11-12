using Atlas.Client;
using Xunit;

namespace Atlas.Client.Tests;

/// <summary>
/// The device-code flow against a scripted server, in the shape of contract/openapi-client-v1.json and the recorded
/// contract/fixtures/pairing answers (client-api 1.0), and the rules from CONTRACT-C15 10.1.
/// </summary>
public class PairingTests
{
    private const string PairBody = "{\"code\":\"K7QM-4XTD\",\"verify_url\":\"https://atlas.test/pair\","
        + "\"verify_url_complete\":\"https://atlas.test/pair?code=K7QM-4XTD\",\"qr_svg\":\"<svg/>\",\"poll_token\":\"poll-SECRET-abcdef\","
        + "\"expires_in\":600,\"interval\":5}";

    [Fact]
    public async Task The_recorded_pairing_answers_are_understood()
    {
        using var rig = new Rig();
        rig.Handler.On("POST", "/api/v2/client/pair", _ => Reply.Json(Fixtures.Body("fixtures/pairing/pair-started.json")));
        rig.Handler.On("POST", "/api/v2/client/pair/poll", _ => Reply.Json(Fixtures.Body("fixtures/pairing/poll-pending.json")));

        var s = await rig.Client.StartPairingAsync("Jellyfin on nas");
        Assert.Equal("ABCD-EFGH", s.UserCode);
        Assert.Equal("https://atlas.example.org/pair", s.VerifyUrl);
        Assert.Equal("https://atlas.example.org/pair?code=ABCD-EFGH", s.VerifyUrlComplete);
        Assert.StartsWith("<svg", s.QrSvg);
        Assert.Equal(600, s.ExpiresInSeconds);
        Assert.Equal(5, s.IntervalSeconds);
        Assert.Equal(PairingStatus.Pending, (await rig.Client.PollPairingAsync(s.PollToken)).Status);

        // the client sends exactly the recorded requests
        var json = new SystemTextJsonAdapter();
        Assert.True(Adapters.Same(json.Parse(Fixtures.Request("fixtures/pairing/pair-started.json")), json.Parse(rig.Api.First().Body)), rig.Api.First().Body);
        Assert.True(Adapters.Same(json.Parse(Fixtures.Request("fixtures/pairing/poll-pending.json")), json.Parse(rig.Api.Last().Body)), rig.Api.Last().Body);
    }

    [Fact]
    public async Task Request_bodies_fit_the_openapi_schemas_which_refuse_unknown_members()
    {
        var spec = System.Text.Json.Nodes.JsonNode.Parse(Fixtures.Text("openapi-client-v1.json"));
        var schemas = spec["components"]["schemas"];
        using var rig = new Rig();
        rig.Handler.On("POST", "/api/v2/client/pair", _ => Reply.Json(PairBody));
        rig.Handler.On("POST", "/api/v2/client/pair/poll", Poll(new { status = "pending" }));
        rig.Handler.On("POST", "/api/v2/games/batch", _ => Reply.Json("{\"results\":[]}"));
        await rig.Client.StartPairingAsync(new string('L', 200));
        await rig.Client.PollPairingAsync("p");
        await rig.Client.GetRecordsAsync(new[] { "atlas:a" });

        foreach (var (path, schema) in new[] { ("/client/pair", "PairBody"), ("/client/pair/poll", "PollBody"), ("/games/batch", "BatchBody") })
        {
            var s = schemas[schema];
            Assert.False((bool)s["additionalProperties"]);
            var sent = System.Text.Json.Nodes.JsonNode.Parse(rig.Api.Single(r => r.Uri.AbsolutePath.EndsWith(path, StringComparison.Ordinal)).Body).AsObject();
            foreach (var member in sent)
            {
                var prop = s["properties"][member.Key];
                Assert.True(prop != null, schema + " has no member " + member.Key + " (Atlas would answer 422)");
                if (prop["maxLength"] != null) Assert.InRange(member.Value.GetValue<string>().Length, 1, (int)prop["maxLength"]);
            }
            foreach (var required in s["required"].AsArray()) Assert.True(sent.ContainsKey((string)required), schema + " needs " + required);
        }
        Assert.Equal(AtlasClient.MaxPairLabel, (int)schemas["PairBody"]["properties"]["label"]["maxLength"]);
    }

    private static FakeHandler.Responder Poll(object body, int status = 200, params (string, string)[] headers) =>
        _ => Reply.Status(status, body, headers);

    [Fact]
    public async Task Happy_path_polls_no_faster_than_5s_and_backs_off_on_slow_down()
    {
        using var rig = new Rig("newtonsoft");
        rig.Handler.On("POST", "/api/v2/client/pair", _ => Reply.Json(PairBody));
        rig.Handler.Sequence("POST", "/api/v2/client/pair/poll",
            Poll(new { status = "pending", interval = 5, expires_in = 590 }),
            Poll(new { status = "slow_down", interval = 10 }),
            Poll(new { status = "approved", token = "new-personal-read-token", token_id = "tid-1", name = "Jellyfin on nas", scopes = new[] { "read" } }));

        PairingSession shown = null;
        var token = await rig.Client.PairAsync("Jellyfin on nas", s => shown = s);

        Assert.Equal("new-personal-read-token", token);
        Assert.Equal("K7QM-4XTD", shown.UserCode);
        Assert.Equal("https://atlas.test/pair", shown.VerifyUrl);
        Assert.Equal("https://atlas.test/pair?code=K7QM-4XTD", shown.VerifyUrlComplete);
        Assert.Equal("<svg/>", shown.QrSvg);
        Assert.Equal(new[] { 5, 5, 10 }, rig.Delays.Select(d => (int)d.TotalSeconds));       // wait, poll, wait, poll(slow_down), wait+5, poll

        var start = new SystemTextJsonAdapter().Parse(rig.Api.First().Body);
        Assert.Equal("Jellyfin on nas", start["label"].AsString());
        Assert.False(start.Has("client"));                                                  // PairBody refuses unknown members
        var poll = new SystemTextJsonAdapter().Parse(rig.Api.Skip(1).First().Body);
        Assert.Equal("poll-SECRET-abcdef", poll["poll_token"].AsString());
    }

    [Fact]
    public async Task Slow_down_uses_the_interval_atlas_names_and_approval_carries_the_token_details()
    {
        using var rig = new Rig();
        rig.Handler.On("POST", "/api/v2/client/pair", _ => Reply.Json(PairBody));
        rig.Handler.Sequence("POST", "/api/v2/client/pair/poll",
            Poll(new { status = "slow_down", interval = 25 }),
            Poll(new { status = "approved", token = "t", token_id = "tid-9", name = "x", scopes = new[] { "read" } }));
        var slow = await rig.Client.PollPairingAsync("p");
        Assert.Equal(PairingStatus.SlowDown, slow.Status);
        Assert.Equal(25, slow.IntervalSeconds);
        var ok = await rig.Client.PollPairingAsync("p");
        Assert.Equal("tid-9", ok.TokenId);
        Assert.Equal(new[] { "read" }, ok.Scopes);
        Assert.DoesNotContain("\"t\"", ok.ToString());

        using var flow = new Rig();
        flow.Handler.On("POST", "/api/v2/client/pair", _ => Reply.Json(PairBody));
        flow.Handler.Sequence("POST", "/api/v2/client/pair/poll", Poll(new { status = "slow_down", interval = 25 }), Poll(new { status = "approved", token = "t" }));
        await flow.Client.PairAsync("x", _ => { });
        Assert.Equal(new[] { 5, 25 }, flow.Delays.Select(d => (int)d.TotalSeconds));
    }

    [Fact]
    public async Task A_denial_for_the_token_limit_says_what_to_do()
    {
        using var rig = new Rig();
        rig.Handler.On("POST", "/api/v2/client/pair", _ => Reply.Json(PairBody));
        rig.Handler.On("POST", "/api/v2/client/pair/poll", Poll(new { status = "denied", reason = "token_limit" }));
        var e = await Assert.ThrowsAsync<AtlasPairingException>(() => rig.Client.PairAsync("x", _ => { }));
        Assert.Equal("denied", e.Outcome);
        Assert.Contains("Revoke", e.Message);
    }

    [Fact]
    public async Task Pairing_calls_send_no_bearer_token_and_no_secret_in_any_url()
    {
        using var rig = new Rig();
        rig.Handler.On("POST", "/api/v2/client/pair", _ => Reply.Json(PairBody));
        rig.Handler.On("POST", "/api/v2/client/pair/poll", Poll(new { status = "approved", token = "t" }));
        await rig.Client.PairAsync("x", _ => { });
        Assert.All(rig.Handler.Requests, r =>
        {
            Assert.False(r.HasHeader("Authorization"));
            Assert.DoesNotContain("SECRET", r.Uri.ToString());
            Assert.DoesNotContain("poll-", r.Uri.ToString());
        });
    }

    [Fact]
    public async Task The_session_and_poll_result_never_print_their_secrets()
    {
        using var rig = new Rig();
        rig.Handler.On("POST", "/api/v2/client/pair", _ => Reply.Json(PairBody));
        var s = await rig.Client.StartPairingAsync("x");
        Assert.DoesNotContain("SECRET", s.ToString());
        Assert.DoesNotContain("SECRET", new PairingPoll { Status = PairingStatus.Approved, Token = "tok-SECRET" }.ToString());
    }

    [Fact]
    public async Task Interval_has_a_floor_of_5s_and_a_ceiling()
    {
        using var rig = new Rig();
        rig.Handler.On("POST", "/api/v2/client/pair", _ => Reply.Json("{\"code\":\"A\",\"poll_token\":\"p\",\"expires_in\":99999,\"interval\":1}"));
        var s = await rig.Client.StartPairingAsync("x");
        Assert.Equal(5, s.IntervalSeconds);
        Assert.Equal(3600, s.ExpiresInSeconds);
    }

    [Fact]
    public async Task Start_is_not_retried_so_a_lost_answer_cannot_open_a_second_pairing()
    {
        using var rig = new Rig();
        rig.Handler.On("POST", "/api/v2/client/pair", _ => Reply.Status(503, new { error = new { code = "unavailable", message = "later" } }));
        await Assert.ThrowsAsync<AtlasApiException>(() => rig.Client.StartPairingAsync("x"));
        Assert.Single(rig.Api);
        Assert.Empty(rig.Delays);
    }

    [Fact]
    public async Task Poll_is_not_retried_on_a_network_error_because_the_token_is_handed_out_once()
    {
        using var rig = new Rig();
        var calls = 0;
        rig.Handler.On("POST", "/api/v2/client/pair/poll", _ => { calls++; throw new HttpRequestException("reset"); });
        await Assert.ThrowsAsync<AtlasNetworkException>(() => rig.Client.PollPairingAsync("p"));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Denied_and_expired_raise_a_pairing_exception_with_the_outcome()
    {
        foreach (var (word, outcome) in new[] { ("denied", "denied"), ("expired", "expired"), ("access_denied", "denied"), ("expired_token", "expired") })
        {
            using var rig = new Rig();
            rig.Handler.On("POST", "/api/v2/client/pair", _ => Reply.Json(PairBody));
            rig.Handler.On("POST", "/api/v2/client/pair/poll", Poll(new { status = word }));
            var e = await Assert.ThrowsAsync<AtlasPairingException>(() => rig.Client.PairAsync("x", _ => { }));
            Assert.Equal(outcome, e.Outcome);
        }
    }

    [Fact]
    public async Task Rfc8628_style_error_codes_in_a_400_are_understood()
    {
        using var rig = new Rig();
        rig.Handler.Sequence("POST", "/api/v2/client/pair/poll",
            Poll(new { error = new { code = "authorization_pending", message = "wait" } }, 400),
            Poll(new { error = new { code = "slow_down", message = "slower" } }, 400),
            Poll(new { error = new { code = "access_denied", message = "no" } }, 403),
            Poll(new { error = new { code = "expired_token", message = "late" } }, 400));
        Assert.Equal(PairingStatus.Pending, (await rig.Client.PollPairingAsync("p")).Status);
        Assert.Equal(PairingStatus.SlowDown, (await rig.Client.PollPairingAsync("p")).Status);
        Assert.Equal(PairingStatus.Denied, (await rig.Client.PollPairingAsync("p")).Status);
        Assert.Equal(PairingStatus.Expired, (await rig.Client.PollPairingAsync("p")).Status);
    }

    [Fact]
    public async Task A_429_on_poll_means_slow_down_not_failure()
    {
        using var rig = new Rig();
        rig.Handler.On("POST", "/api/v2/client/pair/poll", Poll(new { error = new { code = "rate_limited", message = "x" } }, 429, ("Retry-After", "7")));
        Assert.Equal(PairingStatus.SlowDown, (await rig.Client.PollPairingAsync("p")).Status);
    }

    [Fact]
    public async Task A_code_that_is_never_approved_expires_locally()
    {
        using var rig = new Rig();
        rig.Handler.On("POST", "/api/v2/client/pair", _ => Reply.Json("{\"code\":\"A\",\"poll_token\":\"p\",\"expires_in\":20}"));
        rig.Handler.On("POST", "/api/v2/client/pair/poll", Poll(new { status = "pending" }));
        var e = await Assert.ThrowsAsync<AtlasPairingException>(() => rig.Client.PairAsync("x", _ => { }));
        Assert.Equal("expired", e.Outcome);
        Assert.InRange(rig.Delays.Count, 4, 6);
    }

    [Fact]
    public async Task An_unknown_future_status_keeps_waiting_and_approval_without_a_token_is_an_error()
    {
        using var rig = new Rig();
        rig.Handler.Sequence("POST", "/api/v2/client/pair/poll", Poll(new { status = "reviewing_by_robots" }), Poll(new { status = "approved" }));
        Assert.Equal(PairingStatus.Pending, (await rig.Client.PollPairingAsync("p")).Status);
        await Assert.ThrowsAsync<AtlasProtocolException>(() => rig.Client.PollPairingAsync("p"));
    }

    [Fact]
    public async Task A_start_answer_without_code_or_poll_token_is_a_protocol_error()
    {
        using var rig = new Rig();
        rig.Handler.On("POST", "/api/v2/client/pair", _ => Reply.Json("{\"verify_url\":\"x\"}"));
        await Assert.ThrowsAsync<AtlasProtocolException>(() => rig.Client.StartPairingAsync("x"));
    }

    [Fact]
    public async Task Cancellation_stops_the_wait()
    {
        using var rig = new Rig(configure: o => o.Delay = (t, ct) => { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; });
        rig.Handler.On("POST", "/api/v2/client/pair", _ => Reply.Json(PairBody));
        using var cts = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Client.PairAsync("x", _ => cts.Cancel(), cts.Token));
    }

    [Fact]
    public async Task A_label_is_required_and_truncated()
    {
        using var rig = new Rig();
        rig.Handler.On("POST", "/api/v2/client/pair", _ => Reply.Json(PairBody));
        await Assert.ThrowsAsync<ArgumentException>(() => rig.Client.StartPairingAsync(" "));
        await rig.Client.StartPairingAsync(new string('L', 200));
        Assert.Equal(64, new SystemTextJsonAdapter().Parse(rig.Api.Single().Body)["label"].AsString().Length);
    }
}
