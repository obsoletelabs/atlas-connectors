using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Atlas.Client
{
    /// <summary>Settings for <see cref="AtlasClient"/>. The defaults follow CLIENT-CONTRACT and C16 section 10.1.</summary>
    public sealed class AtlasClientOptions
    {
        /// <summary>The Atlas node, e.g. <c>https://atlas.example.org/</c>. A path prefix is allowed (reverse proxy).</summary>
        public Uri BaseUri { get; set; }

        /// <summary>
        /// Called for every request that needs the read token. A function, so the host can keep the token in DPAPI (Playnite)
        /// or its plugin configuration (Jellyfin) and this client never stores it. Null or empty: anonymous (public_read).
        /// </summary>
        public Func<string> TokenProvider { get; set; }

        /// <summary><c>playnite</c>, <c>jellyfin</c>, <c>emby</c>, <c>plex</c> or <c>other</c>. Becomes <c>User-Agent: atlas-connector-&lt;host&gt;/&lt;version&gt;</c>.</summary>
        public string HostName { get; set; } = "other";

        /// <summary>The connector package's own version (not this client's).</summary>
        public string HostVersion { get; set; } = "0.0.0";

        /// <summary>The wire versions this connector speaks (C16). Sent as <c>Atlas-Wire: min=..., max=...</c> on every request.</summary>
        public WireRange Wire { get; set; } = new WireRange(1, 1);

        /// <summary>The client-api major this connector was built for (CLIENT-CONTRACT section 1).</summary>
        public int ClientApiMajor { get; set; } = 1;

        /// <summary>Capabilities the connector cannot work without (for example "books"). Checked against <c>/site</c>.</summary>
        public IList<string> RequiredCapabilities { get; } = new List<string>();

        /// <summary>
        /// When true, an Atlas whose <c>/site</c> has no <c>client_api</c> block is refused. Default false because Atlas ships
        /// the block only with PR A24; switch it on once the connector's minimum Atlas has it.
        /// </summary>
        public bool RequireClientApi { get; set; }

        /// <summary>Plain http is accepted for loopback addresses only, unless the user opts in here (the token travels in clear text).</summary>
        public bool AllowPlainHttp { get; set; }

        /// <summary>Retries after the first attempt, for GET and read-only POSTs only.</summary>
        public int MaxRetries { get; set; } = 3;
        public TimeSpan BaseDelay { get; set; } = TimeSpan.FromMilliseconds(500);
        public TimeSpan MaxDelay { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// The longest <c>Retry-After</c> the client will wait inside a call (a UI thread must not stall for minutes). A longer one
        /// is thrown as <see cref="AtlasRateLimitedException"/> carrying the value so the host can schedule the retry itself.
        /// </summary>
        public TimeSpan MaxRetryAfter { get; set; } = TimeSpan.FromSeconds(60);

        public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>Cap for one downloaded image (bytes).</summary>
        public long MaxImageBytes { get; set; } = 20L * 1024 * 1024;

        /// <summary>Test seam: how to wait. Default is <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.</summary>
        public Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = (t, ct) => Task.Delay(t, ct);

        /// <summary>Test seam: a number in [0,1) for backoff jitter.</summary>
        public Func<double> Jitter { get; set; } = new Random().NextDouble;
    }
}
