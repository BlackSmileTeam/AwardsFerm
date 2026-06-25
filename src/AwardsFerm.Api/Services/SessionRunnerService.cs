using System.Net;
using System.Text;
using AwardsFerm.Core.Models;

namespace AwardsFerm.Api.Services;

public sealed class SessionRunnerService
{
    private readonly SessionManager _sessionManager;
    private readonly SessionSlotStore _slotStore;
    private readonly ProxyStore _proxyStore;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<SessionRunnerService> _logger;

    public SessionRunnerService(
        SessionManager sessionManager,
        SessionSlotStore slotStore,
        ProxyStore proxyStore,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<SessionRunnerService> logger)
    {
        _sessionManager = sessionManager;
        _slotStore = slotStore;
        _proxyStore = proxyStore;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<SessionInfo> StartAsync(StartSessionRequest request, CancellationToken cancellationToken = default)
    {
        request.Options ??= new YandexGamesSearchOptions { Headless = false };
        ApplySlotSettings(request);

        var profileId = string.IsNullOrWhiteSpace(request.ProfileId) ? "session-001" : request.ProfileId.Trim();

        if (!await IsWorkerHealthyAsync(cancellationToken))
        {
            throw new InvalidOperationException(
                "Worker не запущен. Сначала выполните в отдельном терминале: " +
                "dotnet run --project src\\AwardsFerm.Worker\\AwardsFerm.Worker.csproj");
        }

        await StopProfileAsync(profileId, cancellationToken);

        var sessionId = Guid.NewGuid().ToString("N");
        var workerUrl = GetWorkerBaseUrl();
        var payload = new WorkerRunRequest
        {
            SessionId = sessionId,
            ProfileId = profileId,
            AutoRestart = request.AutoRestart ?? true,
            Options = request.Options
        };

        try
        {
            var client = _httpClientFactory.CreateClient("worker");
            var response = await client.PostAsJsonAsync($"{workerUrl}/internal/run", payload, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(body) ? "Worker не смог запустить сессию." : body);
            }
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException($"Worker недоступен: {ex.Message}", ex);
        }

        SessionInfo session;
        try
        {
            session = _sessionManager.StartSession(request, sessionId);
        }
        catch
        {
            await EnsureWorkerStoppedAsync(profileId, useLongTimeout: true);
            throw;
        }

        _logger.LogInformation("Сессия {SessionId} запущена для {ProfileId}", session.Id, session.ProfileId);
        ScheduleStopIfNeeded(session);
        return session;
    }

    public async Task StopProfileAsync(string profileId, CancellationToken cancellationToken = default)
    {
        var session = _sessionManager.GetByProfileId(profileId);
        if (session is not null)
            _sessionManager.StopSession(session.Id);
        else
            _sessionManager.StopByProfileId(profileId);

        if (await IsWorkerHealthyAsync(cancellationToken))
            await EnsureWorkerStoppedAsync(profileId, useLongTimeout: false, cancellationToken);
    }

    public async Task PauseProfileAsync(string profileId, CancellationToken cancellationToken = default)
    {
        if (!await IsWorkerHealthyAsync(cancellationToken))
            throw new InvalidOperationException("Worker не запущен.");

        var client = _httpClientFactory.CreateClient("worker-quick");
        await client.PostAsync($"{GetWorkerBaseUrl()}/internal/pause/{profileId}", null, cancellationToken);

        var session = _sessionManager.GetByProfileId(profileId);
        if (session is not null)
            _sessionManager.PauseSession(profileId);
    }

    public async Task ResumeProfileAsync(string profileId, CancellationToken cancellationToken = default)
    {
        if (!await IsWorkerHealthyAsync(cancellationToken))
            throw new InvalidOperationException("Worker не запущен.");

        var client = _httpClientFactory.CreateClient("worker-quick");
        await client.PostAsync($"{GetWorkerBaseUrl()}/internal/resume/{profileId}", null, cancellationToken);

        var session = _sessionManager.GetByProfileId(profileId);
        if (session is not null)
            _sessionManager.ResumeSession(profileId);
    }

    public async Task SetPreviewAsync(string profileId, bool enabled, CancellationToken cancellationToken = default)
    {
        if (!await IsWorkerHealthyAsync(cancellationToken))
            return;

        try
        {
            var client = _httpClientFactory.CreateClient("worker-quick");
            await client.PostAsJsonAsync(
                $"{GetWorkerBaseUrl()}/internal/preview/{profileId}",
                new { enabled },
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось переключить просмотр для {ProfileId}", profileId);
        }
    }

    public async Task PreviewClickAsync(
        string profileId,
        double xRatio,
        double yRatio,
        CancellationToken cancellationToken = default)
    {
        if (!await IsWorkerHealthyAsync(cancellationToken))
            throw new InvalidOperationException("Worker не запущен.");

        var client = _httpClientFactory.CreateClient("worker");
        var response = await client.PostAsJsonAsync(
            $"{GetWorkerBaseUrl()}/internal/preview/{profileId}/click",
            new { xRatio, yRatio },
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(body) ? "Не удалось отправить клик в браузер." : body);
        }
    }

    public async Task PreviewReloadAsync(string profileId, CancellationToken cancellationToken = default)
    {
        if (!await IsWorkerHealthyAsync(cancellationToken))
            throw new InvalidOperationException("Worker не запущен.");

        var client = _httpClientFactory.CreateClient("worker");
        var response = await client.PostAsync(
            $"{GetWorkerBaseUrl()}/internal/preview/{profileId}/reload",
            null,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(body) ? "Не удалось обновить страницу." : body);
        }
    }

    public async Task PreviewCloseCaptchaTabAsync(string profileId, CancellationToken cancellationToken = default)
    {
        if (!await IsWorkerHealthyAsync(cancellationToken))
            throw new InvalidOperationException("Worker не запущен.");

        var client = _httpClientFactory.CreateClient("worker");
        var response = await client.PostAsync(
            $"{GetWorkerBaseUrl()}/internal/preview/{profileId}/close-captcha-tab",
            null,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(body) ? "Не удалось закрыть вкладку капчи." : body);
        }
    }

    public async Task PreviewReloadTabAsync(
        string profileId,
        int index,
        CancellationToken cancellationToken = default)
    {
        if (!await IsWorkerHealthyAsync(cancellationToken))
            throw new InvalidOperationException("Worker не запущен.");

        var client = _httpClientFactory.CreateClient("worker");
        var response = await client.PostAsync(
            $"{GetWorkerBaseUrl()}/internal/preview/{profileId}/tabs/{index}/reload",
            null,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(body) ? "Не удалось обновить вкладку." : body);
        }
    }

    public async Task<IReadOnlyList<BrowserTabInfo>> ListBrowserTabsAsync(
        string profileId,
        CancellationToken cancellationToken = default)
    {
        if (!await IsWorkerHealthyAsync(cancellationToken))
            throw new InvalidOperationException("Worker не запущен.");

        var client = _httpClientFactory.CreateClient("worker");
        var response = await client.GetAsync(
            $"{GetWorkerBaseUrl()}/internal/preview/{profileId}/tabs",
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(body) ? "Не удалось получить список вкладок." : body);
        }

        return await response.Content.ReadFromJsonAsync<List<BrowserTabInfo>>(cancellationToken)
               ?? [];
    }

    public async Task CloseBrowserTabAsync(
        string profileId,
        int index,
        CancellationToken cancellationToken = default)
    {
        if (!await IsWorkerHealthyAsync(cancellationToken))
            throw new InvalidOperationException("Worker не запущен.");

        var client = _httpClientFactory.CreateClient("worker");
        var response = await client.DeleteAsync(
            $"{GetWorkerBaseUrl()}/internal/preview/{profileId}/tabs/{index}",
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(body) ? "Не удалось закрыть вкладку." : body);
        }
    }

    public async Task<string?> GetPreviewFrameAsync(string profileId, CancellationToken cancellationToken = default)
    {
        if (!await IsWorkerHealthyAsync(cancellationToken))
            return null;

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));

            var client = _httpClientFactory.CreateClient("worker-quick");
            var response = await client.GetAsync(
                $"{GetWorkerBaseUrl()}/internal/preview/{profileId}/frame",
                timeoutCts.Token);
            if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
                return null;
            if (!response.IsSuccessStatusCode)
                return null;

            var payload = await response.Content.ReadFromJsonAsync<PreviewFrameResponse>(cancellationToken);
            return payload?.ImageBase64;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Не удалось получить кадр просмотра для {ProfileId}", profileId);
            return null;
        }
    }

    public async Task<byte[]?> GetSessionLogFileAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return null;

        if (!await IsWorkerHealthyAsync(cancellationToken))
            return null;

        try
        {
            var client = _httpClientFactory.CreateClient("worker");
            var response = await client.GetAsync(
                $"{GetWorkerBaseUrl()}/internal/sessions/{Uri.EscapeDataString(sessionId)}/log",
                cancellationToken);

            if (response.StatusCode == HttpStatusCode.NotFound)
                return null;

            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsByteArrayAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Не удалось получить файл лога сессии {SessionId}", sessionId);
            return null;
        }
    }

    public static byte[] BuildLogFile(SessionInfo session)
    {
        var builder = new StringBuilder();
        foreach (var line in session.Logs)
            builder.AppendLine(line);

        if (session.DiagnosticLogs.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("=== Диагностика ===");
            builder.AppendLine();
            for (var i = 0; i < session.DiagnosticLogs.Count; i++)
            {
                if (i > 0)
                    builder.AppendLine();
                builder.Append(session.DiagnosticLogs[i]);
            }
        }

        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    private void ApplySlotSettings(StartSessionRequest request)
    {
        if (request.AdAccountId is null || string.IsNullOrWhiteSpace(request.ProfileId))
            return;

        var slot = _slotStore.GetAll(request.AdAccountId.Value)
            .FirstOrDefault(x => x.ProfileId == request.ProfileId);
        if (slot is null)
            return;

        request.StopAtMsk ??= slot.StopAtMsk;
        request.AutoRestart ??= slot.AutoRestart;
        request.Options ??= new YandexGamesSearchOptions { Headless = false };
        request.Options.UseProxy = slot.ProxyEnabled;
        request.Options.DevicePlatform = slot.DevicePlatform;

        if (!slot.ProxyEnabled)
        {
            request.Options.ProxyUrl = null;
            return;
        }

        if (slot.ProxyId is null)
            return;

        var userId = _proxyStore.GetUserIdForAccount(request.AdAccountId.Value);
        if (userId is null)
            return;

        request.Options.ProxyUrl = _proxyStore.BuildProxyUrl(userId.Value, slot.ProxyId.Value);
    }

    private string GetWorkerBaseUrl() =>
        _configuration["Worker:BaseUrl"] ?? "http://localhost:8081";

    private async Task<bool> IsWorkerHealthyAsync(CancellationToken cancellationToken)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("worker-quick");
            var response = await client.GetAsync($"{GetWorkerBaseUrl()}/health", cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Worker health check failed");
            return false;
        }
    }

    private Task EnsureWorkerStoppedAsync(string profileId, bool useLongTimeout = false, CancellationToken cancellationToken = default)
    {
        var clientName = useLongTimeout ? "worker" : "worker-quick";
        return EnsureWorkerStoppedCoreAsync(profileId, clientName, cancellationToken);
    }

    private async Task EnsureWorkerStoppedCoreAsync(string profileId, string clientName, CancellationToken cancellationToken)
    {
        try
        {
            var client = _httpClientFactory.CreateClient(clientName);
            await client.PostAsync($"{GetWorkerBaseUrl()}/internal/stop/{profileId}", null, cancellationToken);
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

    private sealed class PreviewFrameResponse
    {
        public string? ImageBase64 { get; set; }
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
