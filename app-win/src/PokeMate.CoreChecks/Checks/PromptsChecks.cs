using PokeMate.Core;

namespace PokeMate.CoreChecks;

// MARK: prompts, 옛 이름 호환
public static class PromptsChecks
{
    public static void Run()
    {
        Check.That(AlarmStore.PromptName("  {{prompt:jira}} ") == "jira" && AlarmStore.PromptName("{{file:~/a.md}}") == null && AlarmStore.PromptName("{{prompt:}}") == null,
                   "prompts: {{prompt:이름}} 읽기");
        var dir = Check.TempDir("pm-prompts");
        File.WriteAllText(Path.Combine(dir, "jira.md"), "# 지라\n할 일");
        File.WriteAllText(Path.Combine(dir, "notes.txt"), "x");
        Check.That(Rules.PromptNames(dir).SequenceEqual(new[] { "jira" }), "prompts: 목록은 md 파일 이름");
        var a = new Alarm("t", "{{prompt:jira}}", 9, 0, new Alarm.Repeat.Daily());
        Check.That(AlarmStore.ResolvePrompt(a, AppSettings.Default, dir) == "# 지라\n할 일", "prompts: 알람 내용에서 프롬프트 파일을 읽음");
        var missing = new Alarm("t", "{{prompt:none}}", 9, 0, new Alarm.Repeat.Daily());
        Check.That(AlarmStore.ResolvePrompt(missing, AppSettings.Default, dir) == null, "prompts: 없는 프롬프트는 읽지 못함(실패로 알림)");
        Check.That(new Rules.File(Path.Combine(dir, "prompts", "x.md")).Kind == Rules.Kind.Prompt
                   && new Rules.File(@"C:\d\prompts\x.md").Name == "prompts/x.md", "prompts: 폴더 종류");
        Check.Remove(dir);

        // 옛 이름 호환
        var legacy = "```" + ChatBlock.LegacyTag + "-alarm\n[{\"title\":\"a\",\"hour\":9,\"repeat\":\"daily\"}]\n```";
        var fresh = "```" + ChatBlock.Tag + "-alarm\n[{\"title\":\"b\",\"hour\":9,\"repeat\":\"daily\"}]\n```";
        Check.That(AlarmProposal.Extract("안내\n" + legacy).Alarms[0].Title == "a" && AlarmProposal.Extract(legacy).Text.Length == 0
                   && AlarmProposal.Extract(fresh).Alarms[0].Title == "b", "legacy: 옛 블록 태그도 읽음");
    }
}
