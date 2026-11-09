using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Atlas.Client;
using Jellyfin.Plugin.Atlas.Configuration;

namespace Jellyfin.Plugin.Atlas;

/// <summary>
/// The shared pieces behind the providers and the settings endpoints: current configuration, token store, the Atlas client and cache.
/// One per plugin instance. The tests build their own with a fake HTTP handler.
/// </summary>
public sealed class AtlasRuntime
{
    private static readonly object StaticGate = new object();
    private static AtlasRuntime _current;

    private readonly object _gate = new object();
    private readonly Func<PluginConfiguration> _config;
    private readonly Func<HttpMessageHandler> _handler;
    private readonly string _cacheDir;
    private AtlasLookup _lookup;
    private string _lookupKey;
    private PairingSession _pairing;
    private DateTime _pairingStartedUtc;

    public AtlasRuntime(Func<PluginConfiguration> config, ITokenStore tokens, string cacheDirectory, Func<HttpMessageHandler> handlerFactory = null, Action<string> log = null)
    {
        _config = config;
        Tokens = tokens;
        _cacheDir = cacheDirectory;
        _handler = handlerFactory;
        Log = log ?? (s => { });
    }

    /// <summary>The runtime of the loaded plugin, or null when the plugin has not been constructed (providers then do nothing).</summary>
    public static AtlasRuntime Current
    {
        get
        {
            lock (StaticGate)
            {
                if (_current == null && Plugin.Instance != null)
                {
                    var p = Plugin.Instance;
                    _current = new AtlasRuntime(
                        () => p.Configuration,
                        new FileTokenStore(p.TokenFilePath),
                        Path.Combine(Path.GetDirectoryName(p.TokenFilePath) ?? ".", "Jellyfin.Plugin.Atlas.cache"));
                }

                return _current;
            }
        }
    }

    public ITokenStore Tokens { get; }

    public Action<string> Log { get; }

    public PluginConfiguration Config => _config();

    public HttpMessageHandler NewHandler() => _handler?.Invoke();

    /// <summary>The lookup for the current settings, or null when no usable Atlas address is configured.</summary>
    public AtlasLookup GetLookup()
    {
        lock (_gate)
        {
            var c = _config();
            var key = c.BaseUrl + "|" + c.AllowPlainHttp + "|" + c.CacheTtlDays;
            if (_lookup != null && key == _lookupKey)
            {
                return _lookup;
            }

            var client = SettingsRules.CreateClient(c, () => Tokens.Read(), NewHandler());
            if (client == null)
            {
                _lookup = null;
                return null;
            }

            _lookup = new AtlasLookup(client, new RecordCache(_cacheDir, new SystemTextJsonAdapter()), SettingsRules.Ttl(c), Log);
            _lookupKey = key;
            return _lookup;
        }
    }

    public Uri BaseUri => SettingsRules.NormalizeBaseUrl(_config().BaseUrl, _config().AllowPlainHttp, out _);

    // ---- settings page support: pairing and connection test (no secret ever leaves the server) ----------------

    public async Task<PairingSession> StartPairingAsync(string label, CancellationToken ct)
    {
        using (var client = SettingsRules.CreateClient(_config(), null, NewHandler()) ?? throw new InvalidOperationException("Set a valid Atlas address first."))
        {
            var session = await client.StartPairingAsync(label, ct).ConfigureAwait(false);
            lock (_gate)
            {
                _pairing = session;
                _pairingStartedUtc = DateTime.UtcNow;
            }

            return session;
        }
    }

    /// <summary>Poll the pending pairing once. On approval the token goes straight into the token store and is not returned.</summary>
    public async Task<PairingStatus?> PollPairingAsync(CancellationToken ct)
    {
        PairingSession session;
        lock (_gate)
        {
            session = _pairing;
        }

        if (session == null)
        {
            return null;
        }

        using (var client = SettingsRules.CreateClient(_config(), null, NewHandler()) ?? throw new InvalidOperationException("Set a valid Atlas address first."))
        {
            var poll = await client.PollPairingAsync(session.PollToken, ct).ConfigureAwait(false);
            if (poll.Status == PairingStatus.Approved && !string.IsNullOrEmpty(poll.Token))
            {
                Tokens.Write(poll.Token);
            }

            if (poll.Status == PairingStatus.Approved || poll.Status == PairingStatus.Denied || poll.Status == PairingStatus.Expired)
            {
                lock (_gate)
                {
                    _pairing = null;
                }
            }

            return poll.Status;
        }
    }

    public async Task<string> TestConnectionAsync(CancellationToken ct)
    {
        using (var client = SettingsRules.CreateClient(_config(), () => Tokens.Read(), NewHandler()))
        {
            if (client == null)
            {
                return "Set a valid Atlas address first.";
            }

            try
            {
                var site = await client.EnsureCompatibleAsync(ct).ConfigureAwait(false);
                var api = site.ClientApi == null ? "no client_api block yet (older Atlas, works with reduced features)" : "client API " + site.ClientApi.Version;
                return "Connected to " + (site.Name ?? "Atlas") + " (" + api + ").";
            }
            catch (AtlasException e)
            {
                return "Not connected: " + e.Message;
            }
        }
    }
}
