using AwardsFerm.Core.Models;

namespace AwardsFerm.Api.Services;

public sealed class SessionRunnerService
{
    private readonly SessionManager _sessionManager;
    private readonly SessionSlotStore _slotStore;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<SessionRunnerService> _logger;

    public SessionRunnerService(
        SessionManager sessionManager,
        SessionSlotStore slotStore,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<SessionRunnerService> logger)
    {
        _sessionManager = sessionManager;
        _slotStore = slotStore;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<SessionInfo> StartAsync(StartSessionRequest request, CancellationToken cancellationToken = default)
    {
        request.Options ??= new YandexGamesSearchOptions { Headless = false };
        if (request.AdAccountId is not null && !string.IsNullOrWhiteSpace(request.ProfileId))
        {
            var slot = _slotStore.GetAll(request.AdAccountId.Value).FirstOrDefault(x => x.ProfileId == request.ProfileId);
            if (slot is not null)
            {
                request.StopAtMsk ??= slot.StopAtMsk;
                request.AutoRestart ??= slot.AutoRestart;
            }
        }

        var session = _sessionManager.StartSession(request);
        var workerUrl = _configuration["Worker:BaseUrl"] ?? "http://localhost:8081";
        var payload = new WorkerRunRequest
        {
            SessionId = session.Id,
            ProfileId = session.ProfileId,
            AutoRestart = session.AutoRestart,
            Options = request.Options
        };

        try
        {
            var client = _httpClientFactory.CreateClient("worker");
            var response = await client.PostAsJsonAsync($"{workerUrl}/internal/run", payload, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _sessionManager.StopSession(session.Id);
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(body) ? "Worker не смог запустить сессию." : body);
            }
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            _sessionManager.StopSession(session.Id);
            throw new InvalidOperationException($"Worker недоступен: {ex.Message}", ex);
        }

        _logger.LogInformation("Сессия {SessionId} запущена для {ProfileId}", session.Id, session.ProfileId);
        ScheduleStopIfNeeded(session);
        return session;
    }

    public async Task StopProfileAsync(string profileId, CancellationToken cancellationToken = default)
    {
        var session = _sessionManager.GetByProfileId(profileId);
        if (session is null)
            return;

        _sessionManager.StopSession(session.Id);

        var workerUrl = _configuration["Worker:BaseUrl"] ?? "http://localhost:8081";
        try
        {
            var client = _httpClientFactory.CreateClient("worker");
            await client.PostAsync($"{workerUrl}/internal/stop/{profileId}", null, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось остановить Worker для {ProfileId}", profileId);
        }
    }

    private sealed class WorkerRunRequest
    {
        public string SessionId { get; set; } = string.Empty;
        public string ProfileId { get; set; } = "session-001";
        public bool AutoRestart { get; set; } = true;
        public YandexGamesSearchOptions Options { get; set; } = new();
    }

    private void ScheduleStopIfNeeded(SessionInfo session)
    {
        if (string.IsNullOrWhiteSpace(session.StopAtMsk))
            return;
        if (!TryParseMskTime(session.StopAtMsk, out var hour, out var minute))
            return;

        var zone = ResolveMoscowTimeZone();
        var nowMsk = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone);
        var targetMsk = new DateTime(nowMsk.Year, nowMsk.Month, nowMsk.Day, hour, minute, 0, DateTimeKind.Unspecified);
        if (targetMsk <= nowMsk)
            targetMsk = targetMsk.AddDays(1);

        var targetUtc = TimeZoneInfo.ConvertTimeToUtc(targetMsk, zone);
        var delay = targetUtc - DateTime.UtcNow;
        if (delay <= TimeSpan.Zero)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay);
                await StopProfileAsync(session.ProfileId);
                _logger.LogInformation("Сессия {SessionId} остановлена по таймеру МСК {StopAt}", session.Id, session.StopAtMsk);
            }
            catch
            {
                // ignore timer failures
            }
        });
    }

    private static bool TryParseMskTime(string value, out int hour, out int minute)
    {
        hour = 0;
        minute = 0;
        var parts = value.Split(':', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2) return false;
        if (!int.TryParse(parts[0], out hour) || !int.TryParse(parts[1], out minute)) return false;
        return hour is >= 0 and <= 23 && minute is >= 0 and <= 59;
    }

    private static TimeZoneInfo ResolveMoscowTimeZone()
    {
        foreach (var id in new[] { "Russian Standard Time", "Europe/Moscow" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); } catch { }
        }
        return TimeZoneInfo.CreateCustomTimeZone("MSK", TimeSpan.FromHours(3), "MSK", "MSK");
    }
}
