using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Atlas.Client;

namespace Atlas.Playnite
{
    /// <summary>One cached record: the raw Atlas JSON, its ETag and when it was fetched.</summary>
    public sealed class CachedRecord
    {
        public AtlasRecord Record { get; set; }
        public string ETag { get; set; }
        public DateTime FetchedUtc { get; set; }
    }

    /// <summary>
    /// File cache in the plugin data directory (never the install directory, which Playnite purges on update). Serves the
    /// offline fallback (CONTRACT-C15 8.4). Uses the Atlas.Client JSON adapter, so no second serializer is involved.
    /// A damaged file is treated as a miss.
    /// </summary>
    public sealed class RecordCache
    {
        private readonly string _dir;
        private readonly IJsonAdapter _json;
        private readonly Func<DateTime> _now;

        public RecordCache(string directory, IJsonAdapter json, Func<DateTime> utcNow = null)
        {
            _dir = directory; _json = json; _now = utcNow ?? (() => DateTime.UtcNow);
        }

        public CachedRecord GetRecord(string atlasId)
        {
            var node = Read("rec-" + Hash(atlasId));
            if (node == null) return null;
            var rec = AtlasReader.ReadRecord(node["record"]);
            DateTime when;
            if (rec == null || !DateTime.TryParse(node["fetched"].AsString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out when)) return null;
            return new CachedRecord { Record = rec, ETag = node["etag"].AsString(), FetchedUtc = when };
        }

        public void PutRecord(AtlasRecord record, string etag)
        {
            if (record == null || string.IsNullOrEmpty(record.Id) || record.Raw == null) return;
            Write("rec-" + Hash(record.Id), JsonNode.Object(new[]
            {
                new KeyValuePair<string, JsonNode>("fetched", JsonNode.Str(_now().ToString("o", CultureInfo.InvariantCulture))),
                new KeyValuePair<string, JsonNode>("etag", JsonNode.Str(etag)),
                new KeyValuePair<string, JsonNode>("record", record.Raw),
            }));
        }

        /// <summary>The Atlas id a host id resolved to earlier, or null.</summary>
        public string GetIdentity(string key)
        {
            var node = Read("idn-" + Hash(key));
            return node == null ? null : node["atlas_id"].AsString();
        }

        public void PutIdentity(string key, string atlasId)
        {
            if (string.IsNullOrEmpty(atlasId)) return;
            Write("idn-" + Hash(key), JsonNode.Object(new[] { new KeyValuePair<string, JsonNode>("atlas_id", JsonNode.Str(atlasId)) }));
        }

        public bool IsFresh(CachedRecord c, TimeSpan ttl) { return c != null && _now() - c.FetchedUtc <= ttl; }

        private JsonNode Read(string name)
        {
            try
            {
                var path = Path.Combine(_dir, name + ".json");
                if (!File.Exists(path)) return null;
                var n = _json.Parse(File.ReadAllText(path, Encoding.UTF8));
                return n.Kind == JsonKind.Object ? n : null;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is AtlasException) { return null; }
        }

        private void Write(string name, JsonNode node)
        {
            try
            {
                Directory.CreateDirectory(_dir);
                var tmp = Path.Combine(_dir, name + ".tmp");
                File.WriteAllText(tmp, _json.Write(node), new UTF8Encoding(false));
                var path = Path.Combine(_dir, name + ".json");
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { /* a cache that cannot write is a cache miss */ }
        }

        private static string Hash(string s)
        {
            using (var sha = SHA256.Create())
            {
                var b = sha.ComputeHash(Encoding.UTF8.GetBytes(s ?? string.Empty));
                var sb = new StringBuilder();
                for (var i = 0; i < 12; i++) sb.Append(b[i].ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }
    }
}
