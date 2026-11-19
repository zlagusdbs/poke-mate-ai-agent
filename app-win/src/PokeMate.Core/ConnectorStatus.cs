namespace PokeMate.Core;

/// claude.ai 커넥터(예: Microsoft 365) 연결 상태. `claude mcp list` 출력으로 판단한다.
public static class ConnectorStatus
{
    public const string Microsoft365 = "Microsoft 365";
    public static readonly Uri SettingsUrl = new("https://claude.ai/settings/connectors");

    public static ConnectionState Parse(string output, string name)
    {
        var line = SwiftText.Lf(output).Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(l => l.Contains($"claude.ai {name}:"));
        if (line == null) return ConnectionState.Disconnected;
        return line.Contains("Connected") && !line.Contains("Needs authentication") ? ConnectionState.Connected : ConnectionState.Disconnected;
    }

    public static async Task<ConnectionState> Check(string name, AppSettings settings)
    {
        if (!File.Exists(settings.ClaudePath)) return ConnectionState.Disconnected;
        try
        {
            Directory.CreateDirectory(AppPaths.WorkDir);
            var output = await new ProcessRunner().Run(settings.ClaudePath, new[] { "mcp", "list" }, workDir: AppPaths.WorkDir,
                                                       environment: AppPaths.ProcessEnvironment(settings), timeout: TimeSpan.FromSeconds(60));
            return Parse(output.Stdout, name);
        }
        catch { return ConnectionState.Disconnected; }
    }
}
