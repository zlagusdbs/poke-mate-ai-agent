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

/// 세션을 이어 쓸지 새로 시작할지 정하는 기준 (설정의 "세션 이어 쓰기").
public sealed record SessionReuse
{
    public const int DefaultMinutes = 55;
    public const int DefaultTokens = 120_000;

    public const string ReviewerReason = "리뷰어는 늘 새로";

    public int IdleMinutes { get; set; }
    public long MaxTokens { get; set; }
    public bool FreshReviewer { get; set; }

    public SessionReuse(int idleMinutes = DefaultMinutes, long maxTokens = DefaultTokens, bool freshReviewer = true)
    {
        IdleMinutes = idleMinutes;
        MaxTokens = maxTokens;
        FreshReviewer = freshReviewer;
    }

    public SessionReuse(AppSettings settings)
        : this(settings.SessionReuseMinutes, settings.SessionReuseMaxTokens, settings.ReviewerAlwaysFresh) { }

    public static SessionReuse Always => new(0, 0, false);

    /// 이 세션을 새로 시작해야 하면 그 이유 (이어 써도 되면 null). 0 이면 그 기준은 끈다.
    public string? FreshReason(WorkSession session, DateTimeOffset now) =>
        FreshReason(PipelineRoleExtensions.Split(session.Repo) is { Role: PipelineRole.Reviewer }, session.LastUsed, session.ContextTokens, now);

    /// 티켓 하나의 역할 세션(`pipelines\{KEY}.json` 의 sessions)에 같은 기준을 쓴다.
    public string? FreshReason(PipelineSession session, PipelineRole role, DateTimeOffset now) =>
        FreshReason(role == PipelineRole.Reviewer, session.LastUsed, session.ContextTokens, now);

    string? FreshReason(bool reviewer, DateTimeOffset lastUsed, long contextTokens, DateTimeOffset now)
    {
        if (FreshReviewer && reviewer) return ReviewerReason;
        var idle = (int)((now - lastUsed).TotalSeconds / 60);
        if (IdleMinutes > 0 && idle >= IdleMinutes) return $"식음 {idle}분 (기준 {IdleMinutes}분)";
        if (MaxTokens > 0 && contextTokens >= MaxTokens) return $"문맥 {TokenFormat.Compact(contextTokens)} (기준 {TokenFormat.Compact(MaxTokens)})";
        return null;
    }

    /// 같은 도메인의 앞 티켓 세션이 있는데 이 티켓은 아직 그 역할 세션이 없을 때 작업 탭에 남기는 이유
    public static string NewTicketReason(string previousTicket) => $"새 티켓 (앞: {previousTicket})";
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

    /// 이어 쓸 세션 id. 새로 시작해야 하면(기준에 걸리면) 옛 세션을 보관하고 이유를 남긴 빈 세션을 두고 null.
    public string? ReuseId(string repo, SessionReuse policy, DateTimeOffset now)
    {
        if (!Sessions.TryGetValue(repo, out var s) || s.Id.Length == 0) return null;
        if (policy.FreshReason(s, now) is not string reason) return s.Id;
        Archived.Add(s);
        Sessions[repo] = new WorkSession("", repo, now) { FreshReason = reason };
        return null;
    }

    /// 역할 세션을 새 티켓(또는 기준에 걸린 같은 티켓)이 새로 시작할 때 작업 탭 기록을 맞춘다. 앞 세션은 보관하고 이유를 남긴 빈 세션을 둔다.
    /// `reason` 이 없고 앞 세션이 다른 티켓의 것이면 "새 티켓 (앞: KEY)". 바뀌었으면 true.
    public bool StartFresh(string repo, string ticket, string? reason, DateTimeOffset now)
    {
        Sessions.TryGetValue(repo, out var s);
        var live = s != null && s.Id.Length > 0;
        if (reason == null && live && s!.Keys.LastOrDefault() is string previous && previous != ticket) reason = SessionReuse.NewTicketReason(previous);
        if (!live && reason == null) return false;
        if (live) Archived.Add(s!);
        Sessions[repo] = new WorkSession("", repo, now) { FreshReason = reason };
        return true;
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
