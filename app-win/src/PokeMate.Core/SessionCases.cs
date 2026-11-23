using System.Text.Json;
using System.Text.Json.Serialization;

namespace PokeMate.Core;

/// 인계 메모 한 줄: 역할이 한 차례를 마칠 때 앱이 작업 건 파일(`pipelines\{KEY}.json`·`prs\{owner}__{repo}__{n}.json`)의 `handoffs` 에 남긴다.
/// JSON: `{"role", "at"(ISO 8601), "summary", "findings"(리뷰어만: 결함·미확인 항목 제목)}`
public sealed record CaseHandoff(string Role, DateTimeOffset At, string Summary, List<string>? Findings = null)
{
    public bool Equals(CaseHandoff? o) =>
        o is not null && Role == o.Role && At == o.At && Summary == o.Summary
        && (Findings == null ? o.Findings == null : o.Findings != null && Findings.SequenceEqual(o.Findings));

    public override int GetHashCode() => HashCode.Combine(Role, At, Summary);
}

/// 세션 맥락은 대화 기록이 아니라 파일로 넘긴다: 인계 메모 만들기·"이번 건 인계" 블록·리뷰어 지적 뽑기.
public static class Handoffs
{
    /// 블록에 넣는 최근 메모 수
    public const int MaxNotes = 8;
    /// 블록 전체 글자 수 상한
    public const int MaxChars = 4000;
    /// 파일에 남기는 메모 수 상한 (오래된 것부터 버림)
    public const int Keep = 50;
    public const int SummaryLimit = 600;
    public const int FindingLimit = 160;
    public const int MaxFindings = 20;

    public const string Title = "## 이번 건 인계";
    public const string Intro = "이 건의 앞 차례들이 남긴 인계 메모다(오래된 것부터). 이 세션은 새로 시작해 앞 대화를 모르니, 이 메모와 파일(작업 건 파일·도메인 노트·지도)로 맥락을 잡는다.";
    public const string FindingsTitle = "### 내 이전 지적 (리뷰어)";

    /// 메모 요약: 다음 단계에게 넘긴 note 와 답 앞부분 (새로 묻지 않는다). 600자 안.
    public static string Summary(string note, string answer)
    {
        var n = SwiftText.Trim(note);
        var a = SwiftText.Trim(PipelineNext.Strip(answer));
        string text;
        if (n.Length == 0) text = a;
        else if (a.Length == 0 || a == n) text = n;
        else text = n + "\n답: " + SwiftText.Prefix(a, 300);
        return SwiftText.Prefix(text, SummaryLimit);
    }

    /// 결과 문서의 "결함"·"미확인(질문)" 절에서 항목 제목을 뽑는다 (`결함: …`, `미확인: …`). 없음·건수 줄은 빼고, 같은 것은 한 번만, 20개까지.
    public static List<string> Findings(string text)
    {
        var found = new List<string>();
        string? current = null;
        foreach (var raw in SwiftText.Lf(text).Split('\n'))
        {
            var line = SwiftText.Trim(raw);
            var head = SwiftText.Trim(line.Replace("**", "").TrimStart('#', '*', ' '));
            if (Header(head) is { } hit)
            {
                current = hit.Kind;
                if (hit.Tail.Length > 0 && !Empty(hit.Tail)) Add(found, current, hit.Tail);
                continue;
            }
            if (line.Length == 0 || current == null) continue;
            if (Bullet(line) is { } item)
            {
                if (!Empty(item)) Add(found, current, item);
            }
            else if (raw.Length > 0 && (raw[0] == ' ' || raw[0] == '\t')) continue;
            else current = null;
        }
        return found;
    }

    static (string Kind, string Tail)? Header(string head)
    {
        foreach (var kind in new[] { "결함", "미확인" })
        {
            if (!head.StartsWith(kind, StringComparison.Ordinal)) continue;
            var rest = head[kind.Length..];
            if (rest.StartsWith('('))
            {
                var close = rest.IndexOf(')');
                if (close < 0) return null;
                rest = rest[(close + 1)..];
            }
            rest = rest.TrimStart(' ', '\t');
            if (rest.Length == 0) return (kind, "");
            if (rest[0] == ':' || rest[0] == '：') return (kind, SwiftText.Trim(rest[1..]).Trim('*', ' '));
            return null;
        }
        return null;
    }

    static string? Bullet(string line)
    {
        foreach (var mark in new[] { "- ", "* ", "• " })
            if (line.StartsWith(mark, StringComparison.Ordinal)) return SwiftText.Trim(line[mark.Length..].Replace("**", ""));
        var digits = 0;
        while (digits < line.Length && char.IsAsciiDigit(line[digits])) digits++;
        if (digits > 0 && digits + 1 < line.Length && (line[digits] == '.' || line[digits] == ')') && line[digits + 1] == ' ')
            return SwiftText.Trim(line[(digits + 2)..].Replace("**", ""));
        return null;
    }

    /// "없음", "0건"·"2 건" 같은 건수만 있는 줄, "-"
    static bool Empty(string text)
    {
        var t = SwiftText.Trim(text);
        if (t.Length == 0 || t == "-" || t.StartsWith("없음", StringComparison.Ordinal)) return true;
        var digits = 0;
        while (digits < t.Length && char.IsAsciiDigit(t[digits])) digits++;
        return digits > 0 && t[digits..].Trim(' ') == "건";
    }

    static void Add(List<string> found, string kind, string item)
    {
        if (found.Count >= MaxFindings) return;
        var title = $"{kind}: {SwiftText.Prefix(item, FindingLimit)}";
        if (!found.Contains(title)) found.Add(title);
    }

    /// 새 세션 프롬프트 앞에 붙이는 "이번 건 인계" 블록. 같은 건의 앞 차례가 없으면 null (처음).
    /// 최근 메모 8개(오래된 것부터), 리뷰어에게는 자신의 마지막 지적. 블록 전체 4,000자 안: 넘치면 오래된 메모부터 뺀다.
    public static string? Block(IReadOnlyList<CaseHandoff>? handoffs, string role)
    {
        if (handoffs == null || handoffs.Count == 0) return null;
        var mine = role == PipelineRole.Reviewer.Raw()
            ? handoffs.LastOrDefault(h => h.Role == role && h.Findings is { Count: > 0 })?.Findings
            : null;
        var notes = handoffs.Skip(Math.Max(0, handoffs.Count - MaxNotes)).ToList();
        while (true)
        {
            var parts = new List<string> { Title, Intro };
            parts.AddRange(notes.Select(Line));
            if (mine != null)
            {
                parts.Add(FindingsTitle);
                parts.AddRange(mine.Select(f => $"- {f}"));
            }
            var text = string.Join("\n", parts);
            if (SwiftText.Count(text) <= MaxChars) return text;
            if (notes.Count > 1)
            {
                notes.RemoveAt(0);
                continue;
            }
            return SwiftText.Prefix(text, MaxChars - 1) + "…";
        }
    }

    static string Line(CaseHandoff h)
    {
        var who = PipelineRoleExtensions.FromRaw(h.Role)?.Label() ?? h.Role;
        var summary = string.Join(" / ", SwiftText.Lf(h.Summary).Split('\n').Select(SwiftText.Trim).Where(l => l.Length > 0));
        return $"- {who}: {(summary.Length == 0 ? "(메모 없음)" : summary)}";
    }

    /// 메모를 덧붙인다 (오래된 것부터 50개까지)
    public static List<CaseHandoff> Append(List<CaseHandoff>? list, CaseHandoff handoff)
    {
        var all = list ?? new List<CaseHandoff>();
        all.Add(handoff);
        if (all.Count > Keep) all.RemoveRange(0, all.Count - Keep);
        return all;
    }
}

/// GitHub PR 한 건의 작업 건 파일 `automation\prs\{owner}__{repo}__{number}.json` (Jira 의 `pipelines\{KEY}.json` 과 같은 모양의 `sessions`·`handoffs`).
/// 리뷰를 APPROVE 로 올렸거나 PR 이 닫힌·머지된 것을 확인하면 보관(`archivedAt`)한다. 같은 PR 에 다시 리뷰 요청이 오면 다시 연다.
public sealed record ReviewCase
{
    /// open(리뷰 중) · approved(승인함) · closed(닫힘·머지됨)
    public enum StatusKind { Open, Approved, Closed }

    public const string ReviewerRole = "reviewer";

    public string Key { get; set; } = "";
    public string Owner { get; set; } = "";
    public string Repo { get; set; } = "";
    public int Number { get; set; }
    public string Url { get; set; } = "";
    public string Domain { get; set; } = "";
    public StatusKind Status { get; set; } = StatusKind.Open;
    /// 역할(reviewer) → 이 PR 의 그 역할 세션
    public Dictionary<string, PipelineSession>? Sessions { get; set; }
    public List<CaseHandoff>? Handoffs { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public ReviewCase() { }

    public ReviewCase(ReviewTarget target, string domain, DateTimeOffset now)
    {
        Key = SessionPolicy.PrKey(target.Owner, target.Repo, target.Number);
        Owner = target.Owner;
        Repo = target.Repo;
        Number = target.Number;
        Url = target.Url;
        Domain = domain;
        CreatedAt = now;
        UpdatedAt = now;
    }

    /// 작업 탭 기록(`keys`)에 쓰는 이름 `repo#12`
    [JsonIgnore] public string Label => $"{Repo}#{Number}";

    [JsonIgnore] public bool IsArchived => ArchivedAt != null;

    public bool Equals(ReviewCase? o) =>
        o is not null && Key == o.Key && Domain == o.Domain && Status == o.Status && ArchivedAt == o.ArchivedAt && UpdatedAt == o.UpdatedAt
        && (Sessions?.Count ?? -1) == (o.Sessions?.Count ?? -1) && (Handoffs?.Count ?? -1) == (o.Handoffs?.Count ?? -1);

    public override int GetHashCode() => HashCode.Combine(Key, Status, UpdatedAt);

    /// 그 역할의 이 PR 세션 (`SessionPolicy.Decide` 의 입력). 없으면 null.
    public SessionPolicy.Previous? PreviousSession(string role) =>
        Sessions != null && Sessions.TryGetValue(role, out var s) && !string.IsNullOrEmpty(s.Id)
            ? new SessionPolicy.Previous(Key, s.Id, s.LastUsed, s.ContextTokens) : null;

    public void RecordSession(string role, string id, RunUsage? usage, DateTimeOffset now)
    {
        Sessions ??= new();
        var before = Sessions.TryGetValue(role, out var old) && old.Id == id ? old.ContextTokens : 0;
        Sessions[role] = new PipelineSession(id, now, usage?.Context ?? before);
        UpdatedAt = now;
    }

    /// 리뷰 한 차례 시작: PR 파일을 읽거나(보관했으면 다시 열고) 만들고, `SessionPolicy` 로 이어 쓸지 고른다.
    public static (ReviewCase Case, SessionPolicy.Decision Decision) Begin(ReviewTarget target, string domain, SessionPolicy policy, string? dir, DateTimeOffset now)
    {
        var c = Load(target, dir) ?? new ReviewCase(target, domain, now);
        c.Domain = domain;
        c.Url = target.Url;
        if (c.IsArchived)
        {
            c.ArchivedAt = null;
            c.Status = StatusKind.Open;
        }
        c.UpdatedAt = now;
        return (c, policy.Decide(c.Key, ReviewerRole, c.PreviousSession(ReviewerRole), now));
    }

    /// 새 세션이면 붙이는 "이번 건 인계" 블록 (이어 쓰면·처음이면 null)
    public string? HandoffBlock(SessionPolicy.Decision decision) => decision.Resumed ? null : PokeMate.Core.Handoffs.Block(Handoffs, ReviewerRole);

    /// 리뷰 한 차례가 끝남: 세션·인계 메모(답 첫 줄 요약 + 결함·미확인)를 남기고, 승인·닫힘이면 보관한다. 바뀐 상태를 돌려준다.
    public StatusKind? Finish(string? sessionId, RunUsage? usage, string? answer, DateTimeOffset now)
    {
        if (!string.IsNullOrEmpty(sessionId)) RecordSession(ReviewerRole, sessionId, usage, now);
        UpdatedAt = now;
        if (answer == null) return null;
        var findings = PokeMate.Core.Handoffs.Findings(answer);
        Handoffs = PokeMate.Core.Handoffs.Append(Handoffs, new CaseHandoff(ReviewerRole, now, PokeMate.Core.Handoffs.Summary("", answer), findings));
        if (Outcome(answer) is not StatusKind done) return null;
        Status = done;
        ArchivedAt = now;
        return done;
    }

    /// 마지막 답 첫 줄(`레포#번호 · 결과 · …`)의 결과: "승인" → approved, "닫힘" → closed. 그 밖은 null (리뷰 중 그대로).
    public static StatusKind? Outcome(string answer)
    {
        var first = SwiftText.Lf(PipelineNext.Strip(answer)).Split('\n').Select(SwiftText.Trim).FirstOrDefault(l => l.Length > 0) ?? "";
        var parts = first.Split('·').Select(p => SwiftText.Trim(p.Replace("**", ""))).ToList();
        if (parts.Contains("승인")) return StatusKind.Approved;
        if (parts.Contains("닫힘")) return StatusKind.Closed;
        return null;
    }

    public static string Dir(string? baseDir = null) => Path.Combine(baseDir ?? AppPaths.DataDir, "automation", "prs");

    public static string FileName(ReviewTarget target) => $"{target.Owner}__{target.Repo}__{target.Number}.json";

    public static string File(ReviewTarget target, string? dir = null) => Path.Combine(dir ?? Dir(), FileName(target));

    /// 읽을 수 없거나 깨졌으면 null
    public static ReviewCase? Load(ReviewTarget target, string? dir = null)
    {
        try { return JsonSerializer.Deserialize<ReviewCase>(Paths.ReadShared(File(target, dir)), Json.Iso8601); }
        catch { return null; }
    }

    /// 키 정렬·들여쓰기·ISO 8601 날짜 (맥과 같은 모양). 실패하면 false.
    public bool Save(string? dir = null)
    {
        try
        {
            AppPaths.WriteAtomic(Path.Combine(dir ?? Dir(), $"{Owner}__{Repo}__{Number}.json"), Json.SortedPretty(this, Json.Iso8601));
            return true;
        }
        catch { return false; }
    }
}
