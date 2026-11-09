#if ATLAS_JSON_STJ
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Atlas.Client
{
    /// <summary>For hosts that ship System.Text.Json (Jellyfin). Unknown members are kept in the tree and ignored by the reader.</summary>
    public sealed class SystemTextJsonAdapter : IJsonAdapter
    {
        private static readonly JsonDocumentOptions ReadOptions = new JsonDocumentOptions { MaxDepth = 64, AllowTrailingCommas = false };

        public JsonNode Parse(string json)
        {
            try
            {
                using (var doc = JsonDocument.Parse(json ?? string.Empty, ReadOptions)) return Convert(doc.RootElement);
            }
            catch (JsonException e) { throw new AtlasProtocolException("Atlas sent invalid JSON.", e); }
        }

        private static JsonNode Convert(JsonElement e)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.Object:
                    var members = new List<KeyValuePair<string, JsonNode>>();
                    foreach (var p in e.EnumerateObject()) members.Add(new KeyValuePair<string, JsonNode>(p.Name, Convert(p.Value)));
                    return JsonNode.Object(members);
                case JsonValueKind.Array:
                    var items = new List<JsonNode>();
                    foreach (var x in e.EnumerateArray()) items.Add(Convert(x));
                    return JsonNode.Array(items);
                case JsonValueKind.String: return JsonNode.Str(e.GetString());
                case JsonValueKind.Number: return JsonNode.NumRaw(e.GetRawText());
                case JsonValueKind.True: return JsonNode.Bool(true);
                case JsonValueKind.False: return JsonNode.Bool(false);
                default: return JsonNode.Null;
            }
        }

        public string Write(JsonNode node)
        {
            using (var ms = new MemoryStream())
            {
                using (var w = new Utf8JsonWriter(ms)) WriteNode(w, node);
                return Encoding.UTF8.GetString(ms.ToArray());
            }
        }

        private static void WriteNode(Utf8JsonWriter w, JsonNode n)
        {
            switch (n.Kind)
            {
                case JsonKind.Object:
                    w.WriteStartObject();
                    foreach (var m in n.Members) { w.WritePropertyName(m.Key); WriteNode(w, m.Value); }
                    w.WriteEndObject();
                    break;
                case JsonKind.Array:
                    w.WriteStartArray();
                    foreach (var x in n.Items) WriteNode(w, x);
                    w.WriteEndArray();
                    break;
                case JsonKind.String: w.WriteStringValue(n.AsString()); break;
                case JsonKind.Number: w.WriteRawValue(n.RawNumber); break;
                case JsonKind.Bool: w.WriteBooleanValue(n.AsBool().GetValueOrDefault()); break;
                default: w.WriteNullValue(); break;
            }
        }
    }
}
#endif
