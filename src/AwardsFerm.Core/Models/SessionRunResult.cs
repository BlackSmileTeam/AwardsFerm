namespace AwardsFerm.Core.Models;

public sealed class SessionRunResult
{
    public bool AutoRestartAfterGameOvers { get; init; }
    public bool AutoRestartAfterIpChange { get; init; }
    public bool AutoRestartAfterDiagnostic { get; init; }
    public bool BrowserClosedUnexpectedly { get; init; }
    public int GameOverCount { get; init; }
    public string? PreviousIp { get; init; }
    public string? NewIp { get; init; }

    public bool ShouldAutoRestart =>
        AutoRestartAfterGameOvers || AutoRestartAfterIpChange || AutoRestartAfterDiagnostic ||
        BrowserClosedUnexpectedly;

    public static SessionRunResult Completed(int gameOverCount = 0) => new()
    {
        AutoRestartAfterGameOvers = false,
        BrowserClosedUnexpectedly = false,
        GameOverCount = gameOverCount
    };

    public static SessionRunResult RestartAfterGameOvers(int gameOverCount) => new()
    {
        AutoRestartAfterGameOvers = true,
        BrowserClosedUnexpectedly = false,
        GameOverCount = gameOverCount
    };

    public static SessionRunResult BrowserClosed(int gameOverCount = 0) => new()
    {
        AutoRestartAfterGameOvers = false,
        BrowserClosedUnexpectedly = true,
        GameOverCount = gameOverCount
    };

    public static SessionRunResult RestartAfterIpChange(
        int gameOverCount = 0,
        string? previousIp = null,
        string? newIp = null) => new()
    {
        AutoRestartAfterIpChange = true,
        GameOverCount = gameOverCount,
        PreviousIp = previousIp,
        NewIp = newIp
    };

    public static SessionRunResult RestartAfterDiagnostic(int gameOverCount = 0, string? reason = null) => new()
    {
        AutoRestartAfterDiagnostic = true,
        GameOverCount = gameOverCount,
        DiagnosticReason = reason
    };

    public string? DiagnosticReason { get; init; }
}
