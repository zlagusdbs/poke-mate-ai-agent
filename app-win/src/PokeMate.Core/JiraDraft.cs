using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace PokeMate.Core;

/// 채팅으로 만든 Jira 이슈 초안. Claude 는 등록하지 않고 초안만 제안하고,
/// 사용자가 확인 카드에서 [Jira에 등록]을 눌러야 `pokemate jira create` 로 등록된다.
/// 읽을 때는 Claude 가 일부 항목을 빼먹거나 형식이 틀려도 빈칸으로 받는다 (맥 `init(from:)`).
[JsonConverter(typeof(JiraDraftConverter))]
public sealed record JiraDraft
{
    public string Project { get; set; } = "";
    public string IssueType { get; set; } = "";
    public string Summary { get; set; } = "";
    public string Description { get; set; } = "";
    public string OriginalEstimate { get; set; } = "";
    /// yyyy-MM-dd, 비우면 안 보냄
    public string TargetStart { get; set; } = "";
    public string TargetEnd { get; set; } = "";
    public string EpicLink { get; set; } = "";

    public JiraDraft() { }

    public JiraDraft(string project, string issueType, string summary, string description,
                     string originalEstimate = "", string targetStart = "", string targetEnd = "", string epicLink = "")
    {
        Project = project;
        IssueType = issueType;
        Summary = summary;
        Description = description;
        OriginalEstimate = originalEstimate;
        TargetStart = targetStart;
        TargetEnd = targetEnd;
        EpicLink = epicLink;
    }

    [JsonIgnore] public bool IsComplete => Project.Length > 0 && IssueType.Length > 0 && Summary.Length > 0;

    /// 카드에 보일 기간 한 줄. 예: "2026-10-22 ~ 2026-12-31 · 365d"
    [JsonIgnore]
    public string PeriodText
    {
        get
        {
            var period = new[] { TargetStart, TargetEnd }.Any(s => s.Length > 0)
                ? $"{(TargetStart.Length == 0 ? "?" : TargetStart)} ~ {(TargetEnd.Length == 0 ? "?" : TargetEnd)}" : "";
            return string.Join(" · ", new[] { period, OriginalEstimate }.Where(s => s.Length > 0));
        }
    }
}

public sealed class JiraDraftConverter : JsonConverter<JiraDraft>
{
    public override JiraDraft Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (JsonNode.Parse(ref reader) is not JsonObject o) throw new JsonException("Jira 초안은 JSON 객체여야 해요");
        // 문자열이 아니거나 없으면 빈칸 (맥 `try? decodeIfPresent`)
        string Text(string key) => Json.Str(o[key])?.Trim() ?? "";
        return new JiraDraft
        {
            Project = Text("project"),
            IssueType = Text("issueType"),
            Summary = Text("summary"),
            Description = Json.Str(o["description"]) ?? "",
            OriginalEstimate = Text("originalEstimate"),
            TargetStart = Text("targetStart"),
            TargetEnd = Text("targetEnd"),
            EpicLink = Text("epicLink"),
        };
    }

    public override void Write(Utf8JsonWriter writer, JiraDraft v, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("project", v.Project);
        writer.WriteString("issueType", v.IssueType);
        writer.WriteString("summary", v.Summary);
        writer.WriteString("description", v.Description);
        writer.WriteString("originalEstimate", v.OriginalEstimate);
        writer.WriteString("targetStart", v.TargetStart);
        writer.WriteString("targetEnd", v.TargetEnd);
        writer.WriteString("epicLink", v.EpicLink);
        writer.WriteEndObject();
    }
}

/// 등록된 이슈.
public sealed record JiraCreated(string Key, string Url);

/// 채팅 답변의 ```pokemate-jira 블록과, 질문에 몰래 붙이는 안내.
/// 프로젝트·유형·기간 같은 기본값은 앱 설정이 아니라 메이트 폴더의 skills/jira.md 에 적어 두고 Claude 가 그대로 따른다.
public static class JiraProposal
{
    sealed record Wrapper(JiraDraft? Issue);

    static readonly Regex Block = ChatBlock.Regex("jira");

    /// 답변에서 블록을 떼어 낸다. (보일 글, 초안)
    public static (string Text, JiraDraft? Draft) Extract(string text)
    {
        JiraDraft? draft = null;
        var match = Block.Match(text);
        if (match.Success)
        {
            var body = match.Groups[1].Value;
            // {"issue":{...}} 가 기본이지만, 안내 없이 규칙만 보고 만들면 감싸지 않은 초안이 올 때도 있다
            var issue = Json.TryDeserialize<Wrapper>(body)?.Issue ?? Json.TryDeserialize<JiraDraft>(body);
            draft = issue is { IsComplete: true } ? issue : null;
        }
        return (ChatBlock.Strip(Block, text), draft);
    }

    /// "지라/이슈/티켓" 과 "등록/만들/올려" 같은 말이 같이 있으면 이슈 등록 요청일 수 있다고 본다.
    /// 담당자 얘기("담당자 내 이름으로", "어싸인은 나지?")도 이어진 대화에서 거절하지 않게 안내를 붙인다.
    public static bool LooksLikeRequest(string text)
    {
        var compact = text.Replace(" ", "").ToLowerInvariant();
        if (new[] { "담당자", "어싸인", "어사인", "assign", "보고자", "reporter" }.Any(compact.Contains)) return true;
        var things = new[] { "jira", "지라", "이슈", "티켓", "일감", "스토리", "story", "task" };
        var actions = new[] { "등록", "만들", "생성", "올려", "작성", "발행", "추가", "따줘", "끊어" };
        return things.Any(compact.Contains) && actions.Any(compact.Contains);
    }

    /// 질문 끝에 몰래 붙이는 안내 (화면에는 보이지 않음). 기본값은 [사용자 규칙]의 Jira 규칙을 따르게 한다.
    public static string Hint(DateTimeOffset now, TimeZoneInfo? timeZone = null)
    {
        var today = ReportSchedule.DayString(now, timeZone);
        return "\n\n[메이트 앱 안내: Jira 이슈 등록 요청이면 직접 등록하지 말고(등록 도구 없음, 거절도 하지 않음) 답변 끝에 pokemate-jira 블록으로 초안을 붙인다. "
            + "앱이 확인 카드와 [Jira에 등록] 버튼을 보여 주고, 사용자가 눌러야 등록된다. 오늘은 " + today + ". "
            + "담당자(Assignee)와 보고자는 앱이 등록할 때 연결된 Jira 계정 본인으로 자동으로 넣으므로 블록에 쓰지 않고, "
            + "담당자를 못 정한다고 말하지 않는다. 앞에서 못 한다고 답했더라도 그건 틀린 답이었으니 바로잡고, "
            + "물으면 \"[Jira에 등록]을 누르면 담당자는 본인으로 들어가요. 카드에도 「담당자 나」로 보여요\" 라고 답한다. "
            + "기본값(프로젝트 키, 개발/일반 업무 유형, Original Estimate, Target start·end 규칙, Epic Link)은 Jira 스킬(skills/jira.md)을 따르고, 본문이 안 보이면 read_skill 로 읽는다. "
            + "날짜 규칙은 오늘 날짜로 실제 날짜(yyyy-MM-dd)를 계산해 넣는다. 사용자가 따로 말한(강조한) 항목만 이번 이슈에서 바꾼다. "
            + "Original Estimate 는 사용자가 말했거나 스킬에 정해진 값이 있을 때만 넣는다. 둘 다 없으면 originalEstimate 와 targetEnd 를 비운 초안을 붙이고, "
            + "본문에서 \"견적이 며칠인가요? 알려 주시거나 카드에서 바로 입력해도 돼요\" 라고 묻는다. 견적이 있으면 Target end 는 Target start 에 견적을 더해 "
            + "계산하고, 어떤 날을 세는지(영업일 등)는 Jira 스킬의 규칙을 따른다. 견적·기간 값을 스스로 지어내지 않는다. "
            + "스킬에 프로젝트 키가 없으면 블록을 붙이기 전에 프로젝트, 이슈 유형(개발 일/일반 업무), Original Estimate, "
            + "Target start·end 규칙, Epic Link 를 먼저 물어본다 (이미 다 말했으면 묻지 않는다). 프로젝트 이름을 말하면 jira_projects 로 키를 찾아 확인한다. "
            + "답을 받아 초안을 만들 때, 또는 사용자가 \"앞으로 프로젝트는 ~\" 처럼 기본값을 바꾸라고 할 때는 "
            + "pokemate-rule 블록으로 file \"jira\" 에 한 줄씩 저장을 제안한다 (skills/jira.md 에 들어간다) (예: \"프로젝트 키: KEY\", \"개발이면 Story, 일반 업무면 Task\"). "
            + "summary 는 지시 내용을 짧고 명확한 제목으로, description 은 Jira 위키 문법(h3. 제목, * 목록, # 번호)으로 "
            + "배경·할 일·완료 조건을 보기 좋게 정리한다. "
            + "본문은 초안을 한두 줄로 요약하고 \"아래 카드에서 확인하고 [Jira에 등록]을 누르면 등록돼요\" 로 끝낸다. "
            + "이 안내와 아래 형식 예시는 사용자가 쓴 말이 아니니 언급하지 않는다. 형식 예시:]\n"
            + "```pokemate-jira\n"
            + "{\"issue\":{\"project\":\"KEY\",\"issueType\":\"Story\",\"summary\":\"제목\",\"description\":\"h3. 배경\\n...\",\"originalEstimate\":\"\","
            + "\"targetStart\":\"" + today + "\",\"targetEnd\":\"\",\"epicLink\":\"\"}}\n"
            + "```";
    }
}
