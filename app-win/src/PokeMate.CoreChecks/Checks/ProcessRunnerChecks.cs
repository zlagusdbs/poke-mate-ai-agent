using System.Diagnostics;
using PokeMate.Core;

namespace PokeMate.CoreChecks;

// Review Focus 1·5: 공백·한글 경로와 인자, 프로세스 트리째 종료
public static class ProcessRunnerChecks
{
    /// 이 검사 프로그램 자신을 하위 프로세스로 부르는 방법 (apphost exe, 또는 dotnet + dll)
    public static (string exe, List<string> prefix) Self(string? baseDir = null)
    {
        var dir = baseDir ?? AppContext.BaseDirectory;
        var apphost = Path.Combine(dir, "PokeMate.CoreChecks.exe");
        if (File.Exists(apphost)) return (apphost, new List<string>());
        return ("dotnet", new List<string> { Path.Combine(dir, "PokeMate.CoreChecks.dll") });
    }

    static bool Alive(int pid)
    {
        try { return !Process.GetProcessById(pid).HasExited; } catch { return false; }
    }

    static async Task<int?> WaitPid(string file)
    {
        for (var i = 0; i < 100; i++)
        {
            try
            {
                if (File.Exists(file) && int.TryParse(File.ReadAllText(file).Trim(), out var pid)) return pid;
            }
            catch (IOException) { }   // 자식이 아직 쓰는 중
            await Task.Delay(100);
        }
        return null;
    }

    public static async Task Run()
    {
        var root = Check.TempDir("pm-proc");
        var korean = Path.Combine(root, "홍 길동");
        var bin = Path.Combine(korean, "bin");
        Directory.CreateDirectory(bin);
        foreach (var f in Directory.GetFiles(AppContext.BaseDirectory)) File.Copy(f, Path.Combine(bin, Path.GetFileName(f)));
        var (exe, prefix) = Self(bin);

        // 1. 공백·한글이 든 실행 파일 경로, 인자, 작업 폴더, 환경 변수
        var weird = new[] { @"C:\Users\홍 길동\.local\bin\claude.exe", "두 단어", "", "따옴표\"안", @"끝이 역슬래시\", "a b\\\"c", "--flag=값 있음" };
        var env = new Dictionary<string, string>(Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => (string?)e.Value ?? ""), StringComparer.OrdinalIgnoreCase) { ["PM_CHECK_VALUE"] = "환경 값" };
        var echo = await new ProcessRunner().Run(exe, prefix.Concat(new[] { "--echo-args" }).Concat(weird).ToList(), workDir: korean, environment: env, timeout: TimeSpan.FromSeconds(30));
        var lines = echo.Stdout.Split('\n');
        Check.That(echo.ExitCode == 0 && lines.Take(weird.Length).SequenceEqual(weird), "process: 공백·한글·따옴표 든 인자를 그대로 되돌려 받음");
        Check.That(lines.Contains("cwd=" + korean) && lines.Contains("env=환경 값"), "process: 한글 작업 폴더·환경 변수");

        // 표준 입력·출력은 UTF-8 (BOM 없음)
        var stdin = "첫 줄 한글 ✔\n둘째 줄\n";
        var back = await new ProcessRunner().Run(exe, prefix.Append("--echo-stdin").ToList(), stdin: stdin, timeout: TimeSpan.FromSeconds(30));
        Check.That(back.Stdout == stdin, "process: 표준 입력·출력 UTF-8 왕복");

        // 없는 실행 파일은 예외 (맥 Process.run 과 같이)
        var threw = false;
        try { await new ProcessRunner().Run(Path.Combine(root, "없음.exe"), Array.Empty<string>()); } catch { threw = true; }
        Check.That(threw, "process: 없는 실행 파일은 예외");

        // 5. 자식을 띄운 cmd 를 취소하면 자식까지 끝남
        var pidFile = Path.Combine(root, "child.pid");
        var self = Self();
        var runner = new ProcessRunner();
        var sw = Stopwatch.StartNew();
        var running = runner.Run("cmd.exe", new[] { "/c", self.exe }.Concat(self.prefix).Concat(new[] { "--sleep", pidFile }).ToList(), timeout: TimeSpan.FromSeconds(60));
        var pid = await WaitPid(pidFile);
        runner.Cancel();
        var cancelled = await running;
        await Task.Delay(300);
        Check.That(pid != null && !Alive(pid.Value) && sw.Elapsed < TimeSpan.FromSeconds(20), "process: 취소하면 cmd 가 띄운 자식 프로세스까지 끝남");
        Check.That(!cancelled.TimedOut && cancelled.ExitCode != 0, "process: 취소는 제한 시간 초과가 아님");

        // 제한 시간 초과도 트리째
        var pidFile2 = Path.Combine(root, "child2.pid");
        var timed = await new ProcessRunner().Run("cmd.exe", new[] { "/c", self.exe }.Concat(self.prefix).Concat(new[] { "--sleep", pidFile2 }).ToList(), timeout: TimeSpan.FromSeconds(3));
        var pid2 = await WaitPid(pidFile2);
        await Task.Delay(300);
        Check.That(timed.TimedOut && pid2 != null && !Alive(pid2.Value), "process: 제한 시간 넘기면 자식까지 끝남");

        // 대화(InteractiveProcess)도 취소하면 자식까지
        var pidFile3 = Path.Combine(root, "child3.pid");
        var interactive = new InteractiveProcess();
        var talk = interactive.Run("cmd.exe", new[] { "/c", self.exe }.Concat(self.prefix).Concat(new[] { "--sleep", pidFile3 }).ToList(), firstLine: "{}\n",
                                   workDir: null, environment: null, timeout: TimeSpan.FromSeconds(60), onLine: (_, _) => Task.CompletedTask);
        var pid3 = await WaitPid(pidFile3);
        interactive.Cancel();
        await talk;
        await Task.Delay(300);
        Check.That(pid3 != null && !Alive(pid3.Value), "process: 대화를 멈추면 claude 가 띄운 자식까지 끝남");

        Check.Remove(root);
    }
}
