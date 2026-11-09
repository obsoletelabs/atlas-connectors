using System;
using System.Collections.Generic;

namespace Atlas.Client
{
    /// <summary>Base of everything this client throws on purpose. Messages are safe to show to a user and never contain the token.</summary>
    public class AtlasException : Exception
    {
        public AtlasException(string message, Exception inner = null) : base(message, inner) { }
    }

    /// <summary>Atlas answered with an error status.</summary>
    public class AtlasApiException : AtlasException
    {
        public AtlasApiException(int status, string code, string message, string requestId = null)
            : base(message) { Status = status; Code = code; RequestId = requestId; }
        public int Status { get; }
        /// <summary>The stable <c>error.code</c> (branch on this and the status, never on the message).</summary>
        public string Code { get; }
        public string RequestId { get; }
    }

    public sealed class AtlasRateLimitedException : AtlasApiException
    {
        public AtlasRateLimitedException(string message, TimeSpan? retryAfter, string requestId = null)
            : base(429, "rate_limited", message, requestId) { RetryAfter = retryAfter; }
        public TimeSpan? RetryAfter { get; }
    }

    /// <summary>The network failed (or timed out) on every attempt.</summary>
    public sealed class AtlasNetworkException : AtlasException
    {
        public AtlasNetworkException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>The answer broke the wire rules: bad JSON, a malformed or out-of-range Atlas-Wire header, a refused redirect.</summary>
    public class AtlasProtocolException : AtlasException
    {
        public AtlasProtocolException(string message, Exception inner = null) : base(message, inner) { }
    }

    /// <summary>HTTP 409 <c>wire_unsupported</c> (C16 section 6). <see cref="Detail"/> is shown verbatim; both ranges are for the log.</summary>
    public sealed class AtlasWireUnsupportedException : AtlasApiException
    {
        public AtlasWireUnsupportedException(string reason, string action, string detail, WireRange? server, WireRange? client, string requestId)
            : base(409, "wire_unsupported", detail, requestId)
        { Reason = reason; Action = action; Detail = detail; Server = server; Client = client; }
        /// <summary>peer_too_old, peer_too_new, peer_v0, peer_downgraded (anything else is passed through).</summary>
        public string Reason { get; }
        /// <summary>upgrade_client, upgrade_server, admin_review.</summary>
        public string Action { get; }
        public string Detail { get; }
        public WireRange? Server { get; }
        public WireRange? Client { get; }
    }

    /// <summary>This Atlas cannot be used by this connector (client-api major differs, or a required capability is missing).</summary>
    public sealed class AtlasIncompatibleException : AtlasException
    {
        public AtlasIncompatibleException(string message, IReadOnlyList<string> missingCapabilities = null)
            : base(message) { MissingCapabilities = missingCapabilities ?? Array.Empty<string>(); }
        public IReadOnlyList<string> MissingCapabilities { get; }
    }

    /// <summary>The pairing code expired or the user denied it.</summary>
    public sealed class AtlasPairingException : AtlasException
    {
        public AtlasPairingException(string outcome, string message) : base(message) { Outcome = outcome; }
        /// <summary>"expired" or "denied".</summary>
        public string Outcome { get; }
    }
}
