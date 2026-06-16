using AwardsFerm.Core.Models;

namespace AwardsFerm.Api.Services;

public sealed class ScheduledSessionService : BackgroundService
{
    private readonly SessionSlotStore _slotStore;
    private readonly SessionManager _sessionManager;
    private readonly SessionRunnerService _runner;
    private readonly ILogger<ScheduledSessionService> _logger;
    private readonly Dictionary<string, DateOnly> _lastTriggered = new();
    private readonly TimeZoneInfo _mskZone;

    public ScheduledSessionService(
        SessionSlotStore slotStore,
        SessionManager sessionManager,
        SessionRunnerService runner,
        ILogger<ScheduledSessionService> logger)
    {
        _slotStore = slotStore;
        _sessionManager = sessionManager;
        _runner = runner;
        _logger = logger;
        _mskZone = ResolveMoscowTimeZone();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckSchedulesAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Ошибка проверки расписания сессий");
            }

            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }

    private async Task CheckSchedulesAsync(CancellationToken cancellationToken)
    {
        var mskNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _mskZone);
        var timeKey = mskNow.ToString("HH:mm");
        var today = DateOnly.FromDateTime(mskNow);

        foreach (var slot in _slotStore.GetAll())
        {
            if (!slot.ScheduleEnabled || string.IsNullOrWhiteSpace(slot.ScheduledStartMsk))
                continue;

            if (!string.Equals(slot.ScheduledStartMsk, timeKey, StringComparison.Ordinal))
                continue;

            if (_lastTriggered.TryGetValue(slot.ProfileId, out var last) && last == today)
                continue;

            var active = _sessionManager.GetByProfileId(slot.ProfileId);
            if (active?.Status is SessionStatus.Starting or SessionStatus.Running)
                continue;

            try
            {
                await _runner.StartAsync(new StartSessionRequest
                {
                    AdAccountId = slot.AdAccountId,
                    ProfileId = slot.ProfileId,
                    StopAtMsk = slot.StopAtMsk,
                    AutoRestart = slot.AutoRestart,
                    Options = new YandexGamesSearchOptions { Headless = false }
                }, cancellationToken);

                _lastTriggered[slot.ProfileId] = today;
                _logger.LogInformation(
                    "Автозапуск по расписанию (МСК {Time}): {ProfileId}",
                    timeKey,
                    slot.ProfileId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Не удалось автозапустить {ProfileId}", slot.ProfileId);
            }
        }
    }

    private static TimeZoneInfo ResolveMoscowTimeZone()
    {
        foreach (var id in new[] { "Russian Standard Time", "Europe/Moscow" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (TimeZoneNotFoundException)
            {
                // try next
            }
        }

        return TimeZoneInfo.CreateCustomTimeZone("MSK", TimeSpan.FromHours(3), "MSK", "MSK");
    }
}
