using System.Text.Json.Serialization;

namespace PokeMate.Core;

/// 연결 상태. 맥의 `SystemStatus.Status` (C# 에서는 속성 이름과 겹쳐 따로 둔다). JSON 값은 소문자 그대로.
/// External: claude.ai 커넥터처럼 앱이 따로 확인하는 것 (Microsoft 365)
public enum ConnectionState { Connected, Disconnected, Expired, Unavailable, External }

/// `pokemate status` 출력 한 줄.
public sealed record SystemStatus(string System, string Name, string Group, ConnectionState Status,
                                  string? Auth = null, string? BaseUrl = null, string? Company = null)
{
    // Auth: password(사내 아이디·비밀번호) / gh(GitHub CLI 로그인). Company: 연결 화면에서 묶어 보일 회사 이름 (connections 파일의 company)
    [JsonIgnore] public string Id => System;
}

/// `pokemate connect` 출력 한 줄. status: connected / auth_failed / network_unreachable / unavailable
public sealed record ConnectResult(string System, string Status, string Message);

public abstract record ConnectTarget
{
    public sealed record System(string Id) : ConnectTarget;
    public sealed record Group(string Name) : ConnectTarget;

    public IReadOnlyList<string> Arguments => this switch
    {
        System s => new[] { "connect", s.Id },
        Group g => new[] { "connect", "--group", g.Name },
        _ => Array.Empty<string>(),
    };
}

/// `pokemate github review-requests` 출력 한 줄.
public sealed record PullRequest(int Id, string Repo, int Number, string Title, string Author, string Url, string Updated);
