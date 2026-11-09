using System;
using System.Globalization;
using System.Net.Http;
using Atlas.Client;
using Jellyfin.Plugin.Atlas.Configuration;

namespace Jellyfin.Plugin.Atlas;

public static class SettingsRules
{
    /// <summary>A usable base URL (https, or http for loopback / when allowed), without user info, query or fragment. Null and an error otherwise.</summary>
    public static Uri NormalizeBaseUrl(string text, bool allowPlainHttp, out string error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(text) || !Uri.TryCreate(text.Trim(), UriKind.Absolute, out var u))
        {
            error = "Enter the address of your Atlas, for example https://atlas.example.org/.";
            return null;
        }

        if (u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeHttp)
        {
            error = "The Atlas address must start with https:// (or http://).";
            return null;
        }

        if (!string.IsNullOrEmpty(u.UserInfo))
        {
            error = "Do not put a user name or password in the address.";
            return null;
        }

        if (!string.IsNullOrEmpty(u.Query) || !string.IsNullOrEmpty(u.Fragment))
        {
            error = "The address must not contain ? or #.";
            return null;
        }

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

    public static TimeSpan Ttl(PluginConfiguration c) => TimeSpan.FromDays(Math.Max(0, Math.Min(365, c.CacheTtlDays)));

    /// <summary>A client for these settings, or null when no usable address is set. The token is read per request through the callback.</summary>
    public static AtlasClient CreateClient(PluginConfiguration c, Func<string> token, HttpMessageHandler handler = null)
    {
        var uri = NormalizeBaseUrl(c.BaseUrl, c.AllowPlainHttp, out _);
        if (uri == null)
        {
            return null;
        }

        var o = new AtlasClientOptions
        {
            BaseUri = uri,
            TokenProvider = token,
            HostName = "jellyfin",
            HostVersion = Version,
            AllowPlainHttp = c.AllowPlainHttp,
        };
        return new AtlasClient(o, new SystemTextJsonAdapter(), handler);
    }
}
