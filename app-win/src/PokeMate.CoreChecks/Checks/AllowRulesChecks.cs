using System.Text.Json.Nodes;
using PokeMate.Core;

namespace PokeMate.CoreChecks;

// MARK: 항상 허용 규칙
public static class AllowRulesChecks
{
    public static void Run()
    {
        Check.That(ShellPrograms.Programs("find ~/x -type f | sed 's|a;b|c|' | awk -F. '{print $NF}' | sort && uniq -c").Programs.SequenceEqual(new[] { "find", "sed", "awk", "sort", "uniq" }),
                   "allow: 파이프·&& 로 나누고 따옴표 안의 | ; 는 무시");
        Check.That(ShellPrograms.Programs("git log -1 --oneline; git push origin HEAD; ls -la").Programs.SequenceEqual(new[] { "git log", "git push", "ls" }), "allow: git 은 하위 명령까지");
        Check.That(ShellPrograms.Programs("C=~/Downloads; find \"$C\"").Problem?.Contains("변수 대입") == true, "allow: 변수 대입은 항상 허용 대상에서 뺌");
        Check.That(ShellPrograms.Programs("echo $(whoami)").Problem?.Contains("$(") == true && ShellPrograms.Programs("echo \"$(date)\"").Problem != null,
                   "allow: $(…) 은 뺌 (따옴표 안 포함)");
        Check.That(ShellPrograms.Programs("echo '$(not)' > /tmp/out.txt 2>&1").Programs.SequenceEqual(new[] { "echo" }), "allow: 작은따옴표 안 $( 는 글자, 리다이렉트 대상은 프로그램 아님");
        Check.That(ShellPrograms.Programs("sed -i '' 's/a/b/' f").Programs.SequenceEqual(new[] { "sed -i" }) && ShellPrograms.IsRisky("sed -i") && ShellPrograms.IsRisky("git push")
                   && !ShellPrograms.IsRisky("git log") && ShellPrograms.IsRisky("rm"), "allow: 바꾸는 프로그램은 주의");
        var deny = new[] { "Bash(sudo:*)", "Bash(defaults write:*)" };
        var m1 = AllowRules.Menu("Bash", new JsonObject { ["command"] = "find . | sort | head -3" }, Array.Empty<string>(), deny);
        Check.That(m1.Unavailable == null && m1.Options.Count == 4 && m1.Options[0].Rules.SequenceEqual(new[] { "Bash(find:*)", "Bash(sort:*)", "Bash(head:*)" }),
                   "allow: 제안이 없어도 묶음 + 프로그램마다 항목");
        var m2 = AllowRules.Menu("Bash", new JsonObject { ["command"] = "ls && sudo -n true" }, Array.Empty<string>(), deny);
        Check.That(m2.Options.Count == 0 && m2.Unavailable?.Contains("sudo") == true, "allow: deny 프로그램이 들어 있으면 메뉴에 안 나옴");
        var m3 = AllowRules.Menu("Bash", new JsonObject { ["command"] = "rm -f /tmp/x" }, new[] { "Bash(rm -f /tmp/x)" }, deny);
        Check.That(m3.Options[0].Label.StartsWith("Claude Code 제안") && m3.Options[^1].Caution, "allow: 제안 + rm 은 주의");
        var m4 = AllowRules.Menu("WebFetch", new JsonObject { ["url"] = "https://docs.example.com/a" }, Array.Empty<string>(), deny);
        Check.That(m4.Options.FirstOrDefault()?.Rules.SequenceEqual(new[] { "WebFetch(domain:docs.example.com)" }) == true, "allow: WebFetch 는 도메인 규칙");
        var m5 = AllowRules.Menu("Edit", new JsonObject { ["file_path"] = @"C:\Users\u\notes\a.md" }, Array.Empty<string>(), deny);
        Check.That(m5.Options[0].Rules.SequenceEqual(new[] { "Edit(//c/Users/u/notes/a.md)" }) && m5.Options[0].Label == "이 파일 (a.md)" && m5.Options[1].Rules.SequenceEqual(new[] { "Edit" }),
                   "allow: 파일 규칙은 Claude Code 의 Windows 경로 형식");
        var req = ClaudeStream.PermissionRequest(new JsonObject
        {
            ["type"] = "control_request", ["request_id"] = "r9",
            ["request"] = new JsonObject { ["subtype"] = "can_use_tool", ["tool_name"] = "Bash", ["input"] = new JsonObject { ["command"] = "find ." }, ["permission_suggestions"] = new JsonArray() },
        })!;
        var resp = ClaudeStream.Object(ClaudeStream.Response(req, new PermissionDecision.AllowRules(new[] { "Bash(find:*)", "Bash(sort:*)" })));
        var perms = (resp?["response"]?["response"]?["updatedPermissions"] as JsonArray)?[0];
        var rules = perms?["rules"] as JsonArray;
        Check.That(Json.Str(perms?["destination"]) == "session" && rules?.Count == 2 && Json.Str(rules[0]?["ruleContent"]) == "find:*" && Json.Str(rules[0]?["toolName"]) == "Bash",
                   "allow: 고른 규칙을 이번 실행(session) 규칙으로 보냄");
        Check.That(new PermissionDecision.AllowRules(new[] { "a" }) == new PermissionDecision.AllowRules(new List<string> { "a" }), "allow: 결정 비교는 내용으로");

        // PowerShell: 맥 Bash 보다 약해지지 않게 "PowerShell 전체" 같은 항상 허용은 없다
        var ps = AllowRules.Menu("PowerShell", new JsonObject { ["command"] = "Get-ChildItem C:/ | Remove-Item -Recurse" }, Array.Empty<string>(), deny);
        Check.That(ps.Options.Count == 0 && ps.Unavailable == AllowRules.PowerShellUnavailable && !ps.Options.Any(o => o.Rules.Contains("PowerShell")),
                   "allow: PowerShell 은 항상 허용 메뉴 없이 한 번 허용만");
        var psSuggested = AllowRules.Menu("PowerShell", new JsonObject { ["command"] = "Get-Date" }, new[] { "PowerShell(Get-Date:*)" }, deny);
        Check.That(psSuggested.Options.Count == 1 && psSuggested.Options[0].Rules.SequenceEqual(new[] { "PowerShell(Get-Date:*)" }) && psSuggested.Unavailable != null,
                   "allow: PowerShell 은 Claude Code 의 좁은 제안만 그대로");
        var psWhole = new[] { new[] { "PowerShell" }, new[] { "PowerShell(*)" }, new[] { "PowerShell(:*)" }, new[] { "PowerShell(Get-Date:*)", "PowerShell" } }
            .Select(s => AllowRules.Menu("PowerShell", new JsonObject { ["command"] = "Get-Date" }, s, deny)).ToList();
        Check.That(psWhole.All(m => m.Options.Count == 0 && m.Unavailable != null), "allow: PowerShell 전체를 여는 제안은 빼고 보여 주지 않음");
        Check.That(AllowRules.Menu("Bash", new JsonObject { ["command"] = "ls" }, Array.Empty<string>(), deny).Options.All(o => !o.Rules.Contains("Bash")),
                   "allow: Bash 도 도구 전체 규칙은 만들지 않음");
        var longCommand = "Get-ChildItem -Recurse -Path C:/work | Where-Object { $_.Length -gt 100 } | ForEach-Object { $_.FullName } " + new string('x', 300) + " ; Remove-Item C:/secret";
        var psReq = ClaudeStream.PermissionRequest(new JsonObject
        {
            ["type"] = "control_request", ["request_id"] = "r10",
            ["request"] = new JsonObject { ["subtype"] = "can_use_tool", ["tool_name"] = "PowerShell", ["input"] = new JsonObject { ["command"] = longCommand, ["description"] = "파일 찾기" } },
        })!;
        Check.That(psReq.Detail.Contains(longCommand) && psReq.Detail.EndsWith("파일 찾기") && psReq.Detail.Contains("Remove-Item C:/secret"),
                   "allow: PowerShell 승인 카드는 명령 전체를 자르지 않고 보여 줌");
    }
}
