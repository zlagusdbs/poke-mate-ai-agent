using PokeMate.Core;

namespace PokeMate.CoreChecks;

// MARK: Connections JSON (pokemate 출력 형식)
public static class ConnectionsJsonChecks
{
    public static void Run()
    {
        const string status = """[{"system":"jira","name":"Jira","group":"ad","status":"expired"},{"system":"wiki","name":"Wiki","group":"ad","status":"unavailable"}]""";
        var s = Json.TryDeserialize<List<SystemStatus>>(status);
        Check.That(s?.Select(x => x.Status).SequenceEqual(new[] { ConnectionState.Expired, ConnectionState.Unavailable }) == true && s[0].Id == "jira", "status: 디코딩");
        const string connect = """[{"system":"jira","status":"network_unreachable","message":"사내망 연결을 확인해 주세요"}]""";
        var c = Json.TryDeserialize<List<ConnectResult>>(connect);
        Check.That(c?.FirstOrDefault()?.Status == "network_unreachable", "connect: 디코딩");
        Check.That(new ConnectTarget.Group("ad").Arguments.SequenceEqual(new[] { "connect", "--group", "ad" })
                   && new ConnectTarget.System("jira").Arguments.SequenceEqual(new[] { "connect", "jira" }), "connect: 그룹 인자");
    }
}
