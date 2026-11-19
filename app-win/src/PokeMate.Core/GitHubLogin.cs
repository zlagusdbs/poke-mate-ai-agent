using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace PokeMate.Core;

/// `gh auth login --web` 대신 실행. 한 번용 코드를 화면에 보여 주고, 브라우저에서 승인하면 끝난다.
public sealed class GitHubLogin
{
    public abstract record Event
    {
        /// 브라우저에 입력할 한 번용 코드
        public sealed record Code(string Value) : Event;
        public sealed record Finished(bool Ok) : Event;
    }

    readonly object gate = new();
    Process? process;
    string buffer = "";

    /// PATH 에서 찾고, 없으면 `%ProgramFiles%\GitHub CLI\gh.exe`, 그것도 없으면 "gh".
    public static string GhPath()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim().Trim('"'), "gh.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch { }
        }
        var installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "GitHub CLI", "gh.exe");
        return File.Exists(installed) ? installed : "gh";
    }

    static readonly Regex CodePattern = new("[A-Z0-9]{4}-[A-Z0-9]{4}");

    public static string? Code(string text)
    {
        var m = CodePattern.Match(text);
        return m.Success ? m.Value : null;
    }

    /// onEvent 는 Start 를 부른 스레드의 SynchronizationContext(화면 스레드)에서 불린다.
    public void Start(string host, AppSettings settings, Action<Event> onEvent)
    {
        Cancel();
        var context = SynchronizationContext.Current;
        void Post(Event e)
        {
            if (context != null) context.Post(_ => onEvent(e), null);
            else onEvent(e);
        }
        var p = new Process
        {
            StartInfo = ProcessRunner.StartInfo(GhPath(), new[] { "auth", "login", "--hostname", host, "--web", "--git-protocol", "https" },
                                                null, AppPaths.ProcessEnvironment(settings)),
        };
        try { p.Start(); }
        catch
        {
            onEvent(new Event.Finished(false));
            return;
        }
        lock (gate)
        {
            process = p;
            buffer = "";
        }
        var stdin = p.StandardInput.BaseStream;
        var sentCode = false;
        void OnChunk(string chunk)
        {
            string? code = null;
            var pressEnter = false;
            lock (gate)
            {
                buffer += chunk;
                if (!sentCode && Code(buffer) is string c)
                {
                    sentCode = true;
                    code = c;
                }
                if (buffer.Contains("press enter", StringComparison.OrdinalIgnoreCase))
                {
                    pressEnter = true;
                    buffer = Regex.Replace(buffer, "Press Enter", "", RegexOptions.IgnoreCase);
                }
            }
            if (code != null) Post(new Event.Code(code));
            if (pressEnter)
            {
                try
                {
                    stdin.Write(Encoding.UTF8.GetBytes("\n"));
                    stdin.Flush();
                }
                catch { }
            }
        }
        var outRead = ClaudeLogin.ReadChunks(p.StandardOutput, OnChunk);
        var errRead = ClaudeLogin.ReadChunks(p.StandardError, OnChunk);
        _ = Task.Run(async () =>
        {
            await p.WaitForExitAsync();
            await Task.WhenAny(Task.WhenAll(outRead, errRead), Task.Delay(2000));
            Post(new Event.Finished(p.ExitCode == 0));
        });
    }

    public void Cancel()
    {
        Process? p;
        lock (gate)
        {
            p = process;
            process = null;
        }
        if (p != null) ProcessRunner.KillTree(p);
    }
}
