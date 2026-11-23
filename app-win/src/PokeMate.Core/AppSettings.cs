using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace PokeMate.Core;

/// 앱 설정과 경로. `%LOCALAPPDATA%\poke-mate\settings.json` 에 저장한다. 키 이름은 맥과 같다.
[JsonConverter(typeof(AppSettingsConverter))]
public sealed record AppSettings
{
    /// 비서 이름. 화면 제목과 비서의 자기 소개에 쓴다.
    public string AssistantName { get; set; } = DefaultAssistantName;
    /// 대표 포켓몬 이름 (저장하지 않음)
    public string? MateName { get; set; }
    public string RepoDir { get; set; } = DefaultRepoDir;
    public string ClaudePath { get; set; } = DefaultClaudePath;
    public string UvPath { get; set; } = DefaultUvPath;
    public int ReportHour { get; set; } = 10;
    public int ReportMinute { get; set; }
    public string? LastReportDay { get; set; }
    public Hotkey Hotkey { get; set; } = Hotkey.Default;
    /// 주인(사용자) 이름. 비어 있으면 처음 사용으로 보고 채팅창에서 이름을 묻는다.
    public string OwnerName { get; set; } = "";
    /// Claude 모델 별칭 (opus, sonnet, fable). 비우면 계정 기본
    public string Model { get; set; } = "";
    /// 추론 수준 (low, medium, high, xhigh, max). 비우면 기본
    public string Effort { get; set; } = "";
    /// 내 홈의 Claude Code 개인 지침(~/.claude/CLAUDE.md, rules/)도 읽을지. 기본은 끔 (개발용 지침이 비서 답에 섞이지 않게)
    public bool UseHomeInstructions { get; set; }
    public bool HomeInChat { get; set; } = true;
    public bool HomeInAutomation { get; set; } = true;
    public List<string> AutomationRules { get; set; } = DefaultAutomationRules.ToList();
    public bool ShareHomeSkills { get; set; } = true;
    public string AutomationModel { get; set; } = "";
    public string AutomationEffort { get; set; } = "";
    public PokedexRange PokedexRange { get; set; } = PokedexRange.Gen1;
    public bool ClaudeTools { get; set; }
    public int WorkSessionAlertPercent { get; set; } = 70;
    public int SessionReuseMinutes { get; set; } = SessionPolicy.DefaultMinutes;
    public int SessionReuseMaxTokens { get; set; } = SessionPolicy.DefaultTokens;

    public const string DefaultAssistantName = "메이트";
    public static readonly IReadOnlyList<string> DefaultAutomationRules = new[] { "commit-pr.md", "domain-knowledge.md", "production-env.md" };

    /// 설치 스크립트가 설치 폴더에 적어 둔 저장소 위치(`repo-path.txt`)가 있으면 그것, 없으면 `%USERPROFILE%\Workspace\poke-mate-ai-agent`
    public static string DefaultRepoDir => InstalledRepoDir() ?? Path.Combine(Paths.Home, "Workspace", "poke-mate-ai-agent");

    /// 설치 폴더(실행 파일 옆)의 `repo-path.txt` 에 적힌 저장소 폴더. 파일이 없거나 그 폴더가 없으면 null.
    public static string? InstalledRepoDir(string? baseDir = null)
    {
        try
        {
            var file = Path.Combine(baseDir ?? AppContext.BaseDirectory, "repo-path.txt");
            if (!File.Exists(file)) return null;
            var dir = File.ReadAllText(file).Trim();
            return dir.Length > 0 && Directory.Exists(dir) ? dir : null;
        }
        catch { return null; }
    }
    /// `%USERPROFILE%\.local\bin\claude.exe`
    public static string DefaultClaudePath => Path.Combine(Paths.Home, ".local", "bin", "claude.exe");
    /// `%USERPROFILE%\.local\bin\uv.exe` (uv 설치 스크립트의 기본 위치)
    public static string DefaultUvPath => Path.Combine(Paths.Home, ".local", "bin", "uv.exe");

    /// 기본 설정 (부를 때마다 새 사본).
    public static AppSettings Default => new();

    public string OwnerCallName
    {
        get
        {
            var name = OwnerName.Trim();
            if (name.Length == 0) return "";
            return name.EndsWith("님", StringComparison.Ordinal) ? name : name + "님";
        }
    }

    public bool Equals(AppSettings? o) =>
        o is not null && AssistantName == o.AssistantName && MateName == o.MateName && RepoDir == o.RepoDir && ClaudePath == o.ClaudePath
        && UvPath == o.UvPath && ReportHour == o.ReportHour && ReportMinute == o.ReportMinute && LastReportDay == o.LastReportDay
        && Hotkey == o.Hotkey && OwnerName == o.OwnerName && Model == o.Model && Effort == o.Effort
        && UseHomeInstructions == o.UseHomeInstructions && HomeInChat == o.HomeInChat && HomeInAutomation == o.HomeInAutomation
        && AutomationRules.SequenceEqual(o.AutomationRules) && ShareHomeSkills == o.ShareHomeSkills && AutomationModel == o.AutomationModel
        && AutomationEffort == o.AutomationEffort && PokedexRange == o.PokedexRange && ClaudeTools == o.ClaudeTools
        && WorkSessionAlertPercent == o.WorkSessionAlertPercent && SessionReuseMinutes == o.SessionReuseMinutes
        && SessionReuseMaxTokens == o.SessionReuseMaxTokens;

    public override int GetHashCode() => HashCode.Combine(AssistantName, RepoDir, ClaudePath, UvPath, OwnerName, Model);

    /// 맥에서 복사해 온 경로(`/opt/homebrew/bin/uv`, `/Users/…`, `~/Library/…`)만 Windows 기본값으로 바꾼다.
    /// `uv` 같은 PATH 이름과 상대 경로는 그대로 두고, `~/` 는 홈 폴더로 푼다.
    /// 설정의 저장소 폴더가 없으면(저장소를 옮긴 경우) 설치 때 적어 둔 위치로 바꾼다. 그 위치도 없으면 그대로 둔다.
    public static string MovedRepo(string repoDir, string installed) =>
        !Directory.Exists(repoDir) && Directory.Exists(installed) ? installed : repoDir;

    internal static string WindowsPath(string value, string fallback)
    {
        var v = value.Trim();
        if (v.Length == 0) return fallback;
        if (v.StartsWith("/", StringComparison.Ordinal)) return fallback;
        if (v.StartsWith("~/Library", StringComparison.Ordinal) || v.StartsWith(@"~\Library", StringComparison.Ordinal)) return fallback;
        if (v.StartsWith("~", StringComparison.Ordinal)) v = Paths.ExpandTilde(v);
        return v;
    }
}

/// 맥 `AppSettings.init(from:)`·`encode(to:)` 와 같은 읽기·쓰기. 예전 파일에 없는 항목은 기본값으로 채운다.
public sealed class AppSettingsConverter : JsonConverter<AppSettings>
{
    public override AppSettings Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var node = JsonNode.Parse(ref reader) as JsonObject ?? throw new JsonException("설정은 JSON 객체여야 해요");
        var d = AppSettings.Default;

        // decodeIfPresent: 없거나 null 이면 기본값, 형식이 틀리면 실패
        string? Str(string key) => node[key] is null ? null : Json.Str(node[key]) ?? throw new JsonException(key);
        int? Int(string key) => node[key] is null ? null : node[key] is JsonValue v && v.TryGetValue<int>(out var i) ? i : throw new JsonException(key);
        bool? Bool(string key) => node[key] is null ? null : Json.Bool(node[key]) ?? throw new JsonException(key);

        var s = new AppSettings
        {
            AssistantName = Str("assistantName") ?? d.AssistantName,
            // 저장소를 옮겨 예전 폴더가 사라졌으면 설치 때 적어 둔 위치를 쓴다
            RepoDir = AppSettings.MovedRepo(AppSettings.WindowsPath(Str("repoDir") ?? d.RepoDir, d.RepoDir), d.RepoDir),
            ClaudePath = AppSettings.WindowsPath(Str("claudePath") ?? d.ClaudePath, d.ClaudePath),
            UvPath = AppSettings.WindowsPath(Str("uvPath") ?? d.UvPath, d.UvPath),
            ReportHour = Int("reportHour") ?? d.ReportHour,
            ReportMinute = Int("reportMinute") ?? d.ReportMinute,
            LastReportDay = Str("lastReportDay"),
            OwnerName = Str("ownerName") ?? "",
            Model = Str("model") ?? "",
            Effort = Str("effort") ?? "",
            UseHomeInstructions = Bool("useHomeInstructions") ?? false,
        };
        if (node["hotkey"] is JsonNode hk)
        {
            var hotkey = hk.Deserialize<Hotkey>(options) ?? throw new JsonException("hotkey");
            // 맥 파일의 Carbon 키 값은 Windows 에서 뜻이 없다 → 기본 단축키
            s.Hotkey = hotkey.LooksLikeMac ? Hotkey.Default : hotkey;
        }
        s.HomeInChat = Bool("homeInChat") ?? s.UseHomeInstructions;
        s.HomeInAutomation = Bool("homeInAutomation") ?? s.UseHomeInstructions;
        s.AutomationRules = node["automationRules"] is null ? AppSettings.DefaultAutomationRules.ToList()
            : Json.Strings(node["automationRules"]) ?? throw new JsonException("automationRules");
        s.ShareHomeSkills = Bool("shareHomeSkills") ?? true;
        s.AutomationModel = Str("automationModel") ?? s.Model;
        s.AutomationEffort = Str("automationEffort") ?? s.Effort;
        s.PokedexRange = Json.Str(node["pokedexRange"]) switch { "all" => PokedexRange.All, _ => PokedexRange.Gen1 };   // try? → gen1
        s.ClaudeTools = Bool("claudeTools") ?? false;
        s.WorkSessionAlertPercent = Int("workSessionAlertPercent") ?? 70;
        s.SessionReuseMinutes = Math.Max(0, Int("sessionReuseMinutes") ?? SessionPolicy.DefaultMinutes);
        s.SessionReuseMaxTokens = Math.Max(0, Int("sessionReuseMaxTokens") ?? SessionPolicy.DefaultTokens);
        return s;
    }

    public override void Write(Utf8JsonWriter writer, AppSettings s, JsonSerializerOptions options)
    {
        // Swift 합성 encode: CodingKeys 의 키만(mateName 은 빼고), nil 은 쓰지 않음
        writer.WriteStartObject();
        writer.WriteString("assistantName", s.AssistantName);
        writer.WriteString("repoDir", s.RepoDir);
        writer.WriteString("claudePath", s.ClaudePath);
        writer.WriteString("uvPath", s.UvPath);
        writer.WriteNumber("reportHour", s.ReportHour);
        writer.WriteNumber("reportMinute", s.ReportMinute);
        if (s.LastReportDay != null) writer.WriteString("lastReportDay", s.LastReportDay);
        writer.WritePropertyName("hotkey");
        JsonSerializer.Serialize(writer, s.Hotkey, options);
        writer.WriteString("ownerName", s.OwnerName);
        writer.WriteString("model", s.Model);
        writer.WriteString("effort", s.Effort);
        writer.WriteBoolean("useHomeInstructions", s.UseHomeInstructions);
        writer.WriteString("pokedexRange", s.PokedexRange == PokedexRange.All ? "all" : "gen1");
        writer.WriteBoolean("claudeTools", s.ClaudeTools);
        writer.WriteNumber("workSessionAlertPercent", s.WorkSessionAlertPercent);
        writer.WriteBoolean("homeInChat", s.HomeInChat);
        writer.WriteBoolean("homeInAutomation", s.HomeInAutomation);
        writer.WriteStartArray("automationRules");
        foreach (var r in s.AutomationRules) writer.WriteStringValue(r);
        writer.WriteEndArray();
        writer.WriteBoolean("shareHomeSkills", s.ShareHomeSkills);
        writer.WriteString("automationModel", s.AutomationModel);
        writer.WriteString("automationEffort", s.AutomationEffort);
        writer.WriteNumber("sessionReuseMinutes", s.SessionReuseMinutes);
        writer.WriteNumber("sessionReuseMaxTokens", s.SessionReuseMaxTokens);
        writer.WriteEndObject();
    }
}

public static class AppPaths
{
    /// `%LOCALAPPDATA%\poke-mate`. 환경 변수 `POKEMATE_DATA_DIR` 가 있으면 그 폴더 (파이썬 `credentials.data_dir()` 과 같은 규칙).
    public static string DataDir
    {
        get
        {
            var overridden = Environment.GetEnvironmentVariable("POKEMATE_DATA_DIR");
            if (!string.IsNullOrWhiteSpace(overridden)) return Paths.ExpandTilde(overridden.Trim());
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "poke-mate");
        }
    }

    public static string WorkDir => Path.Combine(DataDir, "work");
    /// 메이트 전용 Claude 로그인 정보가 들어가는 폴더. 터미널 Claude Code 로그인과 따로 쓴다.
    public static string ClaudeConfigDir => Path.Combine(DataDir, "claude-config");
    public static string McpConfig => Path.Combine(DataDir, "mcp-config.json");
    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string ChatFile => Path.Combine(DataDir, "chat.json");

    public static string RepoDir => LoadSettings().RepoDir;
    public static string SharedDir => Path.Combine(RepoDir, "shared");
    public static string LocalDir => Path.Combine(RepoDir, "local");

    public static bool IsDirectory(string path) => Directory.Exists(path);

    public static bool LocalReady => IsDirectory(LocalDir);

    public static string LocalFile(string name) => Path.Combine(LocalDir, name);

    public static bool MakeLocalFromExample(string? repo = null)
    {
        repo ??= RepoDir;
        var local = Path.Combine(repo, "local");
        var example = Path.Combine(repo, "local.example");
        if (IsDirectory(local) || !IsDirectory(example)) return false;
        try { CopyDirectory(example, local); return true; }
        catch { return false; }
    }

    static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from)) File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        foreach (var dir in Directory.GetDirectories(from)) CopyDirectory(dir, Path.Combine(to, Path.GetFileName(dir)));
    }

    public static string SharedFile(string name, AppSettings settings) =>
        Path.Combine(settings.RepoDir, "shared", name.Replace('/', Path.DirectorySeparatorChar));

    /// 데이터 폴더를 만들고, 템플릿으로 실제 MCP 설정 파일을 다시 쓴다.
    public static void Prepare(AppSettings settings)
    {
        Directory.CreateDirectory(WorkDir);
        ClaudeCommand.InstallPermissions();
        HomeSkills.Sync(enabled: settings.ShareHomeSkills);
        var template = File.ReadAllText(SharedFile("mcp-config.template.json", settings));
        // 템플릿 자리는 JSON 문자열 안이다. Windows 경로의 역슬래시를 JSON 이스케이프한다.
        var config = template
            .Replace("{{UV_PATH}}", JsonText(settings.UvPath))
            .Replace("{{MCP_SERVER_DIR}}", JsonText(Path.Combine(settings.RepoDir, "mcp-server")));
        WriteAtomic(McpConfig, config);
    }

    static string JsonText(string value)
    {
        var quoted = JsonSerializer.Serialize(value, Json.Compact);
        return quoted[1..^1];
    }

    public static AppSettings LoadSettings()
    {
        try { return Json.Deserialize<AppSettings>(File.ReadAllText(SettingsFile)) ?? AppSettings.Default; }
        catch { return AppSettings.Default; }
    }

    /// 설정을 저장한다. 실패하면 false (다른 프로세스가 파일을 붙잡고 있는 경우 등). 임시 파일은 남기지 않는다.
    public static bool SaveSettings(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            WriteAtomic(SettingsFile, Json.SortedPretty(settings));
            return true;
        }
        catch { return false; }
    }

    /// 임시 파일에 쓰고 바꿔 넣는다 (Swift `.atomic`). 한글은 UTF-8, BOM 없음.
    /// Windows 는 다른 프로세스(파이썬 서버)가 대상 파일을 열어 둔 동안 바꿔 넣기가 실패하므로 잠깐씩 쉬며 다시 시도한다.
    /// 끝내 실패하면 임시 파일을 지우고 예외를 던진다.
    public static void WriteAtomic(string path, string text, int attempts = 5, int delayMs = 50)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temp, text, new System.Text.UTF8Encoding(false));
            for (var i = 1; ; i++)
            {
                try { File.Move(temp, path, overwrite: true); return; }
                catch (Exception e) when ((e is IOException or UnauthorizedAccessException) && i < attempts) { Thread.Sleep(delayMs); }
            }
        }
        catch
        {
            try { File.Delete(temp); } catch { }
            throw;
        }
    }

    /// 하위 프로세스 환경. PATH 는 `;` 로 잇고 uv·claude 폴더, Git 의 cmd·bin 폴더를 앞에 더한다. 메이트 전용 로그인 폴더를 쓴다.
    public static Dictionary<string, string> ProcessEnvironment(AppSettings settings)
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
            env[(string)e.Key] = e.Value as string ?? "";
        var extra = new List<string?>
        {
            Path.GetDirectoryName(settings.UvPath),
            Path.GetDirectoryName(settings.ClaudePath),
        };
        foreach (var git in GitDirs()) extra.Add(git);
        env.TryGetValue("PATH", out var current);
        var parts = extra.Where(p => !string.IsNullOrEmpty(p)).Select(p => p!).ToList();
        if (!string.IsNullOrEmpty(current)) parts.Add(current);
        env["PATH"] = string.Join(";", parts);
        // 메이트는 항상 전용 로그인을 쓴다 (터미널 Claude Code 와 다른 계정·조직 가능)
        try { Directory.CreateDirectory(ClaudeConfigDir); } catch { }
        env["CLAUDE_CONFIG_DIR"] = ClaudeConfigDir;
        return env;
    }

    /// Git for Windows 의 cmd·bin 폴더 (있는 것만)
    static IEnumerable<string> GitDirs()
    {
        var roots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Git"),
        };
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            yield return Path.Combine(root, "cmd");
            yield return Path.Combine(root, "bin");
            yield break;
        }
    }
}

public static class HomeSkills
{
    public static string Source(string? home = null) => Path.Combine(home ?? Paths.Home, ".claude", "skills");

    public static string Target => Path.Combine(AppPaths.WorkDir, ".claude", "skills");

    public static List<string> Names(string? home = null)
    {
        var dir = Source(home);
        if (!Directory.Exists(dir)) return new List<string>();
        return Directory.EnumerateFileSystemEntries(dir).Select(Path.GetFileName).Where(n => n != null).Select(n => n!)
            .Where(name => !name.StartsWith(".", StringComparison.Ordinal) && File.Exists(Path.Combine(dir, name, "SKILL.md")))
            .OrderBy(n => n, StringComparer.Ordinal).ToList();
    }

    static string? LinkDestination(string path)
    {
        try { return new DirectoryInfo(path).LinkTarget ?? new FileInfo(path).LinkTarget; }
        catch { return null; }
    }

    static bool Exists(string path)
    {
        try { return File.GetAttributes(path) != (FileAttributes)(-1); } catch { return false; }
    }

    /// 홈의 스킬 폴더를 메이트 작업 폴더에 링크한다. 심볼릭 링크를 만들 권한이 없으면 디렉터리 정션(관리자 권한 불필요).
    public static (List<string> Linked, List<string> Removed) Sync(bool enabled, string? home = null, string? target = null)
    {
        target ??= Target;
        var source = Source(home);
        var basePath = source + Path.DirectorySeparatorChar;
        var wanted = enabled ? Names(home) : new List<string>();
        var linked = new List<string>();
        var removed = new List<string>();
        try { Directory.CreateDirectory(target); } catch { }
        var existing = Directory.Exists(target) ? Directory.EnumerateFileSystemEntries(target).ToList() : new List<string>();
        foreach (var path in existing)
        {
            var name = Path.GetFileName(path);
            var dest = LinkDestination(path)?.TrimEnd('\\', '/');
            if (dest == null || !dest.StartsWith(basePath, StringComparison.OrdinalIgnoreCase)) continue;
            if (!wanted.Contains(name) || !string.Equals(dest, basePath + name, StringComparison.OrdinalIgnoreCase))
            {
                try { Directory.Delete(path); removed.Add(name); }   // 링크 자체만 지운다 (원본은 그대로)
                catch { try { File.Delete(path); removed.Add(name); } catch { } }
            }
        }
        foreach (var name in wanted)
        {
            var path = Path.Combine(target, name);
            if (Exists(path)) continue;
            if (Link(path, basePath + name)) linked.Add(name);
        }
        return (linked, removed);
    }

    static bool Link(string path, string destination)
    {
        try
        {
            Directory.CreateSymbolicLink(path, destination);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // 개발자 모드가 아니면 심볼릭 링크는 권한이 없다 → 정션
            try
            {
                var psi = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var a in new[] { "/c", "mklink", "/J", path, destination }) psi.ArgumentList.Add(a);
                using var p = Process.Start(psi);
                if (p == null) return false;
                p.StandardOutput.ReadToEnd();
                p.StandardError.ReadToEnd();
                p.WaitForExit(10_000);
                return p.HasExited && p.ExitCode == 0 && Directory.Exists(path);
            }
            catch { return false; }
        }
    }
}
