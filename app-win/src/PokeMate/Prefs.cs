using System.Text.Json.Nodes;

namespace PokeMate;

/// 맥의 UserDefaults 자리. 데이터 폴더의 ui-prefs.json 에 가벼운 화면 설정(시작 탭, 창 크기, 고정, 그래프 펼침 …)을 둔다.
public static class Prefs
{
    static JsonObject? cache;
    static readonly object Gate = new();

    static string File => Path.Combine(AppPaths.DataDir, "ui-prefs.json");

    static JsonObject Load()
    {
        if (cache != null) return cache;
        try { cache = Json.ParseObject(System.IO.File.ReadAllText(File)); } catch { }
        return cache ??= new JsonObject();
    }

    public static bool Bool(string key, bool fallback = false)
    {
        lock (Gate) return Json.Bool(Load()[key]) ?? fallback;
    }

    public static double Double(string key, double fallback = 0)
    {
        lock (Gate) return Json.Double(Load()[key]) ?? fallback;
    }

    public static string? String(string key)
    {
        lock (Gate) return Json.Str(Load()[key]);
    }

    public static void Set(string key, bool value) => Write(key, JsonValue.Create(value));
    public static void Set(string key, double value) => Write(key, JsonValue.Create(value));
    public static void Set(string key, string value) => Write(key, JsonValue.Create(value));

    static void Write(string key, JsonNode? value)
    {
        lock (Gate)
        {
            var obj = Load();
            obj[key] = value;
            try { AppPaths.WriteAtomic(File, Json.ToText(Json.Sorted(obj), indented: true)); } catch { }
        }
    }
}
