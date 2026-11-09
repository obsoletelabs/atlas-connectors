using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Atlas.Client
{
    /// <summary>What the user needs to see while pairing: show <see cref="UserCode"/> (and a QR of <see cref="VerifyUrl"/>).</summary>
    public sealed class PairingSession
    {
        public string UserCode { get; set; }
        public string VerifyUrl { get; set; }
        /// <summary>Secret of this client; never display it, never log it.</summary>
        public string PollToken { get; set; }
        public int ExpiresInSeconds { get; set; }
        /// <summary>Seconds between polls. At least 5: Atlas answers <c>slow_down</c> to anything faster.</summary>
        public int IntervalSeconds { get; set; }
        public override string ToString() => "PairingSession(" + UserCode + ")";   // poll token deliberately absent
    }

    public enum PairingStatus { Pending, SlowDown, Approved, Denied, Expired }

    public sealed class PairingPoll
    {
        public PairingStatus Status { get; set; }
        /// <summary>Only when approved: the personal read token. Store it protected (DPAPI on Playnite) and never echo it back.</summary>
        public string Token { get; set; }
        public override string ToString() => "PairingPoll(" + Status + ")";       // token deliberately absent
    }

    /// <summary>
    /// The pairing (device-code) flow, CONTRACT-C15 section 10.1: <c>POST /client/pair</c> then <c>POST /client/pair/poll</c>.
    /// NOTE: Atlas ships this in PR A24. The request and poll-answer member names below follow the contract text and the OAuth
    /// device-grant shape; they are provisional until A24 publishes them in <c>openapi-client-v1.json</c> (see docs/CLIENT-NOTES.md).
    /// </summary>
    public sealed partial class AtlasClient
    {
        private const int MinPollSeconds = 5;

        /// <summary>Ask Atlas for a code. The pairing calls are never retried automatically: a lost answer must not open a second pairing.</summary>
        public async Task<PairingSession> StartPairingAsync(string clientLabel, CancellationToken ct = default(CancellationToken))
        {
            if (string.IsNullOrWhiteSpace(clientLabel)) throw new ArgumentException("A label such as \"Jellyfin on nas\" is required.", nameof(clientLabel));
            var body = JsonNode.Object(new[]
            {
                new KeyValuePair<string, JsonNode>("label", JsonNode.Str(clientLabel.Length > 80 ? clientLabel.Substring(0, 80) : clientLabel)),
                new KeyValuePair<string, JsonNode>("client", JsonNode.Str(_o.HostName)),
            });
            var reply = await SendAsync(new Req { Method = HttpMethod.Post, Path = "client/pair", Body = body, Auth = false }, ct).ConfigureAwait(false);
            var n = Parse(reply);
            var code = n["code"].AsString() ?? n["user_code"].AsString();
            var poll = n["poll_token"].AsString();
            if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(poll))
                throw new AtlasProtocolException("Atlas answered the pairing request without a code and a poll token.");
            var interval = n["interval"].AsLong();
            return new PairingSession
            {
                UserCode = code,
                VerifyUrl = n["verify_url"].AsString(),
                PollToken = poll,
                ExpiresInSeconds = (int)Math.Max(1, Math.Min(n["expires_in"].AsLong() ?? 600, 3600)),
                IntervalSeconds = (int)Math.Max(MinPollSeconds, Math.Min(interval ?? MinPollSeconds, 60)),
            };
        }

        /// <summary>One poll. A 429 or the RFC 8628 error codes are mapped to a status instead of thrown.</summary>
        public async Task<PairingPoll> PollPairingAsync(string pollToken, CancellationToken ct = default(CancellationToken))
        {
            if (string.IsNullOrEmpty(pollToken)) throw new ArgumentException("pollToken is required.", nameof(pollToken));
            var body = JsonNode.Object(new[] { new KeyValuePair<string, JsonNode>("poll_token", JsonNode.Str(pollToken)) });
            Reply reply;
            try
            {
                reply = await SendAsync(new Req { Method = HttpMethod.Post, Path = "client/pair/poll", Body = body, Auth = false }, ct).ConfigureAwait(false);
            }
            catch (AtlasRateLimitedException) { return new PairingPoll { Status = PairingStatus.SlowDown }; }
            catch (AtlasApiException e) when (e.Status < 500)
            {
                switch (e.Code)
                {
                    case "slow_down": return new PairingPoll { Status = PairingStatus.SlowDown };
                    case "authorization_pending": return new PairingPoll { Status = PairingStatus.Pending };
                    case "access_denied": return new PairingPoll { Status = PairingStatus.Denied };
                    case "expired_token": return new PairingPoll { Status = PairingStatus.Expired };
                    default: throw;
                }
            }
            var n = Parse(reply);
            var token = n["token"].AsString() ?? n["access_token"].AsString();
            var status = n["status"].AsString() ?? n["error"].AsString();
            switch (status)
            {
                case "slow_down": return new PairingPoll { Status = PairingStatus.SlowDown };
                case "denied":
                case "access_denied": return new PairingPoll { Status = PairingStatus.Denied };
                case "expired":
                case "expired_token": return new PairingPoll { Status = PairingStatus.Expired };
                case "approved":
                    if (string.IsNullOrEmpty(token)) throw new AtlasProtocolException("Atlas approved the pairing without sending a token.");
                    return new PairingPoll { Status = PairingStatus.Approved, Token = token };
                case "pending":
                case "authorization_pending":
                case null:
                    // a token with no status word still means approved
                    return string.IsNullOrEmpty(token) ? new PairingPoll { Status = PairingStatus.Pending }
                                                       : new PairingPoll { Status = PairingStatus.Approved, Token = token };
                default:
                    return new PairingPoll { Status = PairingStatus.Pending };   // an unknown future status: keep waiting, never fail
            }
        }

        /// <summary>
        /// The whole flow: start, hand the session to <paramref name="show"/> (display the code and QR), poll no faster than every
        /// 5 s (+5 s after each slow_down), and return the token. Throws <see cref="AtlasPairingException"/> when denied or
        /// expired. The caller stores the token; this class keeps nothing.
        /// </summary>
        public async Task<string> PairAsync(string clientLabel, Action<PairingSession> show, CancellationToken ct = default(CancellationToken))
        {
            if (show == null) throw new ArgumentNullException(nameof(show));
            var session = await StartPairingAsync(clientLabel, ct).ConfigureAwait(false);
            show(session);
            var interval = session.IntervalSeconds;
            long waited = 0;
            while (true)
            {
                await _o.Delay(TimeSpan.FromSeconds(interval), ct).ConfigureAwait(false);
                waited += interval;
                var poll = await PollPairingAsync(session.PollToken, ct).ConfigureAwait(false);
                switch (poll.Status)
                {
                    case PairingStatus.Approved: return poll.Token;
                    case PairingStatus.Denied: throw new AtlasPairingException("denied", "The pairing was denied on Atlas.");
                    case PairingStatus.Expired: throw new AtlasPairingException("expired", "The pairing code expired. Start again.");
                    case PairingStatus.SlowDown: interval += MinPollSeconds; break;
                }
                if (waited >= session.ExpiresInSeconds + interval)
                    throw new AtlasPairingException("expired", "The pairing code expired. Start again.");
            }
        }
    }
}
