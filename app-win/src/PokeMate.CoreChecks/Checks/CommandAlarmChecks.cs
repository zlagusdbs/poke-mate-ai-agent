using PokeMate.Core;

namespace PokeMate.CoreChecks;

// MARK: 명령 실행 알람 (ShellCommand·기본 deny 부분. Alarm·AlarmProposal·AlarmRunLog 검사는 그 형식을 옮기는 작업에서)
public static class CommandAlarmChecks
{
    public static async Task Run()
    {
        Check.That(ShellCommand.Tail("a\nb\n\nc\nd\ne\nf\n", lines: 3) == "d\ne\nf", "command: 출력 마지막 줄만");
        Check.That(ShellCommand.Tail("a\r\nb\r\nc\r\n", lines: 2) == "b\nc", "command: CRLF 출력도 줄로 나눔");
        var home = Path.GetTempPath();
        var ok = await ShellCommand.Run("echo hello; echo world", home: home);
        Check.That(ok.Ok && ok.Tail == "hello\nworld", "command: 성공은 종료 코드 0, 출력 마지막 줄");
        var bad = await ShellCommand.Run("[Console]::Error.WriteLine('oops'); exit 3", home: home);
        Check.That(!bad.Ok && bad.ExitCode == 3 && bad.Tail.Contains("oops"), "command: 실패는 종료 코드와 오류 출력");
        var slow = await ShellCommand.Run("Start-Sleep -Seconds 5", timeout: TimeSpan.FromSeconds(1), home: home);
        Check.That(!slow.Ok && slow.TimedOut, "command: 제한 시간 넘기면 멈춤");
        var korean = await ShellCommand.Run("Write-Output \"한글 출력 '따옴표'\"", home: home);
        Check.That(korean.Ok && korean.Tail == "한글 출력 '따옴표'", "command: 한글·따옴표 출력은 UTF-8 로 그대로");
        var where = await ShellCommand.Run("(Get-Location).Path", home: home);
        Check.That(where.Tail.TrimEnd('\\') == home.TrimEnd('\\'), "command: 홈 폴더에서 실행");
        Check.That(ClaudeCommand.DefaultDeny(dataDir: @"C:\d").Contains("Edit(//c/d/alarms.json)"), "command: 메이트 도구로 알람 파일 수정 금지 (기본 deny)");
    }
}
