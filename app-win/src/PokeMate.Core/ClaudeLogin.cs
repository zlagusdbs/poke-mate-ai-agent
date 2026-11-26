using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace PokeMate.Core;

/// `claude auth login` 을 대신 실행한다. 브라우저를 열고, 브라우저에 나온 코드를 받아 표준 입력으로 넘긴다.
/// 코드는 화면·파일에 남기지 않는다.
public sealed class ClaudeLogin
{
    public abstract record Event
    {
        /// 브라우저를 띄웠다
        public sealed record BrowserOpened : Event;
        /// 브라우저에 나온 코드를 붙여넣어야 한다. Url 은 claude 가 알려 준 로그인 주소 (브라우저가 안 떴을 때 직접 연다, 못 찾으면 null).
        public sealed record NeedsCode(string? Url) : Event;
        /// 로그인 성공 여부
        public sealed record Finished(bool Ok) : Event;
    }

    readonly object gate = new();
    Process? process;
    Stream? input;
    string buffer = "";
    bool askedCode;
    bool cancelled;

    public static async Task<bool> IsLoggedIn(AppSettings settings) => (await Account(settings))?.LoggedIn ?? false;

    /// 메이트가 지금 쓰는 Claude 계정 (`claude auth status`). claude 를 못 찾으면 null.
    public static async Task<ClaudeAccount?> Account(AppSettings settings)
    {
        if (!File.Exists(settings.ClaudePath)) return null;
        try
        {
            var output = await new ProcessRunner().Run(settings.ClaudePath, new[] { "auth", "status" },
                                                       environment: AppPaths.ProcessEnvironment(settings), timeout: TimeSpan.FromSeconds(20));
            return ClaudeAccount.Parse(output.Stdout);
        }
        catch { return null; }
    }

    /// 메이트 전용 로그인에서 로그아웃 (터미널 Claude Code 로그인은 그대로).
    public static async Task Logout(AppSettings settings)
    {
        try
        {
            await new ProcessRunner().Run(settings.ClaudePath, new[] { "auth", "logout" },
                                          environment: AppPaths.ProcessEnvironment(settings), timeout: TimeSpan.FromSeconds(20));
        }
        catch { }
    }

    /// 로그인 시작. onEvent 는 Start 를 부른 스레드의 SynchronizationContext(화면 스레드)에서 불린다.
    public void Start(AppSettings settings, Action<Event> onEvent)
    {
        Cancel();
        var context = SynchronizationContext.Current;
        void Post(Event e)
        {
            if (context != null) context.Post(_ => onEvent(e), null);
            else onEvent(e);
        }
        Process p;
        lock (gate)
        {
            cancelled = false;
            buffer = "";
            askedCode = false;
            p = new Process { StartInfo = ProcessRunner.StartInfo(settings.ClaudePath, new[] { "auth", "login" }, null, AppPaths.ProcessEnvironment(settings)), EnableRaisingEvents = true };
        }
        try { p.Start(); }
        catch
        {
            onEvent(new Event.Finished(false));
            return;
        }
        lock (gate)
        {
            process = p;
            input = p.StandardInput.BaseStream;
        }
        void OnChunk(string chunk)
        {
            bool opened, needs = false;
            string? url = null;
            lock (gate)
            {
                buffer += chunk;
                opened = buffer.Contains("Opening browser") || buffer.Contains("visit:");
                if (!askedCode && buffer.Contains("paste code", StringComparison.OrdinalIgnoreCase))
                {
                    askedCode = true;
                    needs = true;
                    url = SignInUrl(buffer);
                }
            }
            if (opened) Post(new Event.BrowserOpened());
            if (needs) Post(new Event.NeedsCode(url));
        }
        var outRead = ReadChunks(p.StandardOutput, OnChunk);
        var errRead = ReadChunks(p.StandardError, OnChunk);
        _ = Task.Run(async () =>
        {
            await p.WaitForExitAsync();
            await Task.WhenAny(Task.WhenAll(outRead, errRead), Task.Delay(2000));
            bool wasCancelled;
            lock (gate)
            {
                input = null;
                wasCancelled = cancelled || !ReferenceEquals(process, p);
            }
            if (wasCancelled) return;
            var ok = await IsLoggedIn(settings);
            Post(new Event.Finished(ok));
        });
    }

    static readonly Regex Escapes = new(@"\x1B\][^\x07\x1B]*(?:\x07|\x1B\\)|\x1B\[[0-9;?]*[A-Za-z]");
    static readonly Regex Url = new(@"https://[^\s\x07\x1B]+");

    /// `claude auth login` 출력에서 로그인 주소 ("If the browser didn't open, visit:" 또는 "Sign in at:" 아래 줄).
    /// 터미널 링크·색 제어 문자는 지우고 첫 https 주소를 쓴다. 없으면 null.
    public static string? SignInUrl(string output)
    {
        var plain = Escapes.Replace(output, "");
        var at = plain.IndexOf("visit:", StringComparison.Ordinal);
        if (at < 0) at = plain.IndexOf("Sign in at:", StringComparison.Ordinal);
        var match = Url.Match(plain, Math.Max(0, at));
        return match.Success ? match.Value : null;
    }

    internal static async Task ReadChunks(StreamReader reader, Action<string> onChunk)
    {
        var chars = new char[4096];
        try
        {
            int n;
            while ((n = await reader.ReadAsync(chars, 0, chars.Length)) > 0) onChunk(new string(chars, 0, n));
        }
        catch { }
    }

    public void Submit(string code)
    {
        var trimmed = code.Trim();
        Stream? s;
        lock (gate) s = input;
        if (trimmed.Length == 0 || s == null) return;
        try
        {
            var bytes = Encoding.UTF8.GetBytes(trimmed + "\n");
            s.Write(bytes, 0, bytes.Length);
            s.Flush();
        }
        catch { }
    }

    public void Cancel()
    {
        Process? p;
        lock (gate)
        {
            cancelled = true;
            p = process;
            process = null;
            input = null;
        }
        if (p != null) ProcessRunner.KillTree(p);
    }
}

/// `claude auth status` 결과.
public sealed record ClaudeAccount(bool LoggedIn, string Email, string OrgName, string Plan)
{
    // Plan: enterprise, team, max, pro …

    public static ClaudeAccount? Parse(string json)
    {
        var obj = Json.ParseObject(json);
        if (obj == null) return null;
        return new ClaudeAccount(Json.Bool(obj["loggedIn"]) ?? false, Json.Str(obj["email"]) ?? "",
                                 Json.Str(obj["orgName"]) ?? "", Json.Str(obj["subscriptionType"]) ?? "");
    }

    /// 예: "kim@example.com · Example Inc. (enterprise)"
    public string Display
    {
        get
        {
            if (!LoggedIn) return "로그인 안 됨";
            var org = OrgName.Length == 0 ? "" : $" · {OrgName}";
            return Email + org + (Plan.Length == 0 ? "" : $" ({Plan})");
        }
    }
}
