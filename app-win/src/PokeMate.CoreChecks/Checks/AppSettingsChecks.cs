using PokeMate.Core;

namespace PokeMate.CoreChecks;

// MARK: AppSettings
public static class AppSettingsChecks
{
    public static void Run()
    {
        const string old = """{"repoDir":"C:\\r","claudePath":"C:\\c\\claude.exe","uvPath":"C:\\u\\uv.exe","reportHour":10,"reportMinute":0,"lastReportDay":"2026-10-21"}""";
        var s = Json.TryDeserialize<AppSettings>(old);
        Check.That(s?.AssistantName == "메이트" && s.RepoDir == @"C:\r" && s.LastReportDay == "2026-10-21", "settings: 예전 파일도 읽음 (이름 기본값)");
        var stored = Json.TryDeserialize<AppSettings>("""{"assistantName":"옛 이름"}""");
        Check.That(stored?.AssistantName == "옛 이름" && stored.MateName == null, "settings: 저장된 이름 읽음");
        var named = AppSettings.Default;
        named.AssistantName = "옛 이름";
        named.MateName = "뿔카노";
        var saved = Json.Serialize(named);
        Check.That(saved.Contains("옛 이름") && !saved.Contains("뿔카노"), "settings: 이름은 저장, 대표 포켓몬 이름은 저장 안 함");
        Check.That(ClaudeRunner.NameLine(named).Contains("대표 포켓몬 \"뿔카노\""), "name: 부화한 뒤엔 대표 포켓몬 이름");
        named.MateName = null;
        Check.That(ClaudeRunner.NameLine(named).Contains("너의 이름은 \"옛 이름\"") && !ClaudeRunner.NameLine(named).Contains("포켓몬"), "name: 알 단계엔 설정 이름");
        Check.That(ClaudeRunner.NameLine(AppSettings.Default).Length == 0, "name: 기본 이름이면 덧붙이지 않음");
        var owner = AppSettings.Default;
        var calls = new[] { "홍길동", "트레이너", "주인님", " 길동 ", "" }.Select(name => { owner.OwnerName = name; return owner.OwnerCallName; }).ToList();
        Check.That(calls.SequenceEqual(new[] { "홍길동님", "트레이너님", "주인님", "길동님", "" }), "owner: 호칭 끝에 님 하나만 (이미 님이면 그대로)");
        owner.OwnerName = "트레이너";
        Check.That(ClaudeRunner.OwnerLine(owner).Contains("\"트레이너님\"이라고 부른다") && ClaudeRunner.OwnerLine(AppSettings.Default).Length == 0, "owner: 프롬프트에 부를 말 그대로");

        // Windows 기본값
        var d = AppSettings.Default;
        var home = Paths.Home;
        Check.That(d.ClaudePath == Path.Combine(home, ".local", "bin", "claude.exe") && d.UvPath == Path.Combine(home, ".local", "bin", "uv.exe")
                   && d.RepoDir == Path.Combine(home, "Workspace", "poke-mate-ai-agent") && d.Hotkey == Hotkey.Default, "settings: Windows 기본 경로");

        // Review Focus 4: 맥에서 복사해 온 settings.json 의 경로는 Windows 기본값으로 고쳐 읽는다
        const string mac = """{"repoDir":"/Users/kim/Workspace/poke-mate-ai-agent","claudePath":"/Users/kim/.local/bin/claude","uvPath":"/opt/homebrew/bin/uv","reportHour":9,"reportMinute":30,"hotkey":{"keyCode":49,"modifiers":6912},"ownerName":"김","pokedexRange":"all","sessionReuseMinutes":-5}""";
        var fromMac = Json.TryDeserialize<AppSettings>(mac);
        Check.That(fromMac?.UvPath == d.UvPath && fromMac.ClaudePath == d.ClaudePath && fromMac.RepoDir == d.RepoDir,
                   "settings: 맥 경로(/opt/homebrew/bin/uv …)는 Windows 기본 경로로");
        Check.That(fromMac?.ReportHour == 9 && fromMac.ReportMinute == 30 && fromMac.OwnerName == "김" && fromMac.PokedexRange == PokedexRange.All && fromMac.SessionReuseMinutes == 0,
                   "settings: 맥 파일의 다른 값은 그대로 (음수 분은 0)");
        Check.That(fromMac?.Hotkey == Hotkey.Default, "settings: 맥 단축키 값(Carbon)은 Windows 기본 단축키로");
        var tilde = Json.TryDeserialize<AppSettings>("""{"repoDir":"~/Workspace/x"}""");
        Check.That(tilde?.RepoDir == Path.Combine(home, "Workspace", "x"), "settings: ~/ 경로는 홈 폴더로");
        // 사용자가 직접 정한 값은 지키고, 맥 경로만 고친다
        var custom = Json.TryDeserialize<AppSettings>("""{"uvPath":"uv","claudePath":"claude","repoDir":"work/repo"}""");
        Check.That(custom?.UvPath == "uv" && custom.ClaudePath == "claude" && custom.RepoDir == "work/repo", "settings: PATH 이름·상대 경로는 그대로 둠");
        var macOnly = Json.TryDeserialize<AppSettings>("""{"uvPath":"/opt/homebrew/bin/uv","claudePath":"~/Library/x/claude","repoDir":"/Users/kim/r"}""");
        Check.That(macOnly?.UvPath == d.UvPath && macOnly.ClaudePath == d.ClaudePath && macOnly.RepoDir == d.RepoDir, "settings: /opt/homebrew, ~/Library, /Users 는 기본 경로로");
        Check.That(Json.TryDeserialize<AppSettings>("""{"uvPath":"~/x/uv.exe"}""")?.UvPath == Path.Combine(home, "x", "uv.exe"), "settings: ~/x 는 홈 폴더로");
        Check.That(Json.TryDeserialize<AppSettings>("""{"pokedexRange":"gen9"}""")?.PokedexRange == PokedexRange.Gen1, "settings: 모르는 도감 범위는 1세대");
        Check.That(Json.TryDeserialize<AppSettings>("""{"reportHour":"ten"}""") == null, "settings: 형식이 틀린 값은 읽지 않음 (기본 설정으로)");

        // 저장 → 다시 읽기 (키 정렬, 데이터 폴더는 POKEMATE_DATA_DIR 임시 폴더)
        var round = AppSettings.Default;
        round.OwnerName = "트레이너";
        round.AutomationRules = new List<string> { "a.md" };
        round.Hotkey = new Hotkey(0x4B, Hotkey.Control | Hotkey.Alt);
        AppPaths.SaveSettings(round);
        var text = File.ReadAllText(AppPaths.SettingsFile);
        var loaded = AppPaths.LoadSettings();
        Check.That(loaded == round && text.IndexOf("\"assistantName\"", StringComparison.Ordinal) < text.IndexOf("\"automationEffort\"", StringComparison.Ordinal)
                   && text.IndexOf("\"workSessionAlertPercent\"", StringComparison.Ordinal) > text.IndexOf("\"uvPath\"", StringComparison.Ordinal)
                   && text.Contains("\"pokedexRange\": \"gen1\"") && !text.Contains("mateName"),
                   "settings: 저장하고 다시 읽음 (키 정렬, rawValue 그대로)");
        File.WriteAllText(AppPaths.SettingsFile, "{ 깨진");
        Check.That(AppPaths.LoadSettings() == AppSettings.Default, "settings: 깨진 파일이면 기본 설정");
        File.Delete(AppPaths.SettingsFile);

        // 다른 프로세스(파이썬 서버)가 파일을 열어 둔 동안의 저장: 풀리면 저장됨, 안 풀리면 실패를 알리고 임시 파일을 남기지 않음
        var lockRound = AppSettings.Default;
        lockRound.OwnerName = "잠금 시험";
        File.WriteAllText(AppPaths.SettingsFile, "{}");
        var held = new FileStream(AppPaths.SettingsFile, FileMode.Open, FileAccess.Read, FileShare.Read);
        var releaser = Task.Run(async () => { await Task.Delay(120); held.Dispose(); });
        var okAfterRelease = AppPaths.SaveSettings(lockRound);
        releaser.Wait();
        Check.That(okAfterRelease && AppPaths.LoadSettings().OwnerName == "잠금 시험", "settings: 파일이 잠깐 열려 있어도 다시 시도해 저장함");
        held = new FileStream(AppPaths.SettingsFile, FileMode.Open, FileAccess.Read, FileShare.Read);
        var failed = AppPaths.SaveSettings(AppSettings.Default);
        held.Dispose();
        Check.That(!failed && AppPaths.LoadSettings().OwnerName == "잠금 시험", "settings: 끝내 못 바꾸면 실패를 알리고 이전 내용 유지");
        Check.That(!Directory.GetFiles(AppPaths.DataDir, "*.tmp-*").Any(), "settings: 실패해도 .tmp- 파일이 남지 않음");
        File.Delete(AppPaths.SettingsFile);

        // 하위 프로세스 환경
        var env = AppPaths.ProcessEnvironment(round);
        var path = env["PATH"].Split(';');
        Check.That(path[0] == Path.GetDirectoryName(round.UvPath) && path[1] == Path.GetDirectoryName(round.ClaudePath) && !env["PATH"].Contains("/opt/homebrew")
                   && env.ContainsKey("Path") && env["CLAUDE_CONFIG_DIR"] == AppPaths.ClaudeConfigDir && Directory.Exists(AppPaths.ClaudeConfigDir),
                   "settings: PATH 는 ; 로, uv·claude 폴더 먼저, 전용 로그인 폴더");
        Check.That(new SessionPolicy(round).IdleMinutes == SessionPolicy.DefaultMinutes && SessionPolicy.Always.MaxTokens == 0, "settings: 세션 재사용 기본값");
    }
}
