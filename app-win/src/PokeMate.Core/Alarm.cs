using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace PokeMate.Core;

/// 사용자가 정한 시각에 메이트에게 시킬 일. `alarms.json` 의 한 항목 (키 이름·반복 규칙 형식은 맥과 같고 파이썬 서버도 읽는다).
/// record 는 참조 형식이다. Swift struct 처럼 복사해서 바꾸려면 `alarm with { Enabled = false }`.
public sealed record Alarm
{
    /// 반복 규칙. JSON 은 Swift 합성 형식: `{"daily":{}}`, `{"weekdays":{}}`, `{"days":{"_0":[2,4]}}`, `{"once":{"_0":"2026-10-26"}}`
    [JsonConverter(typeof(RepeatConverter))]
    public abstract record Repeat
    {
        public sealed record Daily : Repeat;
        /// 월~금
        public sealed record Weekdays : Repeat;
        /// 요일 목록 (Calendar weekday: 1=일 … 7=토)
        public sealed record Days(IReadOnlyList<int> Values) : Repeat
        {
            public bool Equals(Days? o) => o is not null && Values.SequenceEqual(o.Values);
            public override int GetHashCode() => Values.Count == 0 ? 0 : HashCode.Combine(Values.Count, Values[0]);
        }
        /// yyyy-MM-dd
        public sealed record Once(string Day) : Repeat;

        public string Description => this switch
        {
            Daily => "매일",
            Weekdays => "평일",
            Days d => string.Join("·", d.Values.OrderBy(v => v).Select(v => new[] { "", "일", "월", "화", "수", "목", "금", "토" }[v])),
            Once o => o.Day,
            _ => "",
        };
    }

    /// prompt 가 이 값이면 shared/app/daily-report.md 내용을 쓴다.
    public const string DailyReportPrompt = "{{daily-report}}";

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "";
    public string Prompt { get; set; } = "";
    public int Hour { get; set; }
    public int Minute { get; set; }
    public Repeat RepeatRule { get; set; } = new Repeat.Daily();
    public bool Enabled { get; set; } = true;
    public string? LastRunDay { get; set; }
    /// 있으면 시각 대신 이 이벤트가 생길 때 울린다 (Hour·Minute·RepeatRule 은 쓰지 않음).
    public Trigger? Trigger { get; set; }
    /// 알림 그룹 이름. 없으면 "그룹 없음".
    public string? Group { get; set; }
    /// 받을 곳. 없으면 켜짐 (예전 알림 파일 호환). 맥의 "Mac 알림" 자리는 Windows 에서 트레이 풍선 알림.
    public bool? NotifyMac { get; set; }
    public bool? NotifyPanel { get; set; }
    public bool? AllowWrite { get; set; }
    public string? Command { get; set; }
    public int? TimeoutMinutes { get; set; }
    public bool? UseWorkSessions { get; set; }
    public bool? UseRolePipeline { get; set; }
    public bool? UseDomainReviewer { get; set; }

    [JsonIgnore] public bool WorkSessions => Trigger != null && CanWrite && (UseWorkSessions ?? false);
    [JsonIgnore] public bool RolePipeline => WorkSessions && (UseRolePipeline ?? false);
    [JsonIgnore] public bool ReviewsPullRequests => Trigger?.Event == "github.review_requested";
    [JsonIgnore] public bool DomainReviewer => WorkSessions && ReviewsPullRequests && (UseDomainReviewer ?? true);

    public static readonly IReadOnlyList<int> TimeoutChoices = new[] { 30, 60, 90, 120 };

    [JsonIgnore]
    public TimeSpan AutomationTimeout =>
        TimeSpan.FromMinutes(Math.Max(1, TimeoutMinutes ?? (int)(ClaudeRunner.AutomationTimeout.TotalSeconds / 60)));

    static readonly Regex UvPrefix = new(@"^(?:&\s*)?""?(?:[^\s""]*[/\\])?uv(?:\.exe)?""?\s+--directory\s+(?:""[^""]*""|'[^']*'|\S+)\s+run\s+(?:--quiet\s+)?(.+)$");

    /// 목록에 보일 명령. `uv --directory … run X` 의 앞부분은 빼고 실제 명령만, 아니면 홈 폴더를 `~` 로. 길면 자른다.
    public static string DisplayCommand(string command, string? home = null, int limit = 40)
    {
        home ??= Paths.Home;
        var trimmed = command.Trim();
        var shown = trimmed;
        var match = UvPrefix.Match(trimmed);
        if (match.Success) shown = match.Groups[1].Value;
        else if (home.Length > 0)
        {
            shown = trimmed.Replace(home, "~", StringComparison.Ordinal);
            var slashes = Paths.ForwardSlashes(home);
            if (slashes != home) shown = shown.Replace(slashes, "~", StringComparison.Ordinal);
        }
        return SwiftText.Count(shown) > limit ? SwiftText.Prefix(shown, limit) + "…" : shown;
    }

    /// 실행할 셸 명령 (트리거 알람에서는 없음)
    [JsonIgnore]
    public string? ShellCommand
    {
        get
        {
            if (Trigger != null) return null;
            var c = Command?.Trim();
            return string.IsNullOrEmpty(c) ? null : c;
        }
    }

    /// 알림(맥의 Mac 알림, Windows 에서는 트레이 풍선 알림)으로 받을지
    [JsonIgnore] public bool ToMac => NotifyMac ?? true;
    /// 미리 알림 창(화면 오른쪽 아래 카드)으로 받을지
    [JsonIgnore] public bool ToPanel => NotifyPanel ?? true;
    [JsonIgnore] public bool CanWrite => AllowWrite ?? false;

    public Alarm() { }

    public Alarm(string title, string prompt, int hour, int minute, Repeat repeatRule, bool enabled = true, string? lastRunDay = null,
                 Trigger? trigger = null, string? group = null, Guid? id = null)
    {
        Id = id ?? Guid.NewGuid();
        Title = title;
        Prompt = prompt;
        Hour = hour;
        Minute = minute;
        RepeatRule = repeatRule;
        Enabled = enabled;
        LastRunDay = lastRunDay;
        Trigger = trigger;
        Group = group;
    }

    [JsonIgnore] public string TimeText => $"{Hour:00}:{Minute:00}";

    public static Alarm DefaultReviewRequestTrigger() =>
        new("GitHub · 리뷰 요청", "", 0, 0, new Repeat.Daily(), trigger: new Trigger("github", "github.review_requested"));

    public static Alarm DefaultDailyReport(int hour = 10, int minute = 0, string? lastRunDay = null) =>
        new("미완료 Jira 보고", DailyReportPrompt, hour, minute, new Repeat.Daily(), lastRunDay: lastRunDay);

    /// 지금 실행할지. 오늘이 반복 대상 날이고, 시각이 지났고, 오늘 아직 안 돌렸으면 실행한다
    /// (잠자기로 시각을 놓쳤어도 깨어난 뒤 그날 1회).
    public bool ShouldRun(DateTimeOffset now, TimeZoneInfo? timeZone = null)
    {
        if (!Enabled || Trigger != null || !AppliesOn(now, timeZone)) return false;
        return ReportSchedule.ShouldRun(now, LastRunDay, Hour, Minute, timeZone);
    }

    /// 새로 만들거나 시각을 바꾼 알림. 오늘 시각이 이미 지났으면 오늘은 건너뛰고 다음부터 울린다
    /// (오후에 "매일 9시" 를 등록하자마자 울리지 않게). 이 알람 자체를 바꾼다.
    public void ResetSchedule(DateTimeOffset now, TimeZoneInfo? timeZone = null)
    {
        var local = TimeZoneInfo.ConvertTime(now, timeZone ?? TimeZoneInfo.Local);
        var passed = (local.Hour, local.Minute).CompareTo((Hour, Minute)) >= 0;
        LastRunDay = passed ? ReportSchedule.DayString(now, timeZone) : null;
    }

    public bool AppliesOn(DateTimeOffset date, TimeZoneInfo? timeZone = null)
    {
        var local = TimeZoneInfo.ConvertTime(date, timeZone ?? TimeZoneInfo.Local);
        var weekday = (int)local.DayOfWeek + 1;   // 1=일 … 7=토
        return RepeatRule switch
        {
            Repeat.Daily => true,
            Repeat.Weekdays => weekday is >= 2 and <= 6,
            Repeat.Days days => days.Values.Contains(weekday),
            Repeat.Once once => ReportSchedule.DayString(date, timeZone) == once.Day,
            _ => false,
        };
    }

    public bool Equals(Alarm? o) =>
        o is not null && Id == o.Id && Title == o.Title && Prompt == o.Prompt && Hour == o.Hour && Minute == o.Minute && Equals(RepeatRule, o.RepeatRule)
        && Enabled == o.Enabled && LastRunDay == o.LastRunDay && Equals(Trigger, o.Trigger) && Group == o.Group && NotifyMac == o.NotifyMac
        && NotifyPanel == o.NotifyPanel && AllowWrite == o.AllowWrite && Command == o.Command && TimeoutMinutes == o.TimeoutMinutes
        && UseWorkSessions == o.UseWorkSessions && UseRolePipeline == o.UseRolePipeline && UseDomainReviewer == o.UseDomainReviewer;

    public override int GetHashCode() => HashCode.Combine(Id, Title, Hour, Minute);
}

/// Swift 합성 Codable 형식의 반복 규칙: 키 하나짜리 객체
public sealed class RepeatConverter : JsonConverter<Alarm.Repeat>
{
    public override Alarm.Repeat Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var node = JsonNode.Parse(ref reader) as JsonObject;
        if (node == null || node.Count != 1) throw new JsonException("반복 규칙은 키 하나짜리 객체여야 해요");
        var (key, value) = node.First();
        switch (key)
        {
            case "daily": return new Alarm.Repeat.Daily();
            case "weekdays": return new Alarm.Repeat.Weekdays();
            case "days":
                {
                    var days = value is JsonObject d ? d["_0"] : value;
                    if (days is not JsonArray arr) throw new JsonException("days 가 배열이 아니에요");
                    return new Alarm.Repeat.Days(arr.Select(i => Json.IntExact(i) ?? throw new JsonException("요일이 정수가 아니에요")).ToList());
                }
            case "once":
                {
                    var day = Json.Str(value is JsonObject o ? o["_0"] : value);
                    return new Alarm.Repeat.Once(day ?? throw new JsonException("once 가 문자열이 아니에요"));
                }
            default: throw new JsonException($"모르는 반복 규칙: {key}");
        }
    }

    public override void Write(Utf8JsonWriter writer, Alarm.Repeat value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        switch (value)
        {
            case Alarm.Repeat.Daily:
                writer.WriteStartObject("daily"); writer.WriteEndObject();
                break;
            case Alarm.Repeat.Weekdays:
                writer.WriteStartObject("weekdays"); writer.WriteEndObject();
                break;
            case Alarm.Repeat.Days days:
                writer.WriteStartObject("days");
                writer.WriteStartArray("_0");
                foreach (var d in days.Values) writer.WriteNumberValue(d);
                writer.WriteEndArray();
                writer.WriteEndObject();
                break;
            case Alarm.Repeat.Once once:
                writer.WriteStartObject("once");
                writer.WriteString("_0", once.Day);
                writer.WriteEndObject();
                break;
        }
        writer.WriteEndObject();
    }
}

/// `alarms.json`(알람)과 `alarm-groups.json`(그룹 이름) 읽기·쓰기. 둘 다 저장소 `local\` 아래.
public static class AlarmStore
{
    public static string File => AppPaths.LocalFile("alarms.json");

    /// local 이 없으면 빈 목록. 파일이 없거나 깨졌으면 기본 보고 알람 하나.
    public static List<Alarm> Load(AppSettings settings, string? file = null, bool? ready = null)
    {
        if (!(ready ?? AppPaths.LocalReady)) return new List<Alarm>();
        try
        {
            if (Json.Deserialize<List<Alarm>>(Paths.ReadShared(file ?? File)) is { } alarms) return alarms;
        }
        catch { }
        return new List<Alarm> { Alarm.DefaultDailyReport(settings.ReportHour, settings.ReportMinute, settings.LastReportDay) };
    }

    /// local 이 없으면 저장하지 않는다. 실패하면 false.
    public static bool Save(IReadOnlyList<Alarm> alarms, string? file = null, bool? ready = null)
    {
        if (!(ready ?? AppPaths.LocalReady)) return false;
        try { AppPaths.WriteAtomic(file ?? File, Json.SortedPretty(alarms)); return true; }
        catch { return false; }
    }

    public static string GroupsFile => AppPaths.LocalFile("alarm-groups.json");

    public static List<string> LoadGroups()
    {
        if (!AppPaths.LocalReady) return new List<string>();
        try { return Json.Deserialize<List<string>>(Paths.ReadShared(GroupsFile)) ?? new List<string>(); }
        catch { return new List<string>(); }
    }

    public static bool SaveGroups(IReadOnlyList<string> groups)
    {
        if (!AppPaths.LocalReady) return false;
        try { AppPaths.WriteAtomic(GroupsFile, Json.Serialize(groups, indented: false)); return true; }
        catch { return false; }
    }

    /// `{{file:~/a/b.md}}` → 파일 경로 (`~/` 는 홈 폴더로)
    public static string? PromptFilePath(string prompt, string? home = null)
    {
        var trimmed = prompt.Trim();
        if (!trimmed.StartsWith("{{file:", StringComparison.Ordinal) || !trimmed.EndsWith("}}", StringComparison.Ordinal)) return null;
        var raw = SwiftText.TrimSpaces(trimmed[7..^2]);
        if (raw.Length == 0) return null;
        return Paths.ExpandTilde(raw, home);
    }

    /// `{{prompt:이름}}` → 이름
    public static string? PromptName(string prompt)
    {
        var trimmed = prompt.Trim();
        if (!trimmed.StartsWith("{{prompt:", StringComparison.Ordinal) || !trimmed.EndsWith("}}", StringComparison.Ordinal)) return null;
        var raw = SwiftText.TrimSpaces(trimmed[9..^2]);
        return raw.Length == 0 ? null : raw;
    }

    static string? ReadText(string? path)
    {
        if (path == null) return null;
        try { return Paths.ReadShared(path); } catch { return null; }
    }

    /// 알람이 Claude 에게 보낼 내용. 파일을 읽지 못하면 null (실패로 알린다).
    public static string? ResolvePrompt(Alarm alarm, AppSettings settings, string? promptsDir = null)
    {
        if (PromptName(alarm.Prompt) is string name)
            return ReadText(promptsDir != null ? Rules.PromptFile(name, promptsDir) : Rules.ResolvedPrompt(name));
        if (PromptFilePath(alarm.Prompt) is string path) return ReadText(path);
        if (alarm.Prompt != Alarm.DailyReportPrompt) return alarm.Prompt;
        return ReadText(AppPaths.SharedFile("app/daily-report.md", settings));
    }
}
