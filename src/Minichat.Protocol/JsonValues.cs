using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Minichat.Protocol;

public static class JsonValues
{
    public static readonly JsonSerializerOptions Options = new()
    {
        TypeInfoResolver = ProtocolJson.Default,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    public static JsonObject ParseObject(string json) => JsonNode.Parse(json)?.AsObject() ?? new();
    public static string GetText(JsonNode? node, string fallback = "") => node is null ? fallback : node.ToString();
    public static string GetString(this JsonNode? node, string key, string fallback = "") => GetText((node as JsonObject)?[key], fallback);
    public static int GetInt32(this JsonNode? node, string key, int fallback = 0) => int.TryParse((node as JsonObject)?[key]?.ToString(), out var value) ? value : fallback;
    public static bool GetBoolean(this JsonNode? node, string key, bool fallback = false) => bool.TryParse((node as JsonObject)?[key]?.ToString(), out var value) ? value : fallback;
    public static JsonArray GetArray(this JsonNode? node, string key) => (node as JsonObject)?[key] as JsonArray ?? new();
    public static JsonObject GetObject(this JsonNode? node, string key) => (node as JsonObject)?[key] as JsonObject ?? new();
    public static JsonNode? Clone(JsonNode? node) => node?.DeepClone();
    public static void AddNode(this JsonArray array, JsonNode? node) => array.Add(node);
    public static void AddNode(this JsonArray array, string value) => array.Add((JsonNode?)JsonValue.Create(value));
    public static string Serialize(JsonNode? node)
    {
        if (node is null)
        {
            return "null";
        }

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = Options.Encoder }))
        {
            node.WriteTo(writer, Options);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }
    public static JsonObject Error(string message, string code = "error") => new() { ["error"] = message, ["code"] = code };
    public static string NewId() => Guid.NewGuid().ToString("N")[..12];

}

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(JsonNode))]
[JsonSerializable(typeof(JsonObject))]
[JsonSerializable(typeof(JsonArray))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(double))]
internal partial class ProtocolJson : JsonSerializerContext;
