using System.Collections.Concurrent;
using AwardsFerm.Core.Interfaces;
using AwardsFerm.Core.Models;
using AwardsFerm.Infrastructure.Playwright;

namespace AwardsFerm.Worker.Services;

public sealed class SessionExecutionService
{
    private static readonly TimeSpan StopWaitTimeout = TimeSpan.FromSeconds(90);

    private readonly IBrowserSessionRunner _runner;
    private readonly IProfileRepository _profileRepository;
    private readonly ISessionPauseCoordinator _pauseCoordinator;
    private readonly ISessionPreviewCoordinator _previewCoordinator;
    private readonly SessionRemoteInputCoordinator _remoteInput;
    private readonly ILogger<SessionExecutionService> _logger;
    private readonly ConcurrentDictionary<string, ProfileExecution> _byProfile = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    public SessionExecutionService(
        IBrowserSessionRunner runner,
        IProfileRepository profileRepository,
        ISessionPauseCoordinator pauseCoordinator,
        ISessionPreviewCoordinator previewCoordinator,
        SessionRemoteInputCoordinator remoteInput,
        ILogger<SessionExecutionService> logger)
    {
        _runner = runner;
        _profileRepository = profileRepository;
        _pauseCoordinator = pauseCoordinator;
        _previewCoordinator = previewCoordinator;
        _remoteInput = remoteInput;
        _logger = logger;
    }

    public bool IsProfileRunning(string profileId)
    {
        if (!_byProfile.TryGetValue(profileId, out var entry))
            return false;

        if (entry.Task.IsCompleted)
        {
            _byProfile.TryRemove(profileId, out _);
            return false;
        }

        return true;
    }

    public async Task StartAsync(WorkerRunRequest request, CancellationToken cancellationToken = default)
    {
        var gate = _locks.GetOrAdd(request.ProfileId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            await StopExecutionAsync(request.ProfileId, cancellationToken);

            if (IsProfileRunning(request.ProfileId))
                throw new InvalidOperationException($"Профиль {request.ProfileId} уже выполняется.");

            var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var execution = new ProfileExecution
            {
                SessionId = request.SessionId,
                ProfileId = request.ProfileId,
                AutoRestart = request.AutoRestart,
                Cts = cts
            };

            execution.Task = Task.Run(() => RunProfileLoopAsync(execution, request.Options), cts.Token);
            _byProfile[request.ProfileId] = execution;
            _pauseCoordinator.Clear(request.ProfileId);
            _previewCoordinator.Clear(request.ProfileId);
            _remoteInput.Clear(request.ProfileId);

            _logger.LogInformation(
                "Profile {ProfileId}: принят запуск (session {SessionId})",
                request.ProfileId,
                request.SessionId);
        }
        finally
        {
            gate.Release();
        }
    }

    public Task StopAsync(string profileId, CancellationToken cancellationToken = default)
    {
        _pauseCoordinator.Clear(profileId);
        _previewCoordinator.Clear(profileId);
        _remoteInput.Clear(profileId);
        var gate = _locks.GetOrAdd(profileId, _ => new SemaphoreSlim(1, 1));
        return StopWithGateAsync(profileId, gate, cancellationToken);
    }

    public void Pause(string profileId)
    {
        _pauseCoordinator.Pause(profileId);
        _logger.LogInformation("Profile {ProfileId}: пауза", profileId);
    }

    public void Resume(string profileId)
    {
        _pauseCoordinator.Resume(profileId);
        _logger.LogInformation("Profile {ProfileId}: продолжение", profileId);
    }

    public void SetPreview(string profileId, bool enabled)
    {
        _previewCoordinator.SetEnabled(profileId, enabled);
        if (enabled)
            _previewCoordinator.RequestImmediateCapture(profileId);
        _logger.LogInformation("Profile {ProfileId}: просмотр {State}", profileId, enabled ? "вкл" : "выкл");
    }

    public string? GetPreviewFrame(string profileId) => _previewCoordinator.GetLastFrame(profileId);

    public async Task PreviewClickAsync(
        string profileId,
        double xRatio,
        double yRatio,
        CancellationToken cancellationToken = default)
    {
        if (!_previewCoordinator.IsEnabled(profileId))
            throw new InvalidOperationException("Включите «Просмотр», чтобы кликать по экрану браузера.");

        await _remoteInput.ClickAsync(profileId, xRatio, yRatio, cancellationToken);
        _logger.LogDebug(
            "Profile {ProfileId}: удалённый клик ({X:P0}, {Y:P0})",
            profileId,
            xRatio,
            yRatio);
    }

    private async Task StopWithGateAsync(string profileId, SemaphoreSlim gate, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            await StopExecutionAsync(profileId, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task StopExecutionAsync(string profileId, CancellationToken cancellationToken)
    {
        if (!_byProfile.TryGetValue(profileId, out var execution))
            return;

        if (execution.Task.IsCompleted)
        {
            _byProfile.TryRemove(profileId, out _);
            return;
        }

        execution.Cts.Cancel();
        try
        {
            await execution.Task.WaitAsync(StopWaitTimeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Profile {ProfileId}: остановка не завершилась за {Seconds} сек",
                profileId, StopWaitTimeout.TotalSeconds);
        }
        catch (OperationCanceledException)
        {
            // expected on cancel
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Profile {ProfileId}: ошибка при ожидании остановки", profileId);
        }
        finally
        {
            if (execution.Task.IsCompleted)
                _byProfile.TryRemove(profileId, out _);
        }
    }

    private async Task RunProfileLoopAsync(ProfileExecution execution, YandexGamesSearchOptions options)
    {
        var defaultRestartDelay = TimeSpan.FromSeconds(5);

        try
        {
            while (!execution.Cts.Token.IsCancellationRequested)
            {
                var restartDelay = defaultRestartDelay;
                try
                {
                    var profile = await _profileRepository.GetByIdAsync(execution.ProfileId, execution.Cts.Token)
                                    ?? await _profileRepository.GetDefaultAsync(execution.Cts.Token);

                    options.Headless = ResolveHeadless(options);

                    _logger.LogInformation(
                        "Profile {ProfileId}: запуск браузера (session {SessionId}, headless={Headless}, display={Display})",
                        execution.ProfileId,
                        execution.SessionId,
                        options.Headless,
                        Environment.GetEnvironmentVariable("DISPLAY") ?? "(none)");

                    var result = await _runner.RunYandexGamesSearchAsync(
                        execution.SessionId,
                        profile,
                        options,
                        execution.Cts.Token);

                    if (result.AutoRestartAfterIpChange)
                    {
                        _logger.LogInformation(
                            "Profile {ProfileId}: IP сменился {OldIp} → {NewIp} — перезапуск сессии",
                            execution.ProfileId,
                            result.PreviousIp ?? "?",
                            result.NewIp ?? "?");
                    }
                    else if (result.AutoRestartAfterGameOvers)
                    {
                        _logger.LogInformation(
                            "Profile {ProfileId}: {Games} игр — новая сессия с другим отпечатком и профилем браузера",
                            execution.ProfileId,
                            result.GameOverCount);
                    }
                    else if (result.AutoRestartAfterDiagnostic)
                    {
                        _logger.LogInformation(
                            "Profile {ProfileId}: диагностический перезапуск — {Reason}",
                            execution.ProfileId,
                            result.DiagnosticReason ?? "зависание");
                        restartDelay = TimeSpan.FromSeconds(2);
                    }
                    else if (result.BrowserClosedUnexpectedly)
                    {
                        restartDelay = TimeSpan.FromSeconds(1);
                    }
                }
                catch (OperationCanceledException) when (execution.Cts.Token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Profile {ProfileId}: сбой — перезапуск браузера", execution.ProfileId);
                }

                if (execution.Cts.Token.IsCancellationRequested)
                    break;

                if (!execution.AutoRestart)
                    break;

                _logger.LogInformation(
                    "Profile {ProfileId}: перезапуск браузера через {Seconds} сек",
                    execution.ProfileId,
                    restartDelay.TotalSeconds);

                try
                {
                    await Task.Delay(restartDelay, execution.Cts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        finally
        {
            _byProfile.TryRemove(execution.ProfileId, out _);
        }
    }

    private bool ResolveHeadless(YandexGamesSearchOptions options)
    {
        var envHeadless = Environment.GetEnvironmentVariable("BROWSER_HEADLESS");
        if (!string.IsNullOrEmpty(envHeadless) && bool.TryParse(envHeadless, out var fromEnv))
            return ResolveHeadlessForLinux(fromEnv);

        return ResolveHeadlessForLinux(options.Headless);
    }

    private static bool ResolveHeadlessForLinux(bool requestedHeadless)
    {
        if (!OperatingSystem.IsLinux() || !requestedHeadless)
            return requestedHeadless;

        var display = Environment.GetEnvironmentVariable("DISPLAY");
        return string.IsNullOrWhiteSpace(display);
    }

    private sealed class ProfileExecution
    {
        public required string SessionId { get; init; }
        public required string ProfileId { get; init; }
        public required bool AutoRestart { get; init; }
        public required CancellationTokenSource Cts { get; init; }
        public Task Task { get; set; } = Task.CompletedTask;
    }
}

public sealed class WorkerRunRequest
{
    public string SessionId { get; set; } = string.Empty;
    public string ProfileId { get; set; } = "session-001";
    public bool AutoRestart { get; set; } = true;
    public YandexGamesSearchOptions Options { get; set; } = new();
}
