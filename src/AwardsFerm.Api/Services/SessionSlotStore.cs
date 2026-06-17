using System.Text.Json;
using System.Text.RegularExpressions;
using AwardsFerm.Api.Data;
using AwardsFerm.Api.Data.Entities;
using AwardsFerm.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace AwardsFerm.Api.Services;

public sealed class SessionSlotStore
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly object _seedLock = new();
    private volatile bool _seeded;

    public SessionSlotStore(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public string ProfilesRoot => ProfilesPathHelper.FindProfilesRoot();

    public IReadOnlyList<SessionSlotDefinition> GetAll(long? adAccountId = null)
    {
        EnsureDefaults();
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var query = db.SessionSlots.AsNoTracking().AsQueryable();
        if (adAccountId.HasValue)
            query = query.Where(x => x.AdAccountId == adAccountId.Value);

        return query
            .OrderBy(x => x.ProfileId)
            .Select(x => new SessionSlotDefinition
            {
                Id = x.Id,
                AdAccountId = x.AdAccountId,
                ProfileId = x.ProfileId,
                Label = x.Label,
                ScheduleEnabled = x.ScheduleEnabled,
                ScheduledStartMsk = x.ScheduledStartMsk,
                StopAtMsk = x.StopAtMsk,
                AutoRestart = x.AutoRestart,
                ProxyEnabled = x.ProxyEnabled,
                ProxyId = x.ProxyId
            })
            .ToList();
    }

    public int Count => GetAll().Count;

    public SessionSlotDefinition Add(long adAccountId, string? label = null)
    {
        EnsureDefaults();
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var nextIndex = db.SessionSlots
            .AsEnumerable()
            .Select(s => ParseSessionNumber(s.ProfileId))
            .DefaultIfEmpty(0)
            .Max() + 1;

        var profileId = $"session-{nextIndex:D3}";
        var slot = new SessionSlotEntity
        {
            AdAccountId = adAccountId,
            ProfileId = profileId,
            Label = string.IsNullOrWhiteSpace(label) ? $"Сессия {nextIndex}" : label.Trim(),
            ScheduleEnabled = false
        };

        EnsureProfileDirectory(profileId, slot.Label, nextIndex - 1);
        db.SessionSlots.Add(slot);
        db.SaveChanges();

        return new SessionSlotDefinition
        {
            Id = slot.Id,
            AdAccountId = slot.AdAccountId,
            ProfileId = slot.ProfileId,
            Label = slot.Label,
            ScheduleEnabled = slot.ScheduleEnabled,
            ScheduledStartMsk = slot.ScheduledStartMsk,
            StopAtMsk = slot.StopAtMsk,
            AutoRestart = slot.AutoRestart,
            ProxyEnabled = slot.ProxyEnabled,
            ProxyId = slot.ProxyId
        };
    }

    public SessionSlotDefinition Update(long adAccountId, string profileId, UpdateSessionSlotRequest request)
    {
        EnsureDefaults();
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var slot = db.SessionSlots.FirstOrDefault(s => s.AdAccountId == adAccountId && s.ProfileId == profileId)
                   ?? throw new InvalidOperationException($"Слот {profileId} не найден.");

        if (!string.IsNullOrWhiteSpace(request.Label))
            slot.Label = request.Label.Trim();

        if (request.ScheduleEnabled.HasValue)
            slot.ScheduleEnabled = request.ScheduleEnabled.Value;

        if (request.ScheduledStartMsk is not null)
        {
            var normalized = NormalizeMskTime(request.ScheduledStartMsk);
            slot.ScheduledStartMsk = normalized;
        }

        if (request.StopAtMsk is not null)
            slot.StopAtMsk = NormalizeMskTime(request.StopAtMsk);

        if (request.AutoRestart.HasValue)
            slot.AutoRestart = request.AutoRestart.Value;

        if (request.ProxyEnabled.HasValue)
            slot.ProxyEnabled = request.ProxyEnabled.Value;

        if (request.ProxyId.HasValue)
            slot.ProxyId = request.ProxyId.Value > 0 ? request.ProxyId : null;
        else if (request.ProxyEnabled == false)
            slot.ProxyId = null;

        db.SaveChanges();

        return new SessionSlotDefinition
        {
            Id = slot.Id,
            AdAccountId = slot.AdAccountId,
            ProfileId = slot.ProfileId,
            Label = slot.Label,
            ScheduleEnabled = slot.ScheduleEnabled,
            ScheduledStartMsk = slot.ScheduledStartMsk,
            StopAtMsk = slot.StopAtMsk,
            AutoRestart = slot.AutoRestart,
            ProxyEnabled = slot.ProxyEnabled,
            ProxyId = slot.ProxyId
        };
    }

    public void Remove(long adAccountId, string profileId)
    {
        EnsureDefaults();
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var slot = db.SessionSlots.FirstOrDefault(s => s.AdAccountId == adAccountId && s.ProfileId == profileId)
                   ?? throw new InvalidOperationException($"Слот {profileId} не найден.");
        db.SessionSlots.Remove(slot);
        db.SaveChanges();
    }

    public bool Exists(string profileId, long? adAccountId = null) =>
        GetAll(adAccountId).Any(s => s.ProfileId == profileId);

    private void EnsureDefaults()
    {
        if (_seeded) return;
        lock (_seedLock)
        {
            if (_seeded) return;

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var firstAccount = db.AdAccounts.OrderBy(x => x.Id).FirstOrDefault();
            if (firstAccount is null)
                return;
            if (db.SessionSlots.Any())
            {
                _seeded = true;
                return;
            }

            var defaults = new[]
            {
                new SessionSlotEntity { AdAccountId = firstAccount.Id, ProfileId = "session-001", Label = "Сессия 1" },
                new SessionSlotEntity { AdAccountId = firstAccount.Id, ProfileId = "session-002", Label = "Сессия 2" }
            };

            for (var i = 0; i < defaults.Length; i++)
                EnsureProfileDirectory(defaults[i].ProfileId, defaults[i].Label, i);

            db.SessionSlots.AddRange(defaults);
            db.SaveChanges();
            _seeded = true;
        }
    }

    private void EnsureProfileDirectory(string profileId, string label, int cityIndex)
    {
        var profileDir = Path.Combine(ProfilesRoot, profileId);
        Directory.CreateDirectory(profileDir);

        var configPath = Path.Combine(profileDir, "config.json");
        if (File.Exists(configPath))
            return;

        var (lat, lon, cityLabel) = cityIndex switch
        {
            0 => (60.053085, 30.311729, "Санкт-Петербург, Россия"),
            1 => (55.7558, 37.6173, "Москва, Россия"),
            _ => (55.7558, 37.6173, "Москва, Россия")
        };

        var profile = new
        {
            id = profileId,
            name = $"Desktop Chrome Win10 — {label}",
            userAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/121.0.0.0 Safari/537.36",
            viewportWidth = 1920,
            viewportHeight = 1080,
            locale = "ru-RU",
            timezone = "Europe/Moscow",
            latitude = lat,
            longitude = lon,
            locationLabel = cityLabel,
            proxyUrl = (string?)null
        };

        File.WriteAllText(configPath, JsonSerializer.Serialize(profile, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        }));
    }

    private static int ParseSessionNumber(string profileId)
    {
        var match = Regex.Match(profileId, @"session-(\d+)", RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups[1].Value, out var n) ? n : 0;
    }

    private static string? NormalizeMskTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim();
        if (!Regex.IsMatch(trimmed, @"^\d{1,2}:\d{2}$"))
            throw new InvalidOperationException("Время должно быть в формате HH:mm (МСК).");

        var parts = trimmed.Split(':');
        var hours = int.Parse(parts[0]);
        var minutes = int.Parse(parts[1]);
        if (hours is < 0 or > 23 || minutes is < 0 or > 59)
            throw new InvalidOperationException("Некорректное время (МСК).");

        return $"{hours:D2}:{minutes:D2}";
    }

    private static SessionSlotDefinition Clone(SessionSlotDefinition source) => source;
}
