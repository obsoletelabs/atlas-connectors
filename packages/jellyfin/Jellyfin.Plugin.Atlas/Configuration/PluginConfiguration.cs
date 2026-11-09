using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Atlas.Configuration;

/// <summary>Saved by Jellyfin as XML. Contains no secret: the read token is in <see cref="TokenStore"/>.</summary>
public class PluginConfiguration : BasePluginConfiguration
{
    public PluginConfiguration()
    {
        BaseUrl = string.Empty;
        AllowPlainHttp = false;
        CacheTtlDays = 7;
        EnableBooks = true;
        EnableMovies = true;
        EnableSeries = true;
    }

    public string BaseUrl { get; set; }

    /// <summary>Allow plain http to a non-loopback address (the token then travels in clear text on the network).</summary>
    public bool AllowPlainHttp { get; set; }

    public int CacheTtlDays { get; set; }

    public bool EnableBooks { get; set; }

    public bool EnableMovies { get; set; }

    public bool EnableSeries { get; set; }
}
