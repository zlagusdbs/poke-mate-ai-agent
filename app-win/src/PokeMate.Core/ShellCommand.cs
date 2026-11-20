namespace PokeMate.Core;

public sealed record ShellResult(int ExitCode, bool TimedOut, string Tail)
{
    public bool Ok => ExitCode == 0 && !TimedOut;
}

/// 명령 알람 실행. 맥의 `zsh -lc` 대신 `powershell.exe -NoProfile -ExecutionPolicy Bypass -Command` (홈 폴더에서, 5분). 출력은 UTF-8 로 읽는다.
public static class ShellCommand
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(300);

    /// PowerShell 5.1 은 파이프로 내보낼 때 시스템 코드 페이지를 쓰므로 UTF-8 로 바꾼 뒤 명령을 실행한다.
    const string Utf8Prelude = "[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false); $OutputEncoding = [Console]::OutputEncoding; ";

    public static string Tail(string text, int lines = 5, int limit = 600)
    {
        var kept = string.Join("\n", SwiftText.Lf(text).Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(lines));
        return SwiftText.Count(kept) > limit ? "…" + SwiftText.Suffix(kept, limit) : kept;
    }

    public static async Task<ShellResult> Run(string command, TimeSpan? timeout = null, string? home = null)
    {
        var limit = timeout ?? DefaultTimeout;
        try
        {
            var output = await new ProcessRunner().Run("powershell.exe",
                new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", Utf8Prelude + command },
                workDir: home ?? Paths.Home, timeout: limit);
            var text = output.Stdout + "\n" + output.Stderr;
            return new ShellResult(output.ExitCode, output.TimedOut,
                output.TimedOut ? $"제한 시간 {(int)(limit.TotalSeconds / 60)}분을 넘겨 멈췄어요\n" + Tail(text) : Tail(text));
        }
        catch (Exception e)
        {
            return new ShellResult(-1, false, e.Message);
        }
    }
}
