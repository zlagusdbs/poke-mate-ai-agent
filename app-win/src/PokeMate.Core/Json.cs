using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace PokeMate.Core;

/// 맥 `JSONEncoder`/`JSONDecoder`·`JSONSerialization` 자리. 키 이름은 Swift 와 같은 lowerCamel.
public static class Json
{
    /// 공통 옵션: camelCase 키, 들여쓰기, 한글 이스케이프 안 함, nil 은 키를 쓰지 않음(Swift encodeIfPresent),
    /// 문자열 enum, 날짜는 Swift 기본 형식(2001-01-01 기준 초, 실수).
    public static readonly JsonSerializerOptions Options = MakeOptions(indented: true);

    /// 들여쓰기 없는 한 줄 형식 (Swift 의 기본 JSONEncoder 출력처럼).
    public static readonly JsonSerializerOptions Compact = MakeOptions(indented: false);

    static JsonSerializerOptions MakeOptions(bool indented)
    {
        var o = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DictionaryKeyPolicy = null,
            WriteIndented = indented,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            NumberHandling = JsonNumberHandling.Strict,
        };
        o.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        o.Converters.Add(new SwiftDateConverter());
        return o;
    }

    public static string Serialize<T>(T value, bool indented = true) =>
        JsonSerializer.Serialize(value, indented ? Options : Compact);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);

    /// 실패하면 null (Swift `try? JSONDecoder().decode`).
    public static T? TryDeserialize<T>(string? json) where T : class
    {
        if (json == null) return null;
        try { return JsonSerializer.Deserialize<T>(json, Options); } catch { return null; }
    }

    /// Swift `.sortedKeys` + `.prettyPrinted` 로 쓰던 파일 형식 (키 정렬, 들여쓰기).
    public static string SortedPretty<T>(T value) => Sorted(JsonSerializer.SerializeToNode(value, Options))?.ToJsonString(Options) ?? "null";

    /// 노드의 객체 키를 재귀로 정렬한 사본 (Swift `.sortedKeys`, 바이트 순서).
    public static JsonNode? Sorted(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                var copy = new JsonObject();
                foreach (var kv in o.OrderBy(kv => kv.Key, StringComparer.Ordinal)) copy[kv.Key] = Sorted(kv.Value);
                return copy;
            case JsonArray a:
                var arr = new JsonArray();
                foreach (var item in a) arr.Add(Sorted(item));
                return arr;
            case null:
                return null;
            default:
                return JsonNode.Parse(node.ToJsonString());
        }
    }

    /// 노드 사본 (.NET 7 에는 DeepClone 이 없다).
    public static JsonNode? Clone(JsonNode? node) => node == null ? null : JsonNode.Parse(node.ToJsonString());

    /// 문자열을 JSON 객체로. 객체가 아니거나 깨졌으면 null (Swift `try? JSONSerialization.jsonObject as? [String: Any]`).
    public static JsonObject? ParseObject(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try { return JsonNode.Parse(text) as JsonObject; } catch { return null; }
    }

    public static JsonArray? ParseArray(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try { return JsonNode.Parse(text) as JsonArray; } catch { return null; }
    }

    public static string ToText(JsonNode? node, bool indented = false) =>
        node?.ToJsonString(indented ? Options : Compact) ?? "null";

    /// `as? String`
    public static string? Str(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// `as? Bool`
    public static bool? Bool(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    /// `(x as? NSNumber)?.doubleValue`
    public static double? Double(JsonNode? node)
    {
        if (node is not JsonValue v) return null;
        if (v.TryGetValue<double>(out var d)) return d;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<JsonElement>(out var e) && e.ValueKind == JsonValueKind.Number) return e.GetDouble();
        return null;
    }

    /// `(x as? NSNumber)?.intValue` (실수면 버림)
    public static long? Long(JsonNode? node)
    {
        if (node is not JsonValue v) return null;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<JsonElement>(out var e) && e.ValueKind == JsonValueKind.Number)
            return e.TryGetInt64(out var i) ? i : (long)e.GetDouble();
        if (v.TryGetValue<double>(out var d)) return (long)d;
        return null;
    }

    public static int? Int(JsonNode? node) => Long(node) is long l ? (int)l : null;

    /// `as? [String]` (문자열이 아닌 요소가 하나라도 있으면 null)
    public static List<string>? Strings(JsonNode? node)
    {
        if (node is not JsonArray a) return null;
        var list = new List<string>();
        foreach (var item in a)
        {
            if (Str(item) is not string s) return null;
            list.Add(s);
        }
        return list;
    }

    /// Swift `JSONEncoder` 기본 날짜 형식: 2001-01-01T00:00:00Z 부터의 초(실수).
    public sealed class SwiftDateConverter : JsonConverter<DateTimeOffset>
    {
        public static readonly DateTimeOffset ReferenceDate = new(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Number) return ReferenceDate.AddTicks((long)Math.Round(reader.GetDouble() * TimeSpan.TicksPerSecond));
            if (reader.TokenType == JsonTokenType.String && DateTimeOffset.TryParse(reader.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d)) return d;
            throw new JsonException("날짜 형식이 아니에요");
        }

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
            writer.WriteNumberValue((value - ReferenceDate).Ticks / (double)TimeSpan.TicksPerSecond);
    }
}
