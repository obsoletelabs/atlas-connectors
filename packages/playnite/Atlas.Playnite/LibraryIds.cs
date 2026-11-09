using System;

namespace Atlas.Playnite
{
    /// <summary>
    /// Which Atlas provider-id namespace a Playnite library plugin's <c>GameId</c> belongs to. Identity first (CONTRACT-C15 8.4):
    /// only pairs that were checked are listed. Everything else goes to Atlas as title evidence, which never auto-assigns.
    /// </summary>
    public static class LibraryIds
    {
        /// <summary>Playnite's built-in Steam library plugin id (PlayniteExtensions SteamLibrary.cs, read 2026-11-09).</summary>
        public static readonly Guid Steam = new Guid("CB91DFC9-B977-43BF-8E70-55F46E410FAB");

        /// <summary>Atlas namespace for Steam ids: <c>steamsync</c> (resolve-contract-v1.json, 6 cases).</summary>
        public const string SteamNamespace = "steamsync";

        /// <summary>Atlas namespace used when the library is not mapped: the Playnite game's own id.</summary>
        public const string PlayniteNamespace = "playnite";

        /// <summary>
        /// The (namespace, id) Atlas should look up for a game from <paramref name="libraryPluginId"/>, or null when none is known.
        /// Steam: Playnite's Steam <c>GameId</c> is a 64-bit Steam GameID that is a plain app id for ordinary games; mods have
        /// larger values and are not sent as ids.
        /// </summary>
        public static Tuple<string, string> Map(Guid libraryPluginId, string gameId)
        {
            if (libraryPluginId == Steam && IsSteamAppId(gameId)) return Tuple.Create(SteamNamespace, gameId);
            return null;
        }

        public static bool IsSteamAppId(string gameId)
        {
            if (string.IsNullOrEmpty(gameId) || gameId.Length > 10) return false;
            foreach (var c in gameId) if (c < '0' || c > '9') return false;
            uint v;
            return uint.TryParse(gameId, out v) && v > 0;
        }
    }
}
