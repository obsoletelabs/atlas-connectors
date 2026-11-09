using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Atlas.Client
{
    public sealed partial class AtlasClient
    {
        private sealed class Req
        {
            public HttpMethod Method = HttpMethod.Get;
            public string Path;                  // relative to /api/v2/ (ignored when Url is set)
            public Uri Url;
            public JsonNode Body;
            /// <summary>Safe to repeat (GET, read-only POST). Never set for the pairing calls.</summary>
            public bool Retryable;
            public string IfNoneMatch;
            public bool Allow304;
            public bool Auth = true;             // send the bearer token
            public bool AtlasOrigin = true;      // an Atlas answer: send Atlas-Wire, read it back
            public bool AllowRedirect;           // hand a 3xx back to the caller instead of failing
            public long MaxBytes = MaxJsonBytes;
            public string Accept = "application/json";
        }

        private sealed class Reply
        {
            public int Status;
            public byte[] Body = new byte[0];
            public string ETag;
            public string Location;
            public string ContentType;
            public string RequestId;
            public string WireHeader;
            public TimeSpan? RetryAfter;
        }

        private async Task<Reply> SendAsync(Req r, CancellationToken ct)
        {
            var url = r.Url ?? new Uri(_api, r.Path);
            var attempt = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                Reply reply = null;
                Exception net = null;
                try { reply = await OnceAsync(r, url, ct).ConfigureAwait(false); }
                catch (HttpRequestException e) { net = e; }
                catch (OperationCanceledException e) when (!ct.IsCancellationRequested) { net = new TimeoutException("The request timed out.", e); }
                catch (IOException e) { net = e; }

                if (net != null)
                {
                    if (!r.Retryable || attempt >= _o.MaxRetries)
                        throw new AtlasNetworkException("Could not reach Atlas at " + _origin.Host + " (" + net.GetType().Name + ").", net);
                    await _o.Delay(Backoff(attempt), ct).ConfigureAwait(false);
                    attempt++;
                    continue;
                }

                var s = reply.Status;
                if ((s >= 200 && s < 300) || (s == 304 && r.Allow304) || (r.AllowRedirect && (s == 307 || s == 308)))
                {
                    if (r.AtlasOrigin && s != 307 && s != 308) _lastWire = Atlas.Client.WireHeader.ReadResponse(reply.WireHeader, _o.Wire);
                    return reply;
                }

                if (r.Retryable && attempt < _o.MaxRetries && (s == 429 || s == 502 || s == 503 || s == 504))
                {
                    if (reply.RetryAfter.HasValue && reply.RetryAfter.Value > _o.MaxRetryAfter) throw MapError(reply);
                    await _o.Delay(reply.RetryAfter ?? Backoff(attempt), ct).ConfigureAwait(false);
                    attempt++;
                    continue;
                }
                throw MapError(reply);
            }
        }

        private async Task<Reply> OnceAsync(Req r, Uri url, CancellationToken ct)
        {
            using (var req = new HttpRequestMessage(r.Method, url))
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                cts.CancelAfter(_o.RequestTimeout);
                req.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
                req.Headers.TryAddWithoutValidation("Accept", r.Accept);
                if (r.AtlasOrigin) req.Headers.TryAddWithoutValidation(Atlas.Client.WireHeader.Name, Atlas.Client.WireHeader.Format(_o.Wire));
                if (r.Auth)
                {
                    var token = _o.TokenProvider == null ? null : _o.TokenProvider();
                    if (!string.IsNullOrEmpty(token)) req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
                }
                if (!string.IsNullOrEmpty(r.IfNoneMatch)) req.Headers.TryAddWithoutValidation("If-None-Match", r.IfNoneMatch);
                if (r.Body != null) req.Content = new StringContent(_json.Write(r.Body), new UTF8Encoding(false), "application/json");

                using (var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false))
                {
                    var reply = new Reply
                    {
                        Status = (int)resp.StatusCode,
                        ETag = First(resp.Headers, "ETag"),
                        RequestId = First(resp.Headers, "X-Request-ID"),
                        WireHeader = First(resp.Headers, Atlas.Client.WireHeader.Name),
                        Location = resp.Headers.Location == null ? null : resp.Headers.Location.OriginalString,
                        ContentType = resp.Content == null || resp.Content.Headers.ContentType == null ? null : resp.Content.Headers.ContentType.MediaType,
                        RetryAfter = RetryAfter(resp.Headers.RetryAfter),
                    };
                    if (resp.Content != null) reply.Body = await ReadCappedAsync(resp.Content, r.MaxBytes, cts.Token).ConfigureAwait(false);
                    return reply;
                }
            }
        }

        private static async Task<byte[]> ReadCappedAsync(HttpContent content, long max, CancellationToken ct)
        {
            var declared = content.Headers.ContentLength;
            if (declared.HasValue && declared.Value > max)
                throw new AtlasProtocolException("The answer is larger than the allowed " + max.ToString(CultureInfo.InvariantCulture) + " bytes.");
            using (var stream = await content.ReadAsStreamAsync().ConfigureAwait(false))
            using (var ms = new MemoryStream())
            {
                var buffer = new byte[16 * 1024];
                int n;
                while ((n = await stream.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false)) > 0)
                {
                    if (ms.Length + n > max)
                        throw new AtlasProtocolException("The answer is larger than the allowed " + max.ToString(CultureInfo.InvariantCulture) + " bytes.");
                    ms.Write(buffer, 0, n);
                }
                return ms.ToArray();
            }
        }

        private static string First(HttpHeaders headers, string name)
        {
            IEnumerable<string> values;
            return headers.TryGetValues(name, out values) ? values.FirstOrDefault() : null;
        }

        private static TimeSpan? RetryAfter(RetryConditionHeaderValue h)
        {
            if (h == null) return null;
            if (h.Delta.HasValue) return h.Delta.Value < TimeSpan.Zero ? TimeSpan.Zero : h.Delta.Value;
            if (h.Date.HasValue)
            {
                var d = h.Date.Value - DateTimeOffset.UtcNow;
                return d < TimeSpan.Zero ? TimeSpan.Zero : d;
            }
            return null;
        }

        /// <summary>Exponential backoff with jitter: base * 2^attempt, capped, scaled into [0.5, 1).</summary>
        private TimeSpan Backoff(int attempt)
        {
            var ms = _o.BaseDelay.TotalMilliseconds * Math.Pow(2, Math.Min(attempt, 20));
            ms = Math.Min(ms, _o.MaxDelay.TotalMilliseconds);
            var j = _o.Jitter == null ? 1.0 : _o.Jitter();
            return TimeSpan.FromMilliseconds(ms * (0.5 + 0.5 * Math.Max(0, Math.Min(1.0, j))));
        }

        /// <summary>Turn an error answer into the right exception. Error text comes from Atlas and carries no token.</summary>
        private Exception MapError(Reply reply)
        {
            var s = reply.Status;
            if (s >= 300 && s < 400)
                return new AtlasProtocolException("Atlas answered with an unexpected redirect (" + s.ToString(CultureInfo.InvariantCulture) + "); redirects are not followed.");

            JsonNode root = JsonNode.Null;
            try { root = _json.Parse(new UTF8Encoding(false, false).GetString(reply.Body)); }
            catch (Exception) { }

            var err = root["error"];
            var code = err["code"].AsString() ?? root["code"].AsString();
            var message = err["message"].AsString() ?? root["detail"].AsString() ?? root["message"].AsString();
            var requestId = err["request_id"].AsString() ?? reply.RequestId;

            if (s == 409 && code == "wire_unsupported")
            {
                return new AtlasWireUnsupportedException(root["reason"].AsString(), root["action"].AsString(),
                    root["detail"].AsString() ?? message ?? "This Atlas does not speak a wire version this connector speaks.",
                    Range(root["server"]["wire"]), Range(root["client"]["wire"]), requestId);
            }
            if (s == 429)
                return new AtlasRateLimitedException(message ?? "Atlas is rate limiting this connector.", reply.RetryAfter, requestId);
            if (s == 401) return new AtlasApiException(s, code ?? "unauthorized", message ?? "Atlas refused the token (401). Check or renew it.", requestId);
            if (s == 403) return new AtlasApiException(s, code ?? "forbidden", message ?? "The token may not do this (403).", requestId);
            return new AtlasApiException(s, code ?? "http_" + s.ToString(CultureInfo.InvariantCulture),
                message ?? "Atlas answered HTTP " + s.ToString(CultureInfo.InvariantCulture) + ".", requestId);
        }

        private static WireRange? Range(JsonNode n)
        {
            var min = n["min"].AsLong();
            var max = n["max"].AsLong();
            return min.HasValue && max.HasValue ? (WireRange?)new WireRange((int)min.Value, (int)max.Value) : null;
        }
    }
}
