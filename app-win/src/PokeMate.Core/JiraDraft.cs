namespace PokeMate.Core;

// Task 2 는 MateCLI.JiraCreate 가 쓰는 두 형식의 필드만 옮겼다.
// JiraDraft 의 너그러운 읽기(빠진 항목 허용·다듬기), IsComplete, PeriodText, JiraProposal 은 나중 작업이 채운다.

/// 채팅으로 만든 Jira 이슈 초안. 사용자가 확인 카드에서 [Jira에 등록]을 눌러야 `pokemate jira create` 로 등록된다.
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
}

/// 등록된 이슈.
public sealed record JiraCreated(string Key, string Url);
