using System;
using System.Collections.Generic;
using System.Globalization;

namespace Atlas.Client
{
    public enum JsonKind { Null, Bool, Number, String, Array, Object }

    /// <summary>
    /// A tiny neutral JSON tree. The host's own serializer (System.Text.Json or Newtonsoft) only turns text into this tree
    /// and back; every Atlas model is read from the tree by <see cref="AtlasReader"/>. That is the tolerant reader: a missing
    /// member, an unknown member or a member of the wrong type never throws.
    /// </summary>
    public sealed class JsonNode
    {
        public static readonly JsonNode Null = new JsonNode(JsonKind.Null, null, null, null);

        private readonly string _text;                                   // string value, or the raw number text
        private readonly List<JsonNode> _items;
        private readonly List<KeyValuePair<string, JsonNode>> _members;

        private JsonNode(JsonKind kind, string text, List<JsonNode> items, List<KeyValuePair<string, JsonNode>> members)
        {
            Kind = kind; _text = text; _items = items; _members = members;
        }

        public JsonKind Kind { get; }

        public static JsonNode Str(string value) => value == null ? Null : new JsonNode(JsonKind.String, value, null, null);
        public static JsonNode Bool(bool value) => new JsonNode(JsonKind.Bool, value ? "true" : "false", null, null);
        public static JsonNode Num(long value) => new JsonNode(JsonKind.Number, value.ToString(CultureInfo.InvariantCulture), null, null);
        public static JsonNode NumRaw(string raw) => new JsonNode(JsonKind.Number, raw, null, null);
        public static JsonNode Array(IEnumerable<JsonNode> items) => new JsonNode(JsonKind.Array, null, new List<JsonNode>(items), null);
        public static JsonNode Object(IEnumerable<KeyValuePair<string, JsonNode>> members) =>
            new JsonNode(JsonKind.Object, null, null, new List<KeyValuePair<string, JsonNode>>(members));

        public bool IsNull => Kind == JsonKind.Null;
        public IReadOnlyList<JsonNode> Items => _items ?? (IReadOnlyList<JsonNode>)System.Array.Empty<JsonNode>();
        public IReadOnlyList<KeyValuePair<string, JsonNode>> Members =>
            _members ?? (IReadOnlyList<KeyValuePair<string, JsonNode>>)System.Array.Empty<KeyValuePair<string, JsonNode>>();

        /// <summary>The member called <paramref name="name"/>, or <see cref="Null"/> (never null) when absent or not an object.</summary>
        public JsonNode this[string name]
        {
            get
            {
                if (_members != null)
                    for (var i = _members.Count - 1; i >= 0; i--)          // last duplicate wins, like most parsers
                        if (string.Equals(_members[i].Key, name, StringComparison.Ordinal)) return _members[i].Value;
                return Null;
            }
        }

        public bool Has(string name)
        {
            if (_members != null)
                foreach (var m in _members) if (string.Equals(m.Key, name, StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>The string value, or null when this is not a string.</summary>
        public string AsString() => Kind == JsonKind.String ? _text : null;

        public bool? AsBool() => Kind == JsonKind.Bool ? (bool?)(_text == "true") : null;

        /// <summary>The integer value, or null when this is not a number or is not a whole number in range.</summary>
        public long? AsLong()
        {
            if (Kind != JsonKind.Number) return null;
            long l;
            if (long.TryParse(_text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out l)) return l;
            double d;
            if (double.TryParse(_text, NumberStyles.Float, CultureInfo.InvariantCulture, out d) && d == Math.Floor(d) && Math.Abs(d) < 9e15)
                return (long)d;
            return null;
        }

        public double? AsDouble()
        {
            if (Kind != JsonKind.Number) return null;
            double d;
            return double.TryParse(_text, NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? (double?)d : null;
        }

        public string RawNumber => Kind == JsonKind.Number ? _text : null;
    }

    /// <summary>The pluggable serializer boundary. Implementations must tolerate (keep, not reject) unknown members.</summary>
    public interface IJsonAdapter
    {
        /// <summary>Parse text into a tree. Throws <see cref="AtlasProtocolException"/> (not a serializer type) on invalid JSON.</summary>
        JsonNode Parse(string json);

        string Write(JsonNode node);
    }
}
