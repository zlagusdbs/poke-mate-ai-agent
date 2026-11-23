using PokeMate.Core;

namespace PokeMate.CoreChecks;

// MARK: ConnectorStatus
public static class ConnectorStatusChecks
{
    public static void Run()
    {
        const string ok = "claude.ai Google Docs: https://x - ✔ Connected\r\nclaude.ai Microsoft 365: https://microsoft365.mcp.claude.com/mcp - ✔ Connected";
        const string need = "claude.ai Microsoft 365: https://microsoft365.mcp.claude.com/mcp - ! Needs authentication";
        Check.That(ConnectorStatus.Parse(ok, "Microsoft 365") == ConnectionState.Connected, "connector: 연결됨");
        Check.That(ConnectorStatus.Parse(need, "Microsoft 365") == ConnectionState.Disconnected, "connector: 인증 필요");
        Check.That(ConnectorStatus.Parse("", "Microsoft 365") == ConnectionState.Disconnected, "connector: 목록에 없음");
    }
}
