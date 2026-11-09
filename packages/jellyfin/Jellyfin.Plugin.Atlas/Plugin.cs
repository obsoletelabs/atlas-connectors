using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.Atlas.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Atlas;

/// <summary>The Atlas plugin. The GUID never changes (a new one would install as a second plugin).</summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public static readonly Guid PluginId = Guid.Parse("0ca48640-0596-432e-9978-e24ebb438d29");

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        TokenFilePath = System.IO.Path.Combine(applicationPaths.PluginConfigurationsPath, "Jellyfin.Plugin.Atlas.token");
    }

    public static Plugin Instance { get; private set; }

    /// <summary>The token lives next to, not inside, the plugin configuration XML, so the Plugins/{id}/Configuration API never returns it.</summary>
    public string TokenFilePath { get; }

    public override string Name => "Atlas";

    public override Guid Id => PluginId;

    public override string Description => "Metadata and images from your own Atlas node for books, movies and series.";

    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = "AtlasConfigPage",
                DisplayName = "Atlas",
                EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.configPage.html", GetType().Namespace),
            },
        };
    }
}
