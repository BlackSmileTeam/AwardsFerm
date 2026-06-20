using AwardsFerm.Core.Models;

namespace AwardsFerm.Api.Services;

public sealed class SessionManager
{
    private readonly SessionSlotStore _slotStore;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly object _lock = new();
    private readonly Dictionary<string, SessionInfo> _sessions = new();
    private readonly Dictionary<string, CancellationTokenSource> _ctsBySession = new();
    private readonly Dictionary<string, string> _profileToSession = new();

    public SessionManager(SessionSlotStore slotStore, IServiceScopeFactory scopeFactory)
    {
        _slotStore = slotStore;
        _scopeFactory = scopeFactory;
    }

    public int MaxConcurrentSessions => Math.Max(1, _slotStore.Count);

    public IReadOnlyList<SessionInfo> GetAll()
    {
        lock (_lock)
        {
            return _sessions.Values.Select(Clone).ToList();
        }
    }

    public SessionInfo? GetById(string sessionId)
    {
        lock (_lock)
        {
            return _sessions.TryGetValue(sessionId, out var session) ? Clone(session) : null;
        }
    }

    public SessionInfo? GetByProfileId(string profileId)
    {
        lock (_lock)
        {
            if (!_profileToSession.TryGetValue(profileId, out var sessionId))
                return null;

            return _sessions.TryGetValue(sessionId, out var session) ? Clone(session) : null;
        }
    }

    public SessionInfo? GetCurrent()
    {
        lock (_lock)
        {
            var active = _sessions.Values
                .FirstOrDefault(s => s.Status is SessionStatus.Starting or SessionStatus.Running);
            return active is null ? null : Clone(active);
        }
    }

    public SessionInfo StartSession(StartSessionRequest request, string? sessionId = null)
    {
        var profileId = string.IsNullOrWhiteSpace(request.ProfileId) ? "session-001" : request.ProfileId.Trim();
        var adAccountId = request.AdAccountId;

        lock (_lock)
        {
            if (!_slotStore.Exists(profileId, adAccountId))
                throw new InvalidOperationException($"Слот {profileId} не настроен. Добавьте его в панели.");

            if (_profileToSession.TryGetValue(profileId, out var existingId)
                && _sessions.TryGetValue(existingId, out var existing)
                && existing.Status is SessionStatus.Starting or SessionStatus.Running or SessionStatus.Paused)
            {
                throw new InvalidOperationException($"Профиль {profileId} уже выполняется.");
            }

            // Устаревшая привязка после остановки — не блокирует новый старт.
            if (_profileToSession.TryGetValue(profileId, out var staleId)
                && _sessions.TryGetValue(staleId, out var stale)
                && stale.Status is not (SessionStatus.Starting or SessionStatus.Running))
            {
                _profileToSession.Remove(profileId);
            }

            var activeCount = _sessions.Values.Count(s => s.Status is SessionStatus.Starting or SessionStatus.Running or SessionStatus.Paused);
            if (activeCount >= MaxConcurrentSessions)
                throw new InvalidOperationException($"Достигнут лимит параллельных сессий ({MaxConcurrentSessions}).");

            var session = new SessionInfo
            {
                Id = string.IsNullOrWhiteSpace(sessionId) ? Guid.NewGuid().ToString("N") : sessionId,
                AdAccountId = adAccountId,
                ProfileId = profileId,
                AutoRestart = request.AutoRestart ?? true,
                StopAtMsk = request.StopAtMsk,
                Status = SessionStatus.Starting,
                StartedAt = DateTimeOffset.UtcNow,
                TotalSteps = 12
            };

            _sessions[session.Id] = session;
            _profileToSession[profileId] = session.Id;
            _ctsBySession[session.Id] = new CancellationTokenSource();

            return Clone(session);
        }
    }

    public CancellationToken GetCancellationToken(string sessionId)
    {
        lock (_lock)
        {
            return _ctsBySession.TryGetValue(sessionId, out var cts)
                ? cts.Token
                : CancellationToken.None;
        }
    }

    public void PrepareRestart(string sessionId)
    {
        lock (_lock)
        {
            if (!_sessions.TryGetValue(sessionId, out var session))
                return;

            session.Status = SessionStatus.Starting;
            session.CurrentStep = 0;
            session.CurrentStepName = string.Empty;
            session.ErrorMessage = null;
            session.FinishedAt = null;
            session.Logs.Add($"[{DateTimeOffset.UtcNow:HH:mm:ss}] Перезапуск сессии…");
        }
    }

    public void StopSession(string sessionId)
    {
        lock (_lock)
        {
            if (_ctsBySession.TryGetValue(sessionId, out var cts))
                cts.Cancel();

            if (_sessions.TryGetValue(sessionId, out var session))
            {
                session.Status = SessionStatus.Stopped;
                session.AutoRestart = false;
                session.FinishedAt = DateTimeOffset.UtcNow;
                _profileToSession.Remove(session.ProfileId);
            }
        }
    }

    public void PauseSession(string profileId)
    {
        lock (_lock)
        {
            if (!_profileToSession.TryGetValue(profileId, out var sessionId)
                || !_sessions.TryGetValue(sessionId, out var session))
                return;

            if (session.Status is not (SessionStatus.Starting or SessionStatus.Running))
                return;

            session.Status = SessionStatus.Paused;
            session.Logs.Add($"[{DateTimeOffset.UtcNow:HH:mm:ss}] Пауза");
        }
    }

    public void ResumeSession(string profileId)
    {
        lock (_lock)
        {
            if (!_profileToSession.TryGetValue(profileId, out var sessionId)
                || !_sessions.TryGetValue(sessionId, out var session))
                return;

            if (session.Status is not SessionStatus.Paused)
                return;

            session.Status = SessionStatus.Running;
            session.Logs.Add($"[{DateTimeOffset.UtcNow:HH:mm:ss}] Продолжение");
        }
    }

    public void StopByProfileId(string profileId)
    {
        lock (_lock)
        {
            if (_profileToSession.TryGetValue(profileId, out var sessionId))
                StopSession(sessionId);
        }
    }

    public void ApplyEvent(SessionEvent sessionEvent)
    {
        lock (_lock)
        {
            if (!_sessions.TryGetValue(sessionEvent.SessionId, out var session))
                return;

            switch (sessionEvent.Type)
            {
                case SessionEventType.Log when sessionEvent.Message is not null:
                    session.Logs.Add($"[{sessionEvent.Timestamp:HH:mm:ss}] {sessionEvent.Message}");
                    break;
                case SessionEventType.DiagnosticLog when sessionEvent.Message is not null:
                    session.DiagnosticLogs.Add($"[{sessionEvent.Timestamp:HH:mm:ss}]\n{sessionEvent.Message}");
                    session.Logs.Add($"[{sessionEvent.Timestamp:HH:mm:ss}] ⚠ Диагностика: см. отдельный лог");
                    break;
                case SessionEventType.StepChanged:
                    session.CurrentStep = sessionEvent.CurrentStep ?? session.CurrentStep;
                    session.TotalSteps = sessionEvent.TotalSteps ?? session.TotalSteps;
                    session.CurrentStepName = sessionEvent.StepName ?? session.CurrentStepName;
                    if (session.Status is SessionStatus.Starting)
                        session.Status = SessionStatus.Running;
                    if (sessionEvent.Message is not null)
                        session.Logs.Add($"[{sessionEvent.Timestamp:HH:mm:ss}] {sessionEvent.Message}");
                    break;
                case SessionEventType.StatusChanged when sessionEvent.Status is not null:
                    session.Status = sessionEvent.Status.Value;
                    if (sessionEvent.Message is not null)
                        session.Logs.Add($"[{sessionEvent.Timestamp:HH:mm:ss}] {sessionEvent.Message}");
                    break;
                case SessionEventType.IpDetected when !string.IsNullOrWhiteSpace(sessionEvent.PublicIp):
                    session.PublicIp = sessionEvent.PublicIp;
                    session.Logs.Add($"[{sessionEvent.Timestamp:HH:mm:ss}] {sessionEvent.Message ?? $"Текущий IP: {sessionEvent.PublicIp}"}");
                    SaveIpAudit(session, sessionEvent.PublicIp!);
                    break;
                case SessionEventType.TrafficUpdated when sessionEvent.TrafficBytes is >= 0:
                    session.TrafficBytes = sessionEvent.TrafficBytes.Value;
                    break;
                case SessionEventType.Completed:
                    if (!session.AutoRestart)
                    {
                        session.Status = SessionStatus.Completed;
                        session.FinishedAt = DateTimeOffset.UtcNow;
                    }
                    else
                    {
                        session.Logs.Add($"[{sessionEvent.Timestamp:HH:mm:ss}] Цикл завершён — ожидание перезапуска");
                    }
                    break;
                case SessionEventType.Failed:
                    if (!session.AutoRestart)
                    {
                        session.Status = SessionStatus.Failed;
                        session.ErrorMessage = sessionEvent.Message;
                        session.FinishedAt = DateTimeOffset.UtcNow;
                    }
                    else if (sessionEvent.Message is not null)
                    {
                        session.Logs.Add($"[{sessionEvent.Timestamp:HH:mm:ss}] {sessionEvent.Message}");
                    }
                    break;
            }
        }
    }

    public void ReleaseProfile(string profileId)
    {
        lock (_lock)
        {
            _profileToSession.Remove(profileId);
        }
    }

    private static SessionInfo Clone(SessionInfo source) => new()
    {
        Id = source.Id,
        AdAccountId = source.AdAccountId,
        ProfileId = source.ProfileId,
        StopAtMsk = source.StopAtMsk,
        AutoRestart = source.AutoRestart,
        Status = source.Status,
        CurrentStep = source.CurrentStep,
        TotalSteps = source.TotalSteps,
        CurrentStepName = source.CurrentStepName,
        ErrorMessage = source.ErrorMessage,
        PublicIp = source.PublicIp,
        TrafficBytes = source.TrafficBytes,
        StartedAt = source.StartedAt,
        FinishedAt = source.FinishedAt,
        Logs = [..source.Logs],
        DiagnosticLogs = [..source.DiagnosticLogs]
    };

    private void SaveIpAudit(SessionInfo session, string publicIp)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<Data.AppDbContext>();
            db.SessionIpAudits.Add(new Data.Entities.SessionIpAuditEntity
            {
                RuntimeSessionId = session.Id,
                AdAccountId = session.AdAccountId,
                ProfileId = session.ProfileId,
                PublicIp = publicIp
            });
            db.SaveChanges();
        }
        catch
        {
            // do not break runtime flow on audit write failures
        }
    }
}
