using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace PokeMate.Core;

// RunUsage 는 Task 2 계획대로 TokenUsage.cs 에 있다 (맥에서는 이 파일).
// 토큰 수는 모두 64비트다.

/// 작업 세션 하나 (레포 또는 `{도메인}:{역할}` 하나에 Claude 세션 하나). `sessions.json` 의 한 칸: 날짜는 ISO 8601, 키는 맥과 같다.
/// 값이 클래스라서 맥의 값 복사가 필요한 곳은 `Copy()` 를 쓴다.
public sealed record WorkSession
{
    public string Id { get; set; } = "";
    public string Repo { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset RotatedAt { get; set; }
    public DateTimeOffset LastUsed { get; set; }
    public List<string> Keys { get; set; } = new();
    public long ContextTokens { get; set; }
    public long? ContextWindow { get; set; }
    public RunUsage Usage { get; set; } = new();
    public string? SummaryFrom { get; set; }
    public bool? ContextAlerted { get; set; }
    public string? FreshReason { get; set; }

    public WorkSession() { }

    public WorkSession(string id, string repo, DateTimeOffset now, string? summaryFrom = null)
    {
        Id = id;
        Repo = repo;
        CreatedAt = now;
        RotatedAt = now;
        LastUsed = now;
        SummaryFrom = summaryFrom;
    }

    /// 문맥 창 대비 마지막 실행의 문맥 % (최대 100). 창을 모르면 null.
    [JsonIgnore]
    public int? ContextPercent => ContextWindow is long window && window > 0 ? (int)Math.Min(100, ContextTokens * 100 / window) : null;

    public WorkSession Copy() => this with { Keys = new List<string>(Keys), Usage = Usage with { } };

    /// 파일에 `null` 이 적혀 있어도 뒤에서 죽지 않게
    internal void Normalize()
    {
        Keys ??= new();
        Usage ??= new();
    }

    public bool Equals(WorkSession? o) =>
        o is not null && Id == o.Id && Repo == o.Repo && CreatedAt == o.CreatedAt && RotatedAt == o.RotatedAt && LastUsed == o.LastUsed
        && Keys.SequenceEqual(o.Keys) && ContextTokens == o.ContextTokens && ContextWindow == o.ContextWindow && Usage.Equals(o.Usage)
        && SummaryFrom == o.SummaryFrom && ContextAlerted == o.ContextAlerted && FreshReason == o.FreshReason;

    public override int GetHashCode() => HashCode.Combine(Id, Repo, LastUsed);
}

//// 자동 처리 세션을 이어 쓸지 새로 시작할지 정하는 단 하나의 규칙 (설정의 "세션 이어 쓰기": 쉰 시간·문맥 크기).
/// 세션 단위는 작업 한 건(`jira:{KEY}`, `pr:{owner}/{repo}#{number}`)이다. 같은 건 안에서만 이어 쓰고, 건이 바뀌면 늘 새로 시작한다.
/// 모든 자동 처리 진입점(Jira 역할 처리·GitHub 리뷰·Trial·Eval)이 `Decide` 만 부른다. 기능별 예외는 두지 않는다.
public sealed record SessionPolicy
{
    public const int DefaultMinutes = 55;
    public const int DefaultTokens = 120_000;

    public int IdleMinutes { get; set; }
    public long MaxTokens { get; set; }

    public SessionPolicy(int idleMinutes = DefaultMinutes, long maxTokens = DefaultTokens)
    {
        IdleMinutes = idleMinutes;
        MaxTokens = maxTokens;
    }

    public SessionPolicy(AppSettings settings) : this(settings.SessionReuseMinutes, settings.SessionReuseMaxTokens) { }

    /// 같은 건 안에서는 늘 이어 쓰기 (둘 다 0)
    public static SessionPolicy Always => new(0, 0);

    /// 그 건·역할의 이전 세션. 다른 건의 것일 수도 있다 (작업 탭 기록에서 온 것: 그때는 새로 시작하고 이유만 남는다).
    public sealed record Previous(string CaseKey, string Id, DateTimeOffset LastUsed, long ContextTokens);

    /// 이어 쓸 세션 id (새로 시작하면 null)와 새로 시작하는 이유 (처음이면 null)
    public sealed record Decision(string? ResumeId, string? Reason)
    {
        public bool Resumed => ResumeId != null;
    }

    /// 이어 쓰기: 이전 세션이 같은 건이고, 마지막 사용이 IdleMinutes 안이고, 문맥이 MaxTokens 미만. 0 이면 그 기준은 끈다.
    /// 다른 건의 세션은 어떤 설정에서도 이어 쓰지 않는다. `role` 은 기록용이고 판단을 바꾸지 않는다 (역할별 예외 없음).
    public Decision Decide(string caseKey, string role, Previous? previous, DateTimeOffset now)
    {
        if (previous == null || previous.Id.Length == 0) return new Decision(null, null);
        if (!SameCase(previous.CaseKey, caseKey))
            return new Decision(null, previous.CaseKey.Length == 0 ? null : NewCaseReason(caseKey, previous.CaseKey));
        var idle = (int)((now - previous.LastUsed).TotalSeconds / 60);
        if (IdleMinutes > 0 && idle >= IdleMinutes) return new Decision(null, $"식음 {idle}분 (기준 {IdleMinutes}분)");
        if (MaxTokens > 0 && previous.ContextTokens >= MaxTokens)
            return new Decision(null, $"문맥 {TokenFormat.Compact(previous.ContextTokens)} (기준 {TokenFormat.Compact(MaxTokens)})");
        return new Decision(previous.Id, null);
    }

    public const string JiraPrefix = "jira:";
    public const string PrPrefix = "pr:";

    /// Jira 티켓 한 건의 키: `jira:{KEY}`
    public static string JiraKey(string ticket) => JiraPrefix + ticket;

    /// PR 한 건의 키: `pr:{owner}/{repo}#{number}`
    public static string PrKey(string owner, string repo, int number) => $"{PrPrefix}{owner}/{repo}#{number}";

    /// 화면·작업 탭 기록(`keys`)에 쓰는 짧은 이름: `jira:ACME-101` → `ACME-101`, `pr:acme/svc#12` → `svc#12`. 접두어가 없으면 그대로.
    public static string Label(string caseKey)
    {
        if (caseKey.StartsWith(JiraPrefix, StringComparison.Ordinal)) return caseKey[JiraPrefix.Length..];
        if (caseKey.StartsWith(PrPrefix, StringComparison.Ordinal))
        {
            var rest = caseKey[PrPrefix.Length..];
            var slash = rest.LastIndexOf('/');
            return slash >= 0 ? rest[(slash + 1)..] : rest;
        }
        return caseKey;
    }

    static bool Prefixed(string key) => key.StartsWith(JiraPrefix, StringComparison.Ordinal) || key.StartsWith(PrPrefix, StringComparison.Ordinal);

    /// 같은 건인지. 둘 다 접두어가 있으면 키 그대로, 한쪽이 작업 탭 기록의 짧은 이름이면 짧은 이름끼리 비교한다.
    public static bool SameCase(string a, string b)
    {
        if (a == b) return true;
        if (Prefixed(a) && Prefixed(b)) return false;
        return Label(a) == Label(b);
    }

    /// 건이 바뀌어 새로 시작하는 이유: PR 이면 "새 PR (앞: repo#12)", 아니면 "새 티켓 (앞: ACME-101)"
    public static string NewCaseReason(string caseKey, string previousCaseKey) =>
        caseKey.StartsWith(PrPrefix, StringComparison.Ordinal) ? NewPrReason(Label(previousCaseKey)) : NewTicketReason(Label(previousCaseKey));

    public static string NewTicketReason(string previousTicket) => $"새 티켓 (앞: {previousTicket})";

    public static string NewPrReason(string previousLabel) => $"새 PR (앞: {previousLabel})";
}

/// `automation\sessions.json`: 레포(또는 `{도메인}:{역할}`) → 지금 세션, 그리고 보관한 옛 세션들. 파이썬 서버(alarms.py)도 읽는다.
/// 읽다가 파일이 깨져 있으면(서버가 쓰는 도중 등) 앱이 죽지 않고 빈 값을 돌려준다.
public sealed record WorkSessionBook
{
    public const string Operations = "운영";

    public Dictionary<string, WorkSession> Sessions { get; set; } = new();
    public List<WorkSession> Archived { get; set; } = new();

    public WorkSessionBook() { }

    /// 맥의 값 복사 (세션 안의 목록까지 따로)
    public WorkSessionBook Copy() => new()
    {
        Sessions = Sessions.ToDictionary(kv => kv.Key, kv => kv.Value.Copy()),
        Archived = Archived.Select(s => s.Copy()).ToList(),
    };

    public bool Equals(WorkSessionBook? o) =>
        o is not null && Sessions.Count == o.Sessions.Count
        && Sessions.All(kv => o.Sessions.TryGetValue(kv.Key, out var other) && kv.Value.Equals(other))
        && Archived.SequenceEqual(o.Archived);

    public override int GetHashCode() => HashCode.Combine(Sessions.Count, Archived.Count);

    public static string RepoKey(string? repo)
    {
        var trimmed = SwiftText.Trim(repo ?? "");
        return trimmed.Length == 0 ? Operations : trimmed;
    }

    /// 문맥 % 가 문턱을 처음 넘을 때만 그 세션(복사본)을 돌려준다. 문턱 아래로 내려가면(또는 문턱을 끄면) 기록을 푼다.
    public WorkSession? ContextAlert(string repo, int? threshold)
    {
        if (!Sessions.TryGetValue(repo, out var s) || s.ContextPercent is not int percent) return null;
        if (threshold is not int limit || limit <= 0)
        {
            if (s.ContextAlerted == true) s.ContextAlerted = null;
            return null;
        }
        if (percent >= limit)
        {
            if (s.ContextAlerted == true) return null;
            s.ContextAlerted = true;
            return s.Copy();
        }
        if (s.ContextAlerted == true) s.ContextAlerted = null;
        return null;
    }

    /// 레포 작업 세션(알림 자동 처리의 레포 세션)을 이어 쓸 id. 레포 세션은 그 레포 자체가 한 건이다 (`SessionPolicy.Decide` 로 시간·문맥만 본다).
    /// 새로 시작해야 하면 옛 세션을 보관하고 이유를 남긴 빈 세션을 두고 null.
    public string? ReuseId(string repo, SessionPolicy policy, DateTimeOffset now)
    {
        if (!Sessions.TryGetValue(repo, out var s) || s.Id.Length == 0) return null;
        var decision = policy.Decide(repo, "", new SessionPolicy.Previous(repo, s.Id, s.LastUsed, s.ContextTokens), now);
        if (decision.ResumeId != null) return decision.ResumeId;
        Archived.Add(s);
        Sessions[repo] = new WorkSession("", repo, now) { FreshReason = decision.Reason };
        return null;
    }

    /// 작업 탭 칸(`{도메인}:{역할}`)의 마지막 세션: 지금 세션, 없으면(끝나 보관했으면) 가장 최근에 보관한 그 칸의 세션. 건 키는 그 세션의 마지막 `keys`.
    public SessionPolicy.Previous? Previous(string repo)
    {
        WorkSession? s = Sessions.TryGetValue(repo, out var live) && live.Id.Length > 0 ? live : LastArchived(repo);
        return s == null ? null : new SessionPolicy.Previous(s.Keys.LastOrDefault() ?? "", s.Id, s.LastUsed, s.ContextTokens);
    }

    /// 그 칸에서 가장 최근에 보관한 세션 (실행한 적 있는 것만)
    public WorkSession? LastArchived(string repo) =>
        Archived.Where(a => a.Repo == repo && a.Id.Length > 0).OrderBy(a => a.LastUsed).LastOrDefault();

    /// 역할 세션을 새로 시작할 때 작업 탭 기록을 맞춘다. 앞 세션은 보관하고 이유를 남긴 빈 세션을 둔다. 바뀌었으면 true.
    /// `reason` 이 없으면 그 칸의 마지막 세션(지금 것 또는 보관한 것)으로 `SessionPolicy.Decide` 가 이유를 정한다 (다른 건이면 "새 티켓 (앞: KEY)"·"새 PR (앞: repo#12)").
    public bool StartFresh(string repo, string caseKey, string? reason, DateTimeOffset now)
    {
        Sessions.TryGetValue(repo, out var s);
        var live = s != null && s.Id.Length > 0;
        reason ??= new SessionPolicy().Decide(caseKey, "", Previous(repo), now).Reason;
        if (!live && reason == null) return false;
        if (!live && s != null && s.FreshReason == reason) return false;
        if (live) Archived.Add(s!);
        Sessions[repo] = new WorkSession("", repo, now) { FreshReason = reason };
        return true;
    }

    /// 끝난 건(`label`: 티켓 키 또는 `repo#12`)의 역할 세션을 보관한다: 그 칸의 지금 세션이 이 건의 것이면 보관, 아직 실행하지 못한 빈 칸은 지운다.
    /// 보관한 세션은 작업 탭 목록에서 빠지고 기록(`archived`)은 남는다. 바뀌었으면 true.
    public bool ArchiveCase(IEnumerable<string> repos, string label)
    {
        var changed = false;
        foreach (var repo in repos)
        {
            if (!Sessions.TryGetValue(repo, out var s)) continue;
            if (s.Id.Length == 0)
            {
                Sessions.Remove(repo);
                changed = true;
            }
            else if (s.Keys.LastOrDefault() == label)
            {
                Sessions.Remove(repo);
                Archived.Add(s);
                changed = true;
            }
        }
        return changed;
    }
    public void Record(string repo, string sessionId, string? key, RunUsage? usage, DateTimeOffset now)
    {
        var s = Sessions.TryGetValue(repo, out var existing) ? existing : new WorkSession(sessionId, repo, now);
        if (s.Id.Length == 0) s.Id = sessionId;
        if (s.Id != sessionId)
        {
            Archived.Add(s);
            s = new WorkSession(sessionId, repo, now);
        }
        s.LastUsed = now;
        if (!string.IsNullOrEmpty(key) && !s.Keys.Contains(key)) s.Keys.Add(key);
        if (usage != null)
        {
            s.Usage.Add(usage);
            s.ContextTokens = usage.Context;
            s.ContextWindow = usage.ContextWindow ?? s.ContextWindow;
        }
        Sessions[repo] = s;
    }

    /// 세션을 새 id 로 바꾼다 (옛 세션은 보관, 새 세션은 옛 세션 요약을 받음).
    public void Replace(string repo, string sessionId, string? summaryFrom, DateTimeOffset now)
    {
        if (Sessions.TryGetValue(repo, out var current)) Archived.Add(current);
        Sessions[repo] = new WorkSession(sessionId, repo, now, summaryFrom);
    }

    public void Archive(string repo)
    {
        if (Sessions.Remove(repo, out var current)) Archived.Add(current);
    }

    /// 대화 기록에서 다시 센 사용량으로 덮어쓴다 (두 번 세지 않게). 문맥 창은 그대로.
    public void ApplyTranscript(string repo, RunUsage usage, long lastContext)
    {
        if (!Sessions.TryGetValue(repo, out var s)) return;
        var window = s.Usage.ContextWindow ?? s.ContextWindow;
        s.Usage = usage with { ContextWindow = window };
        s.ContextTokens = lastContext;
    }

    /// 아직 실행하지 않은 빈 세션(작업 탭의 "새 세션")을 만든다. 이미 있으면 false.
    public bool AddEmpty(string repo, DateTimeOffset now)
    {
        if (Sessions.ContainsKey(repo)) return false;
        Sessions[repo] = new WorkSession("", repo, now);
        return true;
    }

    public string? ResumeId(string repo) => Sessions.TryGetValue(repo, out var s) && s.Id.Length > 0 ? s.Id : null;

    public static string File => Path.Combine(AppPaths.DataDir, "automation", "sessions.json");

    /// 읽을 수 없거나 깨졌으면 빈 값. 다른 프로세스가 열어 둔 파일도 읽는다.
    public static WorkSessionBook Load(string? from = null)
    {
        try
        {
            var book = JsonSerializer.Deserialize<WorkSessionBook>(Paths.ReadShared(from ?? File), Json.Iso8601);
            if (book == null) return new WorkSessionBook();
            book.Sessions ??= new();
            book.Archived ??= new();
            if (book.Sessions.Values.Any(s => s == null) || book.Archived.Any(s => s == null)) return new WorkSessionBook();
            foreach (var s in book.Sessions.Values.Concat(book.Archived)) s.Normalize();
            return book;
        }
        catch { return new WorkSessionBook(); }
    }

    /// 키 정렬·들여쓰기·ISO 8601 날짜 (맥과 같은 모양). 실패하면(다른 프로세스가 잡고 있는 경우 등) false 이고 이전 파일은 그대로다.
    public bool Save(string? to = null)
    {
        try
        {
            AppPaths.WriteAtomic(to ?? File, Json.SortedPretty(this, Json.Iso8601));
            return true;
        }
        catch { return false; }
    }
}

/// 자동 처리 레포 정하기 답: ```pokemate-route {"repo": "…"} 또는 {"skip": true}
/// 맥의 `String??` 자리: 블록이 없으면 null, `skip` 이면 `Repo` 가 null 인 값, 아니면 레포 이름(없으면 "운영").
public sealed record WorkRouteResult(string? Repo)
{
    public bool Skipped => Repo == null;
}

public static class WorkRoute
{
    public const string Tag = "pokemate-route";

    static readonly Regex Block = new("```" + Tag + @"[ \t]*\r?\n(.*?)```", RegexOptions.Singleline);

    public static WorkRouteResult? Extract(string text)
    {
        var m = Block.Match(text);
        if (!m.Success || Json.ParseObject(m.Groups[1].Value) is not JsonObject obj) return null;
        if (Json.Bool(obj["skip"]) == true) return new WorkRouteResult(null);
        return new WorkRouteResult(WorkSessionBook.RepoKey(Json.Str(obj["repo"])));
    }
}

/// 같은 키의 작업은 차례로, 다른 키는 동시에 (세션 하나에 두 번 말을 걸지 않게).
public sealed class SerialQueue
{
    readonly object gate = new();
    readonly Dictionary<string, Task> tails = new();

    public Task Run(string key, Func<Task> job)
    {
        lock (gate)
        {
            tails.TryGetValue(key, out var previous);
            var task = Task.Run(async () =>
            {
                if (previous != null)
                {
                    try { await previous; } catch { }   // 앞 작업이 실패해도 다음 건은 한다
                }
                await job();
            });
            tails[key] = task;
            return task;
        }
    }
}

/// 세션 대화 기록 한 줄 (작업 탭의 기록 보기).
public sealed record TranscriptEntry(int Id, TranscriptEntry.EntryKind Kind, string Time, string Text)
{
    /// 맥의 중첩 enum `Kind` 는 속성 `Kind` 와 겹쳐 `EntryKind` 로 둔다.
    public enum EntryKind { User, Assistant, Tool, Result }
}

public static class SessionTranscript
{
    /// Claude 의 `*.jsonl` 대화 기록을 사용자·답·도구·결과 줄로. 마지막 `limit` 줄만.
    public static List<TranscriptEntry> Parse(string data, int limit = 400)
    {
        var rows = new List<TranscriptEntry>();
        foreach (var line in data.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (Json.ParseObject(line) is not JsonObject o) continue;
            var type = Json.Str(o["type"]);
            if (type is not ("user" or "assistant") || o["message"] is not JsonObject message) continue;
            var time = SwiftText.Prefix(Json.Str(o["timestamp"]) ?? "", 19).Replace("T", " ");
            void Add(TranscriptEntry.EntryKind kind, string text)
            {
                var t = SwiftText.Trim(text);
                if (t.Length == 0) return;
                rows.Add(new TranscriptEntry(rows.Count, kind, time, SwiftText.Count(t) > 1200 ? SwiftText.Prefix(t, 1200) + "…" : t));
            }
            var speaker = type == "user" ? TranscriptEntry.EntryKind.User : TranscriptEntry.EntryKind.Assistant;
            if (Json.Str(message["content"]) is string plain)
            {
                Add(speaker, plain);
                continue;
            }
            if (message["content"] is not JsonArray items || items.Any(i => i is not JsonObject)) continue;
            foreach (var c in items.Cast<JsonObject>())
            {
                switch (Json.Str(c["type"]))
                {
                    case "text":
                        Add(speaker, Json.Str(c["text"]) ?? "");
                        break;
                    case "tool_use":
                        Add(TranscriptEntry.EntryKind.Tool, ClaudeStream.Activity(Json.Str(c["name"]) ?? "", c["input"] as JsonObject ?? new JsonObject()));
                        break;
                    case "tool_result":
                        var content = c["content"];
                        var text = Json.Str(content)
                                   ?? (content is JsonArray parts && parts.All(p => p is JsonObject)
                                       ? string.Join("\n", parts.Cast<JsonObject>().Select(p => Json.Str(p["text"])).Where(s => s != null))
                                       : "");
                        Add(TranscriptEntry.EntryKind.Result, (Json.Bool(c["is_error"]) == true ? "오류: " : "") + SwiftText.Prefix(text, 300));
                        break;
                }
            }
        }
        return rows.Count > limit ? rows.GetRange(rows.Count - limit, limit) : rows;
    }
}

public static class TranscriptUsage
{
    static readonly Regex HasZone = new(@"(Z|[+-]\d{2}:?\d{2})$", RegexOptions.Compiled);

    static DateTimeOffset? Stamp(string text) =>
        HasZone.IsMatch(text) && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d) ? d : null;

    /// 대화 기록에서 사용량을 다시 센다 (같은 메시지 id 는 한 번만, 비용은 단가표). `since` 이후 기록만. LastContext 는 마지막 답의 문맥 토큰.
    public static (RunUsage Usage, long LastContext) Total(string data, DateTimeOffset? since = null)
    {
        var seen = new HashSet<string>();
        var usage = new RunUsage();
        long last = 0;
        foreach (var line in data.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (Json.ParseObject(line) is not JsonObject o || Json.Str(o["type"]) != "assistant"
                || o["message"] is not JsonObject message || message["usage"] is not JsonObject u) continue;
            if (since is DateTimeOffset from)
            {
                if (Stamp(Json.Str(o["timestamp"]) ?? "") is not DateTimeOffset date || date < from) continue;
            }
            var id = Json.Str(message["id"]) ?? Json.Str(o["uuid"]) ?? Guid.NewGuid().ToString().ToUpperInvariant();
            if (!seen.Add(id)) continue;
            long Count(string k) => Json.Long(u[k]) ?? 0;
            var bucket = new TokenBucket(Count("input_tokens"), Count("output_tokens"), Count("cache_creation_input_tokens"), Count("cache_read_input_tokens"));
            var model = Json.Str(message["model"]) ?? "";
            usage.Add(new RunUsage(bucket.Input, bucket.Output, bucket.CacheRead, bucket.CacheWrite, ClaudePricing.Cost(model, bucket) ?? 0));
            last = bucket.Input + bucket.CacheRead + bucket.CacheWrite;
        }
        return (usage, last);
    }
}
