using Atlas.Client;
using Xunit;

namespace Atlas.Client.Tests;

/// <summary>
/// The device-code flow against a scripted server. The member names of the pair/poll bodies are PROVISIONAL (Atlas PR A24 has
/// not shipped them); these tests pin what the client does with the shape it assumes, and the rules from CONTRACT-C15 10.1.
/// </summary>
public class PairingTests
{
    private const string PairBody = "{\"code\":\"K7QM-4XTD\",\"verify_url\":\"https://atlas.test/pair?code=K7QM-4XTD\",\"poll_token\":\"poll-SECRET-abcdef\",\"expires_in\":600}";

    private static FakeHandler.Responder Poll(object body, int status = 200, params (string, string)[] headers) =>
        _ => Reply.Status(status, body, headers);

    [Fact]
    public async Task Happy_path_polls_no_faster_than_5s_and_backs_off_on_slow_down()
    {
        using var rig = new Rig("newtonsoft");
        rig.Handler.On("POST", "/api/v2/client/pair", _ => Reply.Json(PairBody));
        rig.Handler.Sequence("POST", "/api/v2/client/pair/poll",
            Poll(new { status = "pending" }),
            Poll(new { status = "slow_down" }),
            Poll(new { status = "approved", token = "new-personal-read-token" }));

        PairingSession shown = null;
        var token = await rig.Client.PairAsync("Jellyfin on nas", s => shown = s);

        Assert.Equal("new-personal-read-token", token);
        Assert.Equal("K7QM-4XTD", shown.UserCode);
        Assert.Equal("https://atlas.test/pair?code=K7QM-4XTD", shown.VerifyUrl);
        Assert.Equal(new[] { 5, 5, 10 }, rig.Delays.Select(d => (int)d.TotalSeconds));       // wait, poll, wait, poll(slow_down), wait+5, poll

        var start = new SystemTextJsonAdapter().Parse(rig.Api.First().Body);
        Assert.Equal("Jellyfin on nas", start["label"].AsString());
        Assert.Equal("jellyfin", start["client"].AsString());
        var poll = new SystemTextJsonAdapter().Parse(rig.Api.Skip(1).First().Body);
        Assert.Equal("poll-SECRET-abcdef", poll["poll_token"].AsString());
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
        Assert.Equal(80, new SystemTextJsonAdapter().Parse(rig.Api.Single().Body)["label"].AsString().Length);
    }
}
