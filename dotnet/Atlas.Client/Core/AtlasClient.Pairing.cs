using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Atlas.Client
{
    /// <summary>
    /// What the user needs to see while pairing: show <see cref="UserCode"/> next to <see cref="QrSvg"/>, and offer
    /// <see cref="VerifyUrlComplete"/> as the link to open (it carries the code, so the person only approves).
    /// </summary>
    public sealed class PairingSession
    {
        public string UserCode { get; set; }
        /// <summary>The approval page without the code (<c>.../pair</c>), for a person who types the code in.</summary>
        public string VerifyUrl { get; set; }
        /// <summary>The approval page with the code filled in (<c>.../pair?code=XXXX-XXXX</c>); falls back to <see cref="VerifyUrl"/>.</summary>
        public string VerifyUrlComplete { get; set; }
        /// <summary>An inline SVG QR code of <see cref="VerifyUrlComplete"/>, drawn by Atlas (dark on white). Null when Atlas sent none.
        /// Show it as an image (never inject it as live markup into a page that holds a token).</summary>
        public string QrSvg { get; set; }
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
        /// <summary>Only when approved: the id of that token on the profile of the approving account (safe to show and log).</summary>
        public string TokenId { get; set; }
        /// <summary>Only when approved: the token name on that profile.</summary>
        public string Name { get; set; }
        /// <summary>Only when approved: the token scopes (<c>read</c>).</summary>
        public IReadOnlyList<string> Scopes { get; set; } = Array.Empty<string>();
        /// <summary>With <see cref="PairingStatus.SlowDown"/>: the new interval Atlas asks for, in seconds; null when it gave none.</summary>
        public int? IntervalSeconds { get; set; }
        /// <summary>With <see cref="PairingStatus.Denied"/>: why, when Atlas says (<c>token_limit</c>: the account holds too many tokens).</summary>
        public string Reason { get; set; }
        public override string ToString() => "PairingPoll(" + Status + ")";       // token deliberately absent
    }

    /// <summary>
    /// The pairing (device-code) flow, CONTRACT-C15 section 10.1: <c>POST /client/pair</c> then <c>POST /client/pair/poll</c>, as in
    /// <c>contract/openapi-client-v1.json</c> (client-api 1.0). The poll always answers HTTP 200 with a <c>status</c> (<c>pending</c>,
    /// <c>slow_down</c> with a new <c>interval</c>, <c>approved</c> with the token, <c>denied</c> with an optional <c>reason</c>,
    /// <c>expired</c>; an unknown poll token is <c>expired</c>). Both calls are on the anonymous per-address budget (429 +
    /// Retry-After). The RFC 8628 error codes are still understood in case a proxy or a later Atlas answers that way.
    /// </summary>
    public sealed partial class AtlasClient
    {
        private const int MinPollSeconds = 5;
        /// <summary>PairBody.label maxLength in the OpenAPI file; Atlas refuses longer labels (422).</summary>
        public const int MaxPairLabel = 64;

        /// <summary>
        /// The label as Atlas accepts it (PairBody: "Printable, at most 64 characters", checked with Python <c>str.isprintable</c>):
        /// control, format, private-use, unassigned and lone surrogate characters are dropped, every other separator (tab, newline,
        /// no-break space, ideographic space...) becomes one ASCII space, runs of spaces collapse, and the result is trimmed and cut
        /// to <see cref="MaxPairLabel"/> UTF-16 units without splitting a surrogate pair (so never more than 64 code points).
        /// A machine name such as "nas\u0007" or "Living\u00a0room" would otherwise be refused with 422.
        /// </summary>
        public static string PairLabel(string raw)
        {
            if (raw == null) return string.Empty;
            var sb = new StringBuilder(raw.Length);
            var space = false;
            for (var i = 0; i < raw.Length; i++)
            {
                var c = raw[i];
                var pair = char.IsHighSurrogate(c) && i + 1 < raw.Length && char.IsLowSurrogate(raw[i + 1]);
                var cat = CharUnicodeInfo.GetUnicodeCategory(raw, i);
                var drop = cat == UnicodeCategory.Control || cat == UnicodeCategory.Format || cat == UnicodeCategory.PrivateUse
                           || cat == UnicodeCategory.OtherNotAssigned || cat == UnicodeCategory.Surrogate;
                var sep = cat == UnicodeCategory.SpaceSeparator || cat == UnicodeCategory.LineSeparator
                          || cat == UnicodeCategory.ParagraphSeparator || c == '\t' || c == '\n' || c == '\r';
                if (sep)
                {
                    space = sb.Length > 0;
                }
                else if (!drop)
                {
                    if (space) sb.Append(' ');
                    space = false;
                    sb.Append(c);
                    if (pair) sb.Append(raw[i + 1]);
                }
                if (pair) i++;
            }
            var label = sb.ToString();
            if (label.Length > MaxPairLabel)
                label = label.Substring(0, char.IsHighSurrogate(label[MaxPairLabel - 1]) ? MaxPairLabel - 1 : MaxPairLabel).TrimEnd(' ');
            return label;
        }

        /// <summary>Ask Atlas for a code. The pairing calls are never retried automatically: a lost answer must not open a second pairing.</summary>
        public async Task<PairingSession> StartPairingAsync(string clientLabel, CancellationToken ct = default(CancellationToken))
        {
            if (string.IsNullOrWhiteSpace(clientLabel)) throw new ArgumentException("A label such as \"Jellyfin on nas\" is required.", nameof(clientLabel));
            // PairBody has additionalProperties false: send `label` only (the host is already in the User-Agent).
            var label = PairLabel(clientLabel);
            if (label.Length == 0) throw new ArgumentException("The label has no printable characters.", nameof(clientLabel));
            var body = JsonNode.Object(new[] { new KeyValuePair<string, JsonNode>("label", JsonNode.Str(label)) });
            var reply = await SendAsync(new Req { Method = HttpMethod.Post, Path = "client/pair", Body = body, Auth = false }, ct).ConfigureAwait(false);
            var n = Parse(reply);
            var code = n["code"].AsString() ?? n["user_code"].AsString();
            var poll = n["poll_token"].AsString();
            if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(poll))
                throw new AtlasProtocolException("Atlas answered the pairing request without a code and a poll token.");
            var interval = n["interval"].AsLong();
            var verify = n["verify_url"].AsString();
            return new PairingSession
            {
                UserCode = code,
                VerifyUrl = verify,
                VerifyUrlComplete = n["verify_url_complete"].AsString() ?? verify,
                QrSvg = n["qr_svg"].AsString(),
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
                case "slow_down":
                    var next = n["interval"].AsLong();
                    return new PairingPoll
                    {
                        Status = PairingStatus.SlowDown,
                        IntervalSeconds = next.HasValue ? (int?)Math.Max(MinPollSeconds, Math.Min(next.Value, 60)) : null,
                    };
                case "denied":
                case "access_denied": return new PairingPoll { Status = PairingStatus.Denied, Reason = n["reason"].AsString() };
                case "expired":
                case "expired_token": return new PairingPoll { Status = PairingStatus.Expired };
                case "approved":
                    if (string.IsNullOrEmpty(token)) throw new AtlasProtocolException("Atlas approved the pairing without sending a token.");
                    return Approved(n, token);
                case "pending":
                case "authorization_pending":
                case null:
                    // a token with no status word still means approved
                    return string.IsNullOrEmpty(token) ? new PairingPoll { Status = PairingStatus.Pending } : Approved(n, token);
                default:
                    return new PairingPoll { Status = PairingStatus.Pending };   // an unknown future status: keep waiting, never fail
            }
        }

        private static PairingPoll Approved(JsonNode n, string token) => new PairingPoll
        {
            Status = PairingStatus.Approved, Token = token, TokenId = n["token_id"].AsString(), Name = n["name"].AsString(),
            Scopes = AtlasReader.Strings(n["scopes"]),
        };

        /// <summary>
        /// The whole flow: start, hand the session to <paramref name="show"/> (display the code and QR), poll no faster than every
        /// 5 s (after a slow_down, the interval Atlas names, or +5 s when it names none), and return the token. Throws <see cref="AtlasPairingException"/> when denied or
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
                    case PairingStatus.Denied:
                        throw new AtlasPairingException("denied", poll.Reason == "token_limit"
                            ? "The pairing was refused: that Atlas account already holds as many tokens as it may. Revoke one on the Atlas profile page and pair again."
                            : "The pairing was denied on Atlas.");
                    case PairingStatus.Expired: throw new AtlasPairingException("expired", "The pairing code expired. Start again.");
                    case PairingStatus.SlowDown: interval = Math.Max(interval + MinPollSeconds, poll.IntervalSeconds ?? 0); break;
                }
                if (waited >= session.ExpiresInSeconds + interval)
                    throw new AtlasPairingException("expired", "The pairing code expired. Start again.");
            }
        }
    }
}
