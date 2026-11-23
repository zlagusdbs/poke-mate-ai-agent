using System.Text.Json.Nodes;
using PokeMate.Core;

namespace PokeMate.CoreChecks;

// MARK: Claude Code 도구 승인
public static class ClaudeToolApprovalChecks
{
    public static void Run()
    {
        const string line = """{"type":"control_request","request_id":"r1","request":{"subtype":"can_use_tool","tool_name":"Bash","input":{"command":"ls ~/Downloads","description":"목록"},"permission_suggestions":[{"type":"addRules","rules":[{"toolName":"Bash","ruleContent":"ls:*"}],"behavior":"allow","destination":"localSettings"},{"type":"setMode","mode":"acceptEdits","destination":"session"}]}}""";
        var obj = ClaudeStream.Object(line);
        var request = obj == null ? null : ClaudeStream.PermissionRequest(obj);
        Check.That(request?.Tool == "Bash" && request.Summary == "ls ~/Downloads" && request.Rules.SequenceEqual(new[] { "Bash(ls:*)" }), "approve: 권한 요청 읽기 (규칙 제안만)");
        if (request != null)
        {
            var deny = ClaudeStream.Object(ClaudeStream.Response(request, new PermissionDecision.Deny("거부")));
            var denyBody = deny?["response"]?["response"];
            Check.That(Json.Str(denyBody?["behavior"]) == "deny" && Json.Str(deny?["response"]?["request_id"]) == "r1", "approve: 거부 응답");
            var always = ClaudeStream.Object(ClaudeStream.Response(request, new PermissionDecision.AllowAlways()));
            var body = always?["response"]?["response"];
            var perms = body?["updatedPermissions"] as JsonArray;
            Check.That(Json.Str(body?["behavior"]) == "allow" && Json.Str(body?["updatedInput"]?["command"]) == "ls ~/Downloads"
                       && perms?.Count == 1 && Json.Str(perms[0]?["destination"]) == "session", "approve: 항상 허용은 이 실행(session) 규칙으로만");
            var once = ClaudeStream.Object(ClaudeStream.Response(request, new PermissionDecision.Allow()));
            Check.That(once?["response"]?["response"]?["updatedPermissions"] == null, "approve: 한 번 허용은 규칙 안 남김");
        }
        Check.That(ClaudeStream.PermissionRequest(new JsonObject { ["type"] = "control_request", ["request_id"] = "x", ["request"] = new JsonObject { ["subtype"] = "initialize" } }) == null,
                   "approve: 다른 제어 요청은 무시");
        Check.That(ClaudeStream.Activity("Bash", new JsonObject { ["command"] = "ls ~/Downloads" }) == "Bash 실행 중: ls ~/Downloads"
                   && ClaudeStream.Activity("mcp__pokemate__usage_summary", new JsonObject()) == "usage_summary 조회 중", "approve: 진행 한 줄");
        var dir = Check.TempDir("pm-perm");
        var permFile = Path.Combine(dir, ".claude", "settings.json");
        const string d = @"C:\d";
        Check.That(ClaudeCommand.InstallPermissions(permFile, dataDir: d) && !ClaudeCommand.InstallPermissions(permFile, dataDir: d),
                   "approve: 권한 파일은 없을 때만 만듦");
        var perm = Json.ParseObject(File.ReadAllText(permFile));
        var denyList = Json.Strings(perm?["permissions"]?["deny"]) ?? new List<string>();
        Check.That(denyList.Contains("Bash(sudo:*)") && denyList.Contains("Edit(./.claude/**)") && denyList.Contains("Write(//c/d/write-tools.json)")
                   && denyList.Contains("Edit(//c/d/write-tools.json)"), "approve: 기본 deny 에 sudo·권한 파일 보호 (Windows 경로는 //c/ 형식)");
        Check.That(denyList.Contains("PowerShell(sudo:*)") && denyList.Contains("Bash(reg add:*)") && denyList.Contains("PowerShell(reg add:*)")
                   && denyList.Contains("PowerShell(Set-ExecutionPolicy:*)") && denyList.Contains("Bash(powershell -EncodedCommand:*)")
                   && denyList.Contains("PowerShell(netsh:*)") && denyList.Contains("Bash(icacls:*)"),
                   "approve: Windows 명령도 Bash·PowerShell 양쪽에서 막음");
        var reviewAdded = new[]
        {
            "powershell -enc", "powershell -ec", "powershell -e", "pwsh -EncodedCommand", "pwsh -enc", "pwsh -ec", "pwsh -e",
            "powershell.exe -EncodedCommand", "powershell.exe -enc", "pwsh.exe -e",
            "reg.exe add", "reg.exe delete", "reg.exe import", "sc.exe", "shutdown.exe", "schtasks.exe", "net.exe user", "netsh.exe",
            "Set-MpPreference", "Add-MpPreference", "Set-ItemProperty", "New-ItemProperty", "Register-ScheduledTask",
            "New-Service", "Set-Service", "Start-Process -Verb RunAs", "New-LocalUser", "Add-LocalGroupMember",
            "New-NetFirewallRule", "Set-NetFirewallRule", "Remove-NetFirewallRule", "Disable-NetFirewallRule", "Set-NetFirewallProfile",
            "Restart-Computer", "Stop-Computer", "manage-bde", "manage-bde.exe", "vssadmin", "vssadmin.exe", "wevtutil", "wevtutil.exe",
        };
        var missingDeny = reviewAdded.SelectMany(c => new[] { $"Bash({c}:*)", $"PowerShell({c}:*)" }).Where(r => !denyList.Contains(r)).ToList();
        Check.That(missingDeny.Count == 0, $"approve: 인코딩 명령 짧은 형식·pwsh·.exe 형식·Defender·레지스트리·서비스·계정·방화벽·전원 cmdlet 도 막음 {string.Join(" ", missingDeny)}");
        Check.That(!denyList.Contains("Bash(Set-MpPreference.exe:*)") && !denyList.Contains("Bash(sudo.exe:*)") && denyList.Distinct().Count() == denyList.Count,
                   "approve: .exe 형식은 Windows 실행 파일에만, 중복 없음");
        Check.That(denyList.IndexOf("Bash(sudo:*)") == 0 && denyList.IndexOf("Bash(powershell -EncodedCommand:*)") < denyList.IndexOf("Bash(pwsh -enc:*)"),
                   "approve: 앞서 있던 deny 순서는 그대로, 새 항목은 뒤에");
        Check.That(Json.Strings(perm?["permissions"]?["allow"])?.Count == 0 && Json.Strings(perm?["permissions"]?["ask"])?.Count == 0, "approve: 새 권한 파일은 allow·ask 빈 목록");
        Check.Remove(dir);

        // 설정 층: 이미 있는 권한 파일에 local 설정 파일 보호를 더한다 (맥 `설정 층` 절의 권한 부분)
        var root = Check.TempDir("pm-layers");
        var permPath = Path.Combine(root, "settings.json");
        File.WriteAllText(permPath, """{"permissions": {"deny": ["Bash(rm:*)"], "allow": []}}""");
        var merged = ClaudeCommand.InstallPermissions(permPath, dataDir: d, localDir: @"C:\r\local");
        var mergedDeny = Json.Strings(Json.ParseObject(File.ReadAllText(permPath))?["permissions"]?["deny"]) ?? new List<string>();
        Check.That(merged && mergedDeny.FirstOrDefault() == "Bash(rm:*)" && mergedDeny.Contains("Edit(//c/r/local/write-tools.json)") && !mergedDeny.Contains("Edit(//c/r/local/alarms.json)")
                   && mergedDeny.Contains("Write(//c/r/local/domains.json)") && mergedDeny.Contains("PowerShell(Set-MpPreference:*)") && mergedDeny.Contains("Bash(reg.exe add:*)")
                   && !ClaudeCommand.InstallPermissions(permPath, dataDir: d, localDir: @"C:\r\local"),
                   "layers: 기존 권한 파일에 local 설정 파일 쓰기 금지를 더함 (있던 것은 그대로)");
        Check.That(ClaudeCommand.LoadDeny(permPath).Contains("Bash(rm:*)") && ClaudeCommand.LoadDeny(permPath + ".none").Count == 0, "approve: deny 목록 읽기");
        Check.Remove(root);

        var gate = new PermissionAlertGate();
        var t = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        Check.That(gate.ShouldAlert(t) && !gate.ShouldAlert(t.AddSeconds(3)) && gate.ShouldAlert(t.AddSeconds(9)),
                   "approve: 카드가 연달아 뜨면 5초 안에는 알림 하나");
        if (request != null)
        {
            Check.That(PermissionAlertGate.Body(request) == "Bash: ls ~/Downloads", "approve: 알림 본문은 도구와 명령 한 줄");
            Check.That(PermissionAlertGate.Body(request, limit: 6) == "Bash: …", "approve: 긴 본문은 자름");
        }
        Check.That(AppSettings.Default.ClaudeTools == false && Json.TryDeserialize<AppSettings>("""{"claudeTools":true}""")?.ClaudeTools == true,
                   "approve: 도구 쓰기는 기본 꺼짐, 저장값 읽음");
    }
}
