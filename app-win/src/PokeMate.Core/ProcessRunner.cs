using System.Diagnostics;
using System.Text;

namespace PokeMate.Core;

/// 하위 프로세스 결과. 출력은 UTF-8 로 읽은 글.
public sealed record ProcessOutput(string Stdout, string Stderr, int ExitCode, bool TimedOut);

/// 하위 프로세스를 실행하고 끝날 때까지 기다린다. 제한 시간을 넘기거나 취소되면 프로세스 트리째 종료시킨다.
/// 인자는 `ArgumentList` 로 넘겨 공백·한글·따옴표가 그대로 전달된다. 입출력은 UTF-8(BOM 없음).
public sealed class ProcessRunner
{
    readonly object gate = new();
    Process? current;

    internal static readonly UTF8Encoding Utf8 = new(false);

    public async Task<ProcessOutput> Run(string executable, IReadOnlyList<string> arguments, string? stdin = null, string? workDir = null,
                                         IDictionary<string, string>? environment = null, TimeSpan? timeout = null)
    {
        using var process = new Process { StartInfo = StartInfo(executable, arguments, workDir, environment) };
        process.Start();   // 실행 파일이 없으면 예외 (맥 Process.run 과 같이)
        lock (gate) current = process;
        var outBuffer = new MemoryStream();
        var errBuffer = new MemoryStream();
        var outPump = Pump(process.StandardOutput.BaseStream, outBuffer);
        var errPump = Pump(process.StandardError.BaseStream, errBuffer);

        var timedOut = false;
        using (var cts = new CancellationTokenSource())
        {
            // 제한 시간은 입력을 쓰기 전에 건다: 자식이 큰 입력(이미지 base64 등)을 읽지 않으면 쓰기가 끝나지 않는다.
            // 쓰기는 따로 돌리고, 시간이 지나 트리를 끝내면 파이프가 끊겨 쓰기도 끝난다.
            if (timeout is TimeSpan t && t != Timeout.InfiniteTimeSpan) cts.CancelAfter(t);
            var writer = WriteInput(process.StandardInput.BaseStream, stdin);
            try { await process.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                timedOut = true;
                KillTree(process);
                await process.WaitForExitAsync();
            }
            await Task.WhenAny(writer, Task.Delay(TimeSpan.FromSeconds(5)));
        }
        await Drain(outPump, errPump);
        lock (gate) current = null;
        return new ProcessOutput(Decode(outBuffer), Decode(errBuffer), process.ExitCode, timedOut);
    }

    public void Cancel()
    {
        Process? p;
        lock (gate) p = current;
        if (p != null) KillTree(p);
    }

    /// 입력을 쓰고 닫는다. 쓰기는 스레드 풀에서 해서 호출한 쪽(화면 스레드 등)을 막지 않는다.
    static Task WriteInput(Stream input, string? stdin) => Task.Run(() =>
    {
        try
        {
            if (stdin != null)
            {
                var bytes = Utf8.GetBytes(stdin);
                input.Write(bytes, 0, bytes.Length);
                input.Flush();
            }
        }
        catch (IOException) { }   // 입력을 읽기 전에 끝난 프로세스
        catch (ObjectDisposedException) { }
        try { input.Close(); } catch { }
    });

    internal static ProcessStartInfo StartInfo(string executable, IReadOnlyList<string> arguments, string? workDir, IDictionary<string, string>? environment)
    {
        var psi = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Utf8,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8,
        };
        foreach (var a in arguments) psi.ArgumentList.Add(a);
        if (workDir != null) psi.WorkingDirectory = workDir;
        if (environment != null)
        {
            psi.Environment.Clear();
            foreach (var kv in environment) psi.Environment[kv.Key] = kv.Value;
        }
        return psi;
    }

    /// 프로세스 트리째 종료 (claude.exe 가 띄운 MCP 서버·셸까지).
    internal static void KillTree(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch { }
    }

    internal static async Task Pump(Stream from, MemoryStream to)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            int n;
            while ((n = await from.ReadAsync(buffer)) > 0)
                lock (to) to.Write(buffer, 0, n);
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    /// 프로세스가 끝난 뒤 출력을 마저 읽는다. 손자 프로세스가 출력 파이프를 쥐고 있으면 오래 기다리지 않는다.
    internal static async Task Drain(params Task[] pumps)
    {
        var all = Task.WhenAll(pumps);
        await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(5)));
    }

    internal static string Decode(MemoryStream buffer)
    {
        lock (buffer) return Utf8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }
}

/// 대화용 하위 프로세스. 출력 줄마다 `onLine` 을 부르고, 그 안에서 `Send` 로 답할 수 있다 (권한 요청 응답).
public sealed class InteractiveProcess
{
    readonly object gate = new();
    Process? current;
    Stream? input;

    public async Task<ProcessOutput> Run(string executable, IReadOnlyList<string> arguments, string firstLine, string? workDir,
                                         IDictionary<string, string>? environment, TimeSpan timeout,
                                         Func<string, InteractiveProcess, Task> onLine)
    {
        using var process = new Process { StartInfo = ProcessRunner.StartInfo(executable, arguments, workDir, environment) };
        process.Start();
        lock (gate)
        {
            current = process;
            input = process.StandardInput.BaseStream;
        }
        var errBuffer = new MemoryStream();
        var errPump = ProcessRunner.Pump(process.StandardError.BaseStream, errBuffer);

        // 감시는 첫 줄을 보내기 전에 건다. 첫 줄(이미지가 든 질문)은 스레드 풀에서 써서 화면 스레드를 막지 않는다.
        var timedOut = false;
        using var watchdogCancel = new CancellationTokenSource();
        var watchdog = Task.Run(async () =>
        {
            try { await Task.Delay(timeout, watchdogCancel.Token); }
            catch (OperationCanceledException) { return; }
            if (!process.HasExited)
            {
                timedOut = true;
                ProcessRunner.KillTree(process);
            }
        });
        Send(firstLine);

        var outData = new StringBuilder();
        var stdout = process.StandardOutput.BaseStream;
        var buffer = new byte[16 * 1024];
        var pending = new List<byte>();
        async Task Emit(List<byte> bytes)
        {
            var count = bytes.Count;
            if (count > 0 && bytes[count - 1] == 0x0D) count--;   // CRLF 줄 끝
            var line = ProcessRunner.Utf8.GetString(bytes.GetRange(0, count).ToArray());
            outData.Append(line).Append('\n');
            await onLine(line, this);
        }
        async Task ReadLines()
        {
            try
            {
                int n;
                while ((n = await stdout.ReadAsync(buffer)) > 0)
                {
                    for (var i = 0; i < n; i++)
                    {
                        if (buffer[i] == 0x0A)
                        {
                            var line = pending;
                            pending = new List<byte>();
                            await Emit(line);
                        }
                        else pending.Add(buffer[i]);
                    }
                }
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
            if (pending.Count > 0) await Emit(pending);
        }
        var reader = ReadLines();
        // 프로세스가 끝났는데 손자 프로세스가 출력 파이프를 쥐고 있으면 오래 기다리지 않는다
        var exitedThenGrace = process.WaitForExitAsync().ContinueWith(_ => Task.Delay(TimeSpan.FromSeconds(5))).Unwrap();
        await Task.WhenAny(reader, exitedThenGrace);

        watchdogCancel.Cancel();
        CloseInput();
        var exited = process.WaitForExitAsync();
        if (await Task.WhenAny(exited, Task.Delay(TimeSpan.FromSeconds(10))) != exited) ProcessRunner.KillTree(process);
        await process.WaitForExitAsync();
        await ProcessRunner.Drain(errPump);
        lock (gate) current = null;
        return new ProcessOutput(outData.ToString(), ProcessRunner.Decode(errBuffer), process.ExitCode, timedOut);
    }

    /// 입력 쓰기는 이 순서대로 스레드 풀에서 한다 (자식이 입력을 읽지 않아도 부르는 쪽은 막히지 않는다).
    Task writes = Task.CompletedTask;

    /// 한 줄을 보낸다. 바로 돌아오고, 쓰기는 앞서 보낸 줄 다음에 이어서 한다.
    public void Send(string line)
    {
        lock (gate)
        {
            var s = input;
            if (s == null) return;
            var bytes = ProcessRunner.Utf8.GetBytes(line);
            writes = writes.ContinueWith(_ =>
            {
                try
                {
                    s.Write(bytes, 0, bytes.Length);
                    s.Flush();
                }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }
    }

    /// 입력을 닫는다. 앞서 보낸 줄을 다 쓴 뒤에 닫고, 그 뒤의 Send 는 무시한다.
    public void CloseInput()
    {
        lock (gate)
        {
            var s = input;
            input = null;
            if (s == null) return;
            writes = writes.ContinueWith(_ => { try { s.Close(); } catch { } }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }
    }

    public void Cancel()
    {
        Process? p;
        lock (gate) p = current;
        if (p != null) ProcessRunner.KillTree(p);
    }
}
