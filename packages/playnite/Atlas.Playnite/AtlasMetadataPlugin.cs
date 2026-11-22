using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows.Controls;
using Playnite.SDK;
using Playnite.SDK.Plugins;

namespace Atlas.Playnite
{
    /// <summary>The Playnite metadata plugin "Atlas". Thin: the work is in <see cref="AtlasLookup"/> and <see cref="GameMapper"/>.</summary>
    public class AtlasMetadataPlugin : MetadataPlugin
    {
        /// <summary>Never changes (CONTRACT-C15 8.3). Matches the Id suffix in extension.yaml.</summary>
        public static readonly Guid PluginId = new Guid("07b50695-a9e6-4d21-b11a-6d94e0df4f3f");

        private static readonly ILogger Logger = SafeLogger();
        private readonly AtlasServices _services;

        public AtlasMetadataPlugin(IPlayniteAPI api) : base(api)
        {
            Properties = new MetadataPluginProperties { HasSettings = true };
            var settings = LoadPluginSettings<AtlasSettings>() ?? new AtlasSettings();
            _services = new AtlasServices(settings, new DpapiTokenProtector(), GetPluginUserDataPath(), null, m => Log(m));
        }

        public override Guid Id { get { return PluginId; } }

        public override string Name { get { return "Atlas"; } }

        public override List<MetadataField> SupportedFields
        {
            get
            {
                return new List<MetadataField>
                {
                    MetadataField.Name, MetadataField.ReleaseDate, MetadataField.Developers, MetadataField.Publishers,
                    MetadataField.Description, MetadataField.Series, MetadataField.Genres, MetadataField.Tags, MetadataField.Links,
                    MetadataField.CoverImage, MetadataField.BackgroundImage, MetadataField.Icon,
                };
            }
        }

        public override OnDemandMetadataProvider GetMetadataProvider(MetadataRequestOptions options)
        {
            return new AtlasMetadataProvider(_services, options, Choose);
        }

        public override ISettings GetSettings(bool firstRunSettings)
        {
            return new AtlasSettingsViewModel(_services, s => SavePluginSettings(s));
        }

        public override UserControl GetSettingsView(bool firstRunSettings)
        {
            return new AtlasSettingsView();
        }

        // Manual download only: let the user pick among Atlas candidates, or search again (title evidence is never auto-assigned).
        private Candidate Choose(IReadOnlyList<Candidate> candidates)
        {
            var lookup = _services.GetLookup();
            Func<Candidate, GenericItemOption> toOption = c => new GenericItemOption(
                string.IsNullOrEmpty(c.Detail) ? c.Title : c.Title + " (" + c.Detail + ")", c.Id);
            var initial = candidates.Select(toOption).ToList();
            Func<string, List<GenericItemOption>> search = text =>
                lookup == null ? new List<GenericItemOption>() : lookup.SearchAsync(text, CancellationToken.None).GetAwaiter().GetResult().Select(toOption).ToList();
            var picked = PlayniteApi.Dialogs.ChooseItemWithSearch(initial, search, string.Empty, "Atlas: which game is this?");
            return picked == null ? null : candidates.FirstOrDefault(c => c.Id == picked.Description) ?? new Candidate { Id = picked.Description, Title = picked.Name };
        }

        private static void Log(string message)
        {
            try { if (Logger != null) Logger.Info(message); } catch (Exception) { /* logging must never break a metadata request */ }
        }

        private static ILogger SafeLogger()
        {
            try { return LogManager.GetLogger(); } catch (Exception) { return null; }
        }
    }
}
