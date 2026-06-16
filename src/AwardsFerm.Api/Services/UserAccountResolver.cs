using AwardsFerm.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace AwardsFerm.Api.Services;

public sealed class UserAccountResolver
{
    private readonly AppDbContext _db;

    public UserAccountResolver(AppDbContext db)
    {
        _db = db;
    }

    public Task<bool> UserOwnsAccountAsync(long userId, long adAccountId, CancellationToken ct) =>
        _db.AdAccounts.AnyAsync(x => x.Id == adAccountId && x.UserId == userId, ct);

    public async Task<long?> ResolveAdAccountByProfileAsync(long userId, string profileId, CancellationToken ct)
    {
        return await _db.SessionSlots
            .Where(x => x.ProfileId == profileId && x.AdAccount.UserId == userId)
            .Select(x => (long?)x.AdAccountId)
            .FirstOrDefaultAsync(ct);
    }
}
