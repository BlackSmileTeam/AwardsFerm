using AwardsFerm.Api.Auth;
using AwardsFerm.Api.Data;
using AwardsFerm.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace AwardsFerm.Api.Services;

public sealed class UserProfitService
{
    private readonly AppDbContext _db;
    private readonly TokenEncryptionService _tokenEncryption;
    private readonly YandexRsyaStatisticsService _rsya;

    public UserProfitService(
        AppDbContext db,
        TokenEncryptionService tokenEncryption,
        YandexRsyaStatisticsService rsya)
    {
        _db = db;
        _tokenEncryption = tokenEncryption;
        _rsya = rsya;
    }

    public async Task<UserProfitSummaryDto> GetSummaryAsync(long userId, CancellationToken ct)
    {
        var accounts = await _db.AdAccounts
            .Where(x => x.UserId == userId)
            .OrderBy(x => x.CreatedAt)
            .ToListAsync(ct);

        var result = new List<AdAccountDto>(accounts.Count);
        decimal totalToday = 0;
        decimal totalMonth = 0;

        foreach (var account in accounts)
        {
            decimal todayReward = 0;
            decimal monthReward = 0;

            try
            {
                var token = _tokenEncryption.Decrypt(account.TokenEncrypted);
                var dashboard = await _rsya.GetDashboardForTokenAsync(token, ct);
                todayReward = dashboard.Today.Reward;
                monthReward = dashboard.ThisMonth.Reward;

                _db.RsyaSnapshots.Add(new Data.Entities.RsyaSnapshotEntity
                {
                    AdAccountId = account.Id,
                    TodayReward = dashboard.Today.Reward,
                    MonthReward = dashboard.ThisMonth.Reward,
                    TodayShows = dashboard.Today.Shows,
                    TodayClicks = dashboard.Today.Clicks
                });
            }
            catch
            {
                // ignore account fetch errors; keep others working
            }

            totalToday += todayReward;
            totalMonth += monthReward;
            result.Add(new AdAccountDto
            {
                Id = account.Id,
                Name = account.Name,
                GameTitle = account.GameTitle,
                GameUrl = account.GameUrl,
                TodayReward = todayReward,
                MonthReward = monthReward,
                CreatedAt = account.CreatedAt
            });
        }

        await _db.SaveChangesAsync(ct);

        return new UserProfitSummaryDto
        {
            TotalTodayReward = totalToday,
            TotalMonthReward = totalMonth,
            Accounts = result
        };
    }
}
