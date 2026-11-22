using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Atlas.Client;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;

namespace Atlas.Playnite
{
    /// <summary>
    /// One metadata request for one game. Playnite calls the getters from worker threads, one after another, so the first call does
    /// the lookup (on the thread pool, so no synchronization context can deadlock it) and the rest read the result. A failed
    /// lookup provides no fields; it never throws into Playnite and never guesses (a title match is never applied automatically).
    /// </summary>
    public sealed class AtlasMetadataProvider : OnDemandMetadataProvider
    {
        private readonly AtlasServices _services;
        private readonly MetadataRequestOptions _options;
        private readonly Func<IReadOnlyList<Candidate>, Candidate> _chooser;
        private readonly object _gate = new object();
        private bool _loaded;
        private GameMetadata _meta;
        private AtlasRecord _record;
        private ArtworkList _art;

        /// <param name="chooser">Shown candidates for the user in a manual (non-background) request. Null: never ask.</param>
        public AtlasMetadataProvider(AtlasServices services, MetadataRequestOptions options, Func<IReadOnlyList<Candidate>, Candidate> chooser)
        {
            _services = services; _options = options; _chooser = chooser;
        }

        /// <summary>Set after loading: what happened, for the log and for tests.</summary>
        public LookupResult Result { get; private set; }

        public override List<MetadataField> AvailableFields
        {
            get
            {
                Load(CancellationToken.None);
                var f = new List<MetadataField>();
                if (_meta == null) return f;
                if (!string.IsNullOrEmpty(_meta.Name)) f.Add(MetadataField.Name);
                if (_meta.ReleaseDate.HasValue) f.Add(MetadataField.ReleaseDate);
                if (_meta.Developers != null) f.Add(MetadataField.Developers);
                if (_meta.Publishers != null) f.Add(MetadataField.Publishers);
                if (_meta.Description != null) f.Add(MetadataField.Description);
                if (_meta.Series != null) f.Add(MetadataField.Series);
                if (_meta.Genres != null) f.Add(MetadataField.Genres);
                if (_meta.Tags != null) f.Add(MetadataField.Tags);
                if (_meta.Links != null) f.Add(MetadataField.Links);
                if (AtlasLookup.Pick(_art, "cover", "poster") != null) f.Add(MetadataField.CoverImage);
                if (AtlasLookup.Pick(_art, "background") != null) f.Add(MetadataField.BackgroundImage);
                if (AtlasLookup.Pick(_art, "icon") != null) f.Add(MetadataField.Icon);
                return f;
            }
        }

        public override string GetName(GetMetadataFieldArgs args) { Load(args); return _meta == null ? null : _meta.Name; }
        public override ReleaseDate? GetReleaseDate(GetMetadataFieldArgs args) { Load(args); return _meta == null ? null : _meta.ReleaseDate; }
        public override string GetDescription(GetMetadataFieldArgs args) { Load(args); return _meta == null ? null : _meta.Description; }
        public override IEnumerable<MetadataProperty> GetDevelopers(GetMetadataFieldArgs args) { Load(args); return _meta == null ? null : _meta.Developers; }
        public override IEnumerable<MetadataProperty> GetPublishers(GetMetadataFieldArgs args) { Load(args); return _meta == null ? null : _meta.Publishers; }
        public override IEnumerable<MetadataProperty> GetSeries(GetMetadataFieldArgs args) { Load(args); return _meta == null ? null : _meta.Series; }
        public override IEnumerable<MetadataProperty> GetGenres(GetMetadataFieldArgs args) { Load(args); return _meta == null ? null : _meta.Genres; }
        public override IEnumerable<MetadataProperty> GetTags(GetMetadataFieldArgs args) { Load(args); return _meta == null ? null : _meta.Tags; }
        public override IEnumerable<Link> GetLinks(GetMetadataFieldArgs args) { Load(args); return _meta == null ? null : _meta.Links; }

        public override MetadataFile GetCoverImage(GetMetadataFieldArgs args) { return Image(args, "cover", "cover", "poster"); }
        public override MetadataFile GetBackgroundImage(GetMetadataFieldArgs args) { return Image(args, "background", "background"); }
        public override MetadataFile GetIcon(GetMetadataFieldArgs args) { return Image(args, "icon", "icon"); }

        private MetadataFile Image(GetMetadataFieldArgs args, string name, params string[] kinds)
        {
            Load(args);
            var art = AtlasLookup.Pick(_art, kinds);
            var lookup = _services.GetLookup();
            if (art == null || lookup == null) return null;
            var ct = Token(args);
            ImageData data;
            try { data = Task.Run(() => lookup.ImageAsync(art, ct), ct).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { return null; }   // the user cancelled the download: no image, no exception
            return data == null || data.Bytes == null || data.Bytes.Length == 0
                ? null
                : new MetadataFile("atlas-" + name + Extension(data.ContentType), data.Bytes);
        }

        public static string Extension(string contentType)
        {
            var t = (contentType ?? string.Empty).Split(';')[0].Trim().ToLowerInvariant();
            switch (t)
            {
                case "image/png": return ".png";
                case "image/jpeg": return ".jpg";
                case "image/webp": return ".webp";
                case "image/gif": return ".gif";
                case "image/x-icon":
                case "image/vnd.microsoft.icon": return ".ico";
                default: return ".img";
            }
        }

        private static CancellationToken Token(GetMetadataFieldArgs args) { return args == null ? CancellationToken.None : args.CancelToken; }

        private void Load(GetMetadataFieldArgs args) { Load(Token(args)); }

        private void Load(CancellationToken ct)
        {
            lock (_gate)
            {
                if (_loaded) return;
                _loaded = true;
                try { LoadCore(ct); }
                catch (OperationCanceledException)
                {
                    // The user cancelled the metadata download. Playnite gets no fields, never an exception (see the class summary).
                    _meta = null; _art = null;
                    Result = LookupResult.Unavailable("Cancelled.");
                }
            }
        }

        private void LoadCore(CancellationToken ct)
        {
            var lookup = _services.GetLookup();
            if (lookup == null) { Result = LookupResult.Unavailable("Atlas is not set up: open the Atlas extension settings."); return; }
            var query = BuildQuery(_options == null ? null : _options.GameData, _services.Settings.BaseUrl);
            if (string.IsNullOrWhiteSpace(query.Name) && string.IsNullOrEmpty(query.KnownAtlasId)) { Result = new LookupResult { Outcome = LookupOutcome.NotFound }; return; }
            var result = Task.Run(() => lookup.FindAsync(query, ct), ct).GetAwaiter().GetResult();
            if (result.Outcome == LookupOutcome.Candidates && _chooser != null && _options != null && !_options.IsBackgroundDownload)
            {
                var picked = _chooser(result.Candidates);
                if (picked != null) result = Task.Run(() => lookup.ChooseAsync(query, picked, ct), ct).GetAwaiter().GetResult();
            }
            Result = result;
            if (result.Outcome != LookupOutcome.Found || result.Record == null)
            {
                _services.Log("Atlas gave no metadata for '" + query.Name + "': " + result.Outcome + (result.Message != null ? " (" + result.Message + ")" : ""));
                return;
            }
            _record = result.Record;
            _meta = GameMapper.Map(_record, _services.Settings.BaseUrl);
            if (!result.FromCache && !string.IsNullOrEmpty(_record.Id))
                _art = Task.Run(() => lookup.ArtworkAsync(_record.Id, ct), ct).GetAwaiter().GetResult();
        }

        internal static GameQuery BuildQuery(Game game, string baseUrl)
        {
            if (game == null) return new GameQuery();
            string known = null;
            if (game.Links != null) foreach (var l in game.Links) { known = AtlasLinks.IdFromLink(l, baseUrl); if (known != null) break; }
            return new GameQuery
            {
                Name = game.Name,
                Year = game.ReleaseDate.HasValue && game.ReleaseDate.Value.Year > 0 ? (int?)game.ReleaseDate.Value.Year : null,
                PlayniteGameId = game.Id,
                LibraryPluginId = game.PluginId,
                LibraryGameId = game.GameId,
                KnownAtlasId = known,
            };
        }
    }
}
