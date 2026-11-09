using System;
using System.Collections.Generic;
using System.Globalization;

namespace Atlas.Client
{
    public struct WireRange : IEquatable<WireRange>
    {
        public WireRange(int min, int max) { Min = min; Max = max; }
        public int Min { get; }
        public int Max { get; }
        public bool Contains(int v) => v >= Min && v <= Max;
        public bool Equals(WireRange o) => o.Min == Min && o.Max == Max;
        public override bool Equals(object obj) => obj is WireRange && Equals((WireRange)obj);
        public override int GetHashCode() => Min * 10007 + Max;
        public override string ToString() => Min == Max ? Min.ToString(CultureInfo.InvariantCulture)
            : Min.ToString(CultureInfo.InvariantCulture) + "-" + Max.ToString(CultureInfo.InvariantCulture);
    }

    public enum WireMode
    {
        /// <summary>The response carried <c>Atlas-Wire: use=n</c> inside our range.</summary>
        Negotiated,
        /// <summary>No <c>Atlas-Wire</c> in the response: an Atlas from before wire versions (v0). Work, but show a notice (C16 section 10.1).</summary>
        LegacyV0
    }

    /// <summary>What the last answer told us about the version in force.</summary>
    public sealed class WireInfo
    {
        public WireInfo(WireMode mode, int? use, WireRange? server)
        { Mode = mode; Use = use; Server = server; }
        public WireMode Mode { get; }
        public int? Use { get; }
        public WireRange? Server { get; }
        public bool IsDegraded => Mode == WireMode.LegacyV0;
    }

    /// <summary>
    /// The <c>Atlas-Wire</c> header (C16 section 3.2): an RFC 8941 dictionary restricted to integer members,
    /// <c>^\s*[a-z_]{1,16}=[0-9]{1,4}(\s*,\s*[a-z_]{1,16}=[0-9]{1,4}){0,7}\s*$</c>, with 1 &lt;= min &lt;= max &lt;= 9999.
    /// Unknown keys are ignored; a duplicate key is an error.
    /// </summary>
    public static class WireHeader
    {
        public const string Name = "Atlas-Wire";

        public static string Format(WireRange range) =>
            "min=" + range.Min.ToString(CultureInfo.InvariantCulture) + ", max=" + range.Max.ToString(CultureInfo.InvariantCulture);

        /// <summary>Parse a header value into its integer members. Returns null when the value breaks the grammar.</summary>
        public static Dictionary<string, int> TryParse(string value)
        {
            if (value == null) return null;
            var result = new Dictionary<string, int>(StringComparer.Ordinal);
            var parts = value.Split(',');
            if (parts.Length > 8) return null;
            foreach (var raw in parts)
            {
                var part = raw.Trim();
                var eq = part.IndexOf('=');
                if (eq < 1 || eq == part.Length - 1) return null;
                var key = part.Substring(0, eq);
                var num = part.Substring(eq + 1);
                if (key.Length > 16 || num.Length > 4) return null;
                foreach (var c in key) if (!((c >= 'a' && c <= 'z') || c == '_')) return null;
                foreach (var c in num) if (c < '0' || c > '9') return null;
                if (result.ContainsKey(key)) return null;
                result[key] = int.Parse(num, CultureInfo.InvariantCulture);
            }
            return result;
        }

        /// <summary>
        /// Read a response header. Absent value: <see cref="WireMode.LegacyV0"/>. Present: it must parse, carry min and max
        /// with 1 &lt;= min &lt;= max, and a <c>use</c> inside both its range and <paramref name="mine"/> (a <c>use</c> outside
        /// our own range is a protocol error, never guessed at).
        /// </summary>
        public static WireInfo ReadResponse(string value, WireRange mine)
        {
            if (value == null) return new WireInfo(WireMode.LegacyV0, null, null);
            var d = TryParse(value);
            if (d == null) throw new AtlasProtocolException("Atlas sent a malformed Atlas-Wire header.");
            int min, max, use;
            if (!d.TryGetValue("min", out min) || !d.TryGetValue("max", out max) || min < 1 || max < min)
                throw new AtlasProtocolException("Atlas sent an Atlas-Wire header without a valid min and max.");
            if (!d.TryGetValue("use", out use))
                throw new AtlasProtocolException("Atlas answered without choosing a wire version (no use=).");
            if (!mine.Contains(use) || use < min || use > max)
                throw new AtlasProtocolException(
                    "Atlas chose wire version " + use.ToString(CultureInfo.InvariantCulture) + ", which this connector (wire " + mine + ") does not speak.");
            return new WireInfo(WireMode.Negotiated, use, new WireRange(min, max));
        }
    }
}
