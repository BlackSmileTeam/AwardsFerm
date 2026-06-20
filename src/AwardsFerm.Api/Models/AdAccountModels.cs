namespace AwardsFerm.Api.Models;

public sealed class AdAccountDto
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string GameTitle { get; set; } = string.Empty;
    public string GameUrl { get; set; } = string.Empty;
    public decimal TodayReward { get; set; }
    public decimal YesterdayReward { get; set; }
    public decimal WeekReward { get; set; }
    public decimal MonthReward { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class CreateAdAccountRequest
{
    public string Name { get; set; } = string.Empty;
    public string GameTitle { get; set; } = string.Empty;
    public string GameUrl { get; set; } = string.Empty;
    public string Token { get; set; } = string.Empty;
}

public sealed class UpdateAdAccountRequest
{
    public string? Name { get; set; }
    public string? GameTitle { get; set; }
    public string? GameUrl { get; set; }
    public string? Token { get; set; }
}

public sealed class UserProfitSummaryDto
{
    public decimal TotalTodayReward { get; set; }
    public decimal TotalYesterdayReward { get; set; }
    public decimal TotalWeekReward { get; set; }
    public decimal TotalMonthReward { get; set; }
    public IReadOnlyList<AdAccountDto> Accounts { get; set; } = [];
}
