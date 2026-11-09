using System;
using System.Globalization;
using Atlas.Client;

namespace Atlas.Playnite
{
    /// <summary>
    /// What is saved to the plugin's settings file. The token is never here in clear text: <see cref="ProtectedToken"/> is
    /// DPAPI output (CONTRACT-C15 8.4). Playnite's own "metadata sources" setting decides when Atlas is asked relative to other
    /// providers, so there is no separate order setting.
    /// </summary>
    public class AtlasSettings
    {
        public string BaseUrl { get; set; } = string.Empty;
        public string ProtectedToken { get; set; } = string.Empty;
        /// <summary>Allow plain http to a non-loopback address (the token then travels in clear text on the LAN).</summary>
        public bool AllowPlainHttp { get; set; }
        public int CacheTtlDays { get; set; } = 7;

        public TimeSpan CacheTtl
        {
            get { return TimeSpan.FromDays(Math.Max(0, Math.Min(365, CacheTtlDays))); }
        }

        public AtlasSettings Copy()
        {
            return new AtlasSettings { BaseUrl = BaseUrl, ProtectedToken = ProtectedToken, AllowPlainHttp = AllowPlainHttp, CacheTtlDays = CacheTtlDays };
        }
    }

    public static class SettingsRules
    {
        /// <summary>A usable base URL (https, or http for loopback / when allowed), without query or fragment; null and an error otherwise.</summary>
        public static Uri NormalizeBaseUrl(string text, bool allowPlainHttp, out string error)
        {
            error = null;
            Uri u;
            if (string.IsNullOrWhiteSpace(text) || !Uri.TryCreate(text.Trim(), UriKind.Absolute, out u))
            {
                error = "Enter the address of your Atlas, for example https://atlas.example.org/.";
                return null;
            }
            if (u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeHttp) { error = "The Atlas address must start with https:// (or http://)."; return null; }
            if (!string.IsNullOrEmpty(u.UserInfo)) { error = "Do not put a user name or password in the address."; return null; }
            if (!string.IsNullOrEmpty(u.Query) || !string.IsNullOrEmpty(u.Fragment)) { error = "The address must not contain ? or #."; return null; }
            if (u.Scheme == Uri.UriSchemeHttp && !u.IsLoopback && !allowPlainHttp)
            {
                error = "Plain http sends the token unprotected. Use https, or tick the box that allows http on a trusted network.";
                return null;
            }
            var s = u.AbsoluteUri;
            return new Uri(s.EndsWith("/", StringComparison.Ordinal) ? s : s + "/");
        }

        public static string Version
        {
            get
            {
                var v = typeof(SettingsRules).Assembly.GetName().Version;
                return v.Major.ToString(CultureInfo.InvariantCulture) + "." + v.Minor.ToString(CultureInfo.InvariantCulture) + "." + v.Build.ToString(CultureInfo.InvariantCulture);
            }
        }

        /// <summary>Build a client for the saved settings. Null when the settings are not usable yet.</summary>
        public static AtlasClient CreateClient(AtlasSettings s, Func<string> token, System.Net.Http.HttpMessageHandler handler = null)
        {
            string err;
            var uri = NormalizeBaseUrl(s.BaseUrl, s.AllowPlainHttp, out err);
            if (uri == null) return null;
            var o = new AtlasClientOptions
            {
                BaseUri = uri,
                TokenProvider = token,
                HostName = "playnite",
                HostVersion = Version,
                AllowPlainHttp = s.AllowPlainHttp,
            };
            return new AtlasClient(o, new NewtonsoftJsonAdapter(), handler);
        }
    }
}
