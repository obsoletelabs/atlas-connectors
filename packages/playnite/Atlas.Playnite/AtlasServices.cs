using System;
using System.IO;
using System.Net.Http;
using Atlas.Client;

namespace Atlas.Playnite
{
    /// <summary>The shared pieces of the plugin: settings, token protection, the Atlas client and the cache. One per plugin instance.</summary>
    public sealed class AtlasServices
    {
        private readonly object _gate = new object();
        private readonly ITokenProtector _protector;
        private readonly string _dataDir;
        private readonly Func<HttpMessageHandler> _handler;
        private readonly Action<string> _log;
        private AtlasLookup _lookup;
        private AtlasSettings _settings;

        public AtlasServices(AtlasSettings settings, ITokenProtector protector, string dataDirectory,
            Func<HttpMessageHandler> handlerFactory = null, Action<string> log = null)
        {
            _settings = settings ?? new AtlasSettings();
            _protector = protector;
            _dataDir = dataDirectory;
            _handler = handlerFactory;
            _log = log ?? (s => { });
        }

        public AtlasSettings Settings { get { lock (_gate) return _settings; } }

        public string Token { get { return _protector.Unprotect(Settings.ProtectedToken); } }

        public ITokenProtector Protector { get { return _protector; } }

        public Action<string> Log { get { return _log; } }

        /// <summary>Test seam: a message handler for temporary clients (pairing, connection test). Null means the real network.</summary>
        public HttpMessageHandler CreateHandler() { return _handler == null ? null : _handler(); }

        /// <summary>Replace the settings (after the user saved the settings view) and drop the old client.</summary>
        public void Reload(AtlasSettings settings)
        {
            lock (_gate) { _settings = settings ?? new AtlasSettings(); _lookup = null; }
        }

        /// <summary>The lookup for the current settings, or null when no usable Atlas address is configured.</summary>
        public AtlasLookup GetLookup()
        {
            lock (_gate)
            {
                if (_lookup != null) return _lookup;
                var s = _settings;
                var client = SettingsRules.CreateClient(s, () => _protector.Unprotect(s.ProtectedToken), _handler == null ? null : _handler());
                if (client == null) return null;
                var cache = new RecordCache(Path.Combine(_dataDir, "cache"), new NewtonsoftJsonAdapter());
                _lookup = new AtlasLookup(client, cache, s.CacheTtl, _log);
                return _lookup;
            }
        }
    }
}
