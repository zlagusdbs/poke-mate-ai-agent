using PokeMate.Core;

namespace PokeMate.CoreChecks;

// MARK: 자동 처리 모델
public static class AutomationModelChecks
{
    public static void Run()
    {
        var old = Json.TryDeserialize<AppSettings>("""{"model": "fable", "effort": "medium"}""");
        Check.That(old?.AutomationModel == "fable" && old.AutomationEffort == "medium", "model: 예전 설정은 자동 처리 모델로 그대로 이어짐");
        var split = Json.TryDeserialize<AppSettings>("""{"model": "opus", "effort": "high", "automationModel": "sonnet", "automationEffort": "low"}""");
        Check.That(split?.Model == "opus" && split.AutomationModel == "sonnet" && split.AutomationEffort == "low", "model: 대화·자동 처리 모델은 따로 저장");
    }
}
