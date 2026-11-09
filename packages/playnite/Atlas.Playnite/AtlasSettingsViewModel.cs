using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Atlas.Client;
using Playnite.SDK;

namespace Atlas.Playnite
{
    /// <summary>
    /// The data context of the settings view: Atlas address, pairing, connection test. Playnite calls BeginEdit when the view opens,
    /// VerifySettings then EndEdit on OK, CancelEdit on Cancel. Nothing is saved before EndEdit.
    /// Pairing runs on a background task and never blocks the UI thread; the token it returns is held in memory until EndEdit.
    /// </summary>
    public sealed class AtlasSettingsViewModel : INotifyPropertyChanged, ISettings
    {
        private readonly AtlasServices _services;
        private readonly Action<AtlasSettings> _save;
        private AtlasSettings _backup;
        private string _baseUrl, _status = string.Empty, _pairCode = string.Empty, _pairUrl = string.Empty;
        private bool _allowHttp, _busy;
        private int _ttlDays;
        private string _pendingToken;     // set by pairing or by the paste box; null = keep what is stored
        private bool _forget;
        private CancellationTokenSource _cts;

        public AtlasSettingsViewModel(AtlasServices services, Action<AtlasSettings> save)
        {
            _services = services; _save = save;
            _backup = services.Settings.Copy();
            Load(_backup);
            PairCommand = new RelayCommand(() => { var _ = PairAsync(); }, () => !Busy);
            TestCommand = new RelayCommand(() => { var _ = TestAsync(); }, () => !Busy);
            ForgetTokenCommand = new RelayCommand(ForgetToken);
            CancelPairingCommand = new RelayCommand(() => { if (_cts != null) _cts.Cancel(); });
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public ICommand PairCommand { get; private set; }
        public ICommand TestCommand { get; private set; }
        public ICommand ForgetTokenCommand { get; private set; }
        public ICommand CancelPairingCommand { get; private set; }

        public string BaseUrl { get { return _baseUrl; } set { Set(ref _baseUrl, value); } }
        public bool AllowPlainHttp { get { return _allowHttp; } set { Set(ref _allowHttp, value); } }
        public int CacheTtlDays { get { return _ttlDays; } set { Set(ref _ttlDays, value); } }
        public string Status { get { return _status; } private set { Set(ref _status, value); } }
        public string PairCode { get { return _pairCode; } private set { Set(ref _pairCode, value); } }
        public string PairUrl { get { return _pairUrl; } private set { Set(ref _pairUrl, value); } }
        public bool Busy { get { return _busy; } private set { Set(ref _busy, value); } }

        public string TokenState
        {
            get
            {
                if (_forget) return "The token will be removed when you press OK.";
                if (_pendingToken != null) return "A new token is ready and will be saved when you press OK.";
                return string.IsNullOrEmpty(_backup.ProtectedToken) ? "No token stored (fine only if this Atlas allows public read)." : "A token is stored (encrypted for your Windows account).";
            }
        }

        /// <summary>Called by the view when the user pastes a token instead of pairing. Never logged, never shown again.</summary>
        public void SetPastedToken(string token)
        {
            _pendingToken = string.IsNullOrWhiteSpace(token) ? null : token.Trim();
            _forget = false;
            Raise(nameof(TokenState));
        }

        // ---- ISettings ----------------------------------------------------------------------------------------

        public void BeginEdit() { _backup = _services.Settings.Copy(); Load(_backup); }

        public void CancelEdit()
        {
            if (_cts != null) _cts.Cancel();
            Load(_backup ?? _services.Settings);
        }

        public bool VerifySettings(out List<string> errors)
        {
            errors = new List<string>();
            if (!string.IsNullOrWhiteSpace(BaseUrl))      // an empty address is allowed: the plugin then does nothing
            {
                string err;
                if (SettingsRules.NormalizeBaseUrl(BaseUrl, AllowPlainHttp, out err) == null) errors.Add(err);
            }
            if (CacheTtlDays < 0 || CacheTtlDays > 365) errors.Add("Keep cached answers for 0 to 365 days.");
            return errors.Count == 0;
        }

        public void EndEdit()
        {
            if (_cts != null) _cts.Cancel();
            string err;
            var uri = SettingsRules.NormalizeBaseUrl(BaseUrl, AllowPlainHttp, out err);
            var s = _backup.Copy();
            s.BaseUrl = uri == null ? string.Empty : uri.AbsoluteUri;
            s.AllowPlainHttp = AllowPlainHttp;
            s.CacheTtlDays = CacheTtlDays;
            if (_forget) s.ProtectedToken = string.Empty;
            else if (_pendingToken != null) s.ProtectedToken = _services.Protector.Protect(_pendingToken);
            _pendingToken = null; _forget = false;
            _save(s);
            _services.Reload(s);
            _backup = s.Copy();
            Raise(nameof(TokenState));
        }

        // ---- actions ------------------------------------------------------------------------------------------

        private void ForgetToken()
        {
            _pendingToken = null; _forget = true;
            Raise(nameof(TokenState));
        }

        private AtlasClient TempClient(Func<string> token)
        {
            string err;
            var uri = SettingsRules.NormalizeBaseUrl(BaseUrl, AllowPlainHttp, out err);
            if (uri == null) { Status = err; return null; }
            return SettingsRules.CreateClient(new AtlasSettings { BaseUrl = uri.AbsoluteUri, AllowPlainHttp = AllowPlainHttp }, token, _services.CreateHandler());
        }

        /// <summary>Show a code, wait for the user to approve it on Atlas, keep the token it returns (CONTRACT-C15 10.1).</summary>
        public async Task PairAsync()
        {
            var client = TempClient(null);
            if (client == null) return;
            Busy = true; PairCode = string.Empty; PairUrl = string.Empty; Status = "Contacting Atlas...";
            _cts = new CancellationTokenSource();
            try
            {
                using (client)
                {
                    var token = await client.PairAsync("Playnite on " + Environment.MachineName, session =>
                    {
                        PairCode = session.UserCode;
                        PairUrl = session.VerifyUrl;
                        Status = "Open the address below on a device where you are signed in to Atlas and approve the code. Only approve a code shown here.";
                    }, _cts.Token).ConfigureAwait(true);
                    _pendingToken = token; _forget = false;
                    Status = "Paired. Press OK to save.";
                    PairCode = string.Empty;
                    Raise(nameof(TokenState));
                }
            }
            catch (OperationCanceledException) { Status = "Pairing cancelled."; PairCode = string.Empty; }
            catch (AtlasPairingException e) { Status = "Pairing did not finish: " + e.Message; PairCode = string.Empty; }
            catch (AtlasException e) { Status = "Pairing failed: " + e.Message; PairCode = string.Empty; }
            finally { Busy = false; _cts = null; }
        }

        public async Task TestAsync()
        {
            var tokenNow = _pendingToken ?? (_forget ? null : _services.Protector.Unprotect(_backup.ProtectedToken));
            var client = TempClient(() => tokenNow);
            if (client == null) return;
            Busy = true; Status = "Testing...";
            try
            {
                using (client)
                {
                    var site = await client.EnsureCompatibleAsync(CancellationToken.None).ConfigureAwait(true);
                    var api = site.ClientApi == null ? "no client_api block yet (older Atlas, works with reduced features)" : "client API " + site.ClientApi.Version;
                    Status = "Connected to " + (site.Name ?? "Atlas") + " (" + api + ").";
                }
            }
            catch (AtlasException e) { Status = "Not connected: " + e.Message; }
            finally { Busy = false; }
        }

        // ---- plumbing -----------------------------------------------------------------------------------------

        private void Load(AtlasSettings s)
        {
            BaseUrl = s.BaseUrl; AllowPlainHttp = s.AllowPlainHttp; CacheTtlDays = s.CacheTtlDays;
            _pendingToken = null; _forget = false; Status = string.Empty; PairCode = string.Empty; PairUrl = string.Empty;
            Raise(nameof(TokenState));
        }

        private void Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value; Raise(name);
        }

        private void Raise(string name)
        {
            var h = PropertyChanged;
            if (h != null) h(this, new PropertyChangedEventArgs(name));
        }
    }
}
