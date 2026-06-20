namespace AwardsFerm.Core.Models;

public sealed class RsyaDashboard
{
    public bool Configured { get; init; }
    public string? Error { get; init; }
    public string Currency { get; init; } = "RUB";
    public string? ReportTitle { get; init; }
    public RsyaPeriodStats Today { get; init; } = new();
    public RsyaPeriodStats Yesterday { get; init; } = new();
    public RsyaPeriodStats ThisWeek { get; init; } = new();
    public RsyaPeriodStats ThisMonth { get; init; } = new();
    public IReadOnlyList<RsyaDailyPoint> DailyChart { get; init; } = [];
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;

    public static RsyaDashboard NotConfigured(string? message = null) => new()
    {
        Configured = false,
        Error = message ?? "Токен РСЯ не настроен. Укажите YandexRsya:OAuthToken или profiles/rsya-token.txt"
    };

    public static RsyaDashboard Failed(string message) => new()
    {
        Configured = true,
        Error = message
    };
}

public sealed class RsyaPeriodStats
{
    public decimal Reward { get; init; }
    public long Shows { get; init; }
    public long Clicks { get; init; }
    public long Hits { get; init; }
    public double? FillRate { get; init; }
}

public sealed class RsyaDailyPoint
{
    public string Date { get; init; } = string.Empty;
    public decimal Reward { get; init; }
    public long Shows { get; init; }
    public long Clicks { get; init; }
}
