#if ATLAS_JSON_NEWTONSOFT
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Newtonsoft.Json;

namespace Atlas.Client
{
    /// <summary>For hosts that ship Newtonsoft.Json (Playnite). Reads tokens directly, so dates stay text and numbers keep their digits.</summary>
    public sealed class NewtonsoftJsonAdapter : IJsonAdapter
    {
        public JsonNode Parse(string json)
        {
            try
            {
                using (var sr = new StringReader(json ?? string.Empty))
                using (var r = new JsonTextReader(sr) { DateParseHandling = DateParseHandling.None, FloatParseHandling = FloatParseHandling.Double, MaxDepth = 64 })
                {
                    if (!r.Read()) throw new AtlasProtocolException("Atlas sent an empty body.");
                    var node = ReadValue(r);
                    while (r.Read()) { }   // trailing garbage makes the reader throw, like the other adapter
                    return node;
                }
            }
            catch (JsonException e) { throw new AtlasProtocolException("Atlas sent invalid JSON.", e); }
        }

        private static JsonNode ReadValue(JsonTextReader r)
        {
            switch (r.TokenType)
            {
                case JsonToken.StartObject:
                    var members = new List<KeyValuePair<string, JsonNode>>();
                    while (r.Read() && r.TokenType != JsonToken.EndObject)
                    {
                        var name = (string)r.Value;
                        r.Read();
                        members.Add(new KeyValuePair<string, JsonNode>(name, ReadValue(r)));
                    }
                    if (r.TokenType != JsonToken.EndObject) throw new AtlasProtocolException("Atlas sent truncated JSON.");
                    return JsonNode.Object(members);
                case JsonToken.StartArray:
                    var items = new List<JsonNode>();
                    while (r.Read() && r.TokenType != JsonToken.EndArray) items.Add(ReadValue(r));
                    if (r.TokenType != JsonToken.EndArray) throw new AtlasProtocolException("Atlas sent truncated JSON.");
                    return JsonNode.Array(items);
                case JsonToken.String: return JsonNode.Str((string)r.Value);
                case JsonToken.Integer: return JsonNode.NumRaw(Convert.ToString(r.Value, CultureInfo.InvariantCulture));
                case JsonToken.Float: return JsonNode.NumRaw(((double)r.Value).ToString("R", CultureInfo.InvariantCulture));
                case JsonToken.Boolean: return JsonNode.Bool((bool)r.Value);
                default: return JsonNode.Null;
            }
        }

        public string Write(JsonNode node)
        {
            var sw = new StringWriter(CultureInfo.InvariantCulture);
            using (var w = new JsonTextWriter(sw) { Formatting = Formatting.None })
                WriteNode(w, node);
            return sw.ToString();
        }

        private static void WriteNode(JsonTextWriter w, JsonNode n)
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
                case JsonKind.String: w.WriteValue(n.AsString()); break;
                case JsonKind.Number: w.WriteRawValue(n.RawNumber); break;
                case JsonKind.Bool: w.WriteValue(n.AsBool().GetValueOrDefault()); break;
                default: w.WriteNull(); break;
            }
        }
    }
}
#endif
