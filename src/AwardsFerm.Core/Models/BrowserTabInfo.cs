namespace AwardsFerm.Core.Models;

public sealed class BrowserTabInfo
{
    public int Index { get; init; }
    public string Url { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public bool IsActive { get; init; }
    public bool IsCaptcha { get; init; }
}
