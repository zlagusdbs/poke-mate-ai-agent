using System.Text.RegularExpressions;

namespace PokeMate.Core;

/// 답변에 들어 있는 MCP 도구 오류 코드. 화면에서 [다시 연결] 같은 버튼을 띄우는 데 쓴다.
public abstract record ToolErrorCode(string System)
{
    public sealed record AuthExpired(string System) : ToolErrorCode(System);
    public sealed record NetworkUnreachable(string System) : ToolErrorCode(System);
    public sealed record NotConnected(string System) : ToolErrorCode(System);

    static readonly Regex Pattern = new("(AUTH_EXPIRED|NETWORK_UNREACHABLE|NOT_CONNECTED):([a-z0-9_-]+)");

    public static List<ToolErrorCode> Detect(string text)
    {
        var found = new List<ToolErrorCode>();
        foreach (Match match in Pattern.Matches(text))
        {
            var system = match.Groups[2].Value;
            ToolErrorCode code = match.Groups[1].Value switch
            {
                "AUTH_EXPIRED" => new AuthExpired(system),
                "NETWORK_UNREACHABLE" => new NetworkUnreachable(system),
                _ => new NotConnected(system),
            };
            if (!found.Contains(code)) found.Add(code);
        }
        return found;
    }
}
