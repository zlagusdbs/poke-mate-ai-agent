namespace PokeMate;

public static class ModelLabels
{
    public static readonly IReadOnlyDictionary<string, string> Models = new Dictionary<string, string>
    {
        [""] = "기본 (계정 기본)", ["opus"] = "Opus", ["sonnet"] = "Sonnet", ["fable"] = "Fable",
    };

    public static readonly IReadOnlyDictionary<string, string> Efforts = new Dictionary<string, string>
    {
        [""] = "기본", ["low"] = "낮음", ["medium"] = "보통", ["high"] = "높음", ["xhigh"] = "아주 높음", ["max"] = "최대",
    };

    public static string ShortModel(string key) => key.Length == 0 ? "기본 모델" : Models.GetValueOrDefault(key) ?? key;
    public static string ShortEffort(string key) => key.Length == 0 ? "기본 추론" : Efforts.GetValueOrDefault(key) ?? key;
}
