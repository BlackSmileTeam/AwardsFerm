using System.Text;
using AwardsFerm.Core.Interfaces;
using AwardsFerm.Core.Models;

namespace AwardsFerm.Worker.Services;

/// <summary>Дублирует события сессии в файл logs/sessions/{sessionId}.log для офлайн-диагностики.</summary>
public sealed class SessionFileEventReporter : ISessionEventReporter
{
    private readonly IConfiguration _configuration;
    private readonly object _writeLock = new();

    public SessionFileEventReporter(IConfiguration configuration) =>
        _configuration = configuration;

    public Task ReportAsync(SessionEvent sessionEvent, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionEvent.SessionId))
            return Task.CompletedTask;

        var path = SessionLogDirectory.GetLogPath(_configuration, sessionEvent.SessionId);
        var timestamp = sessionEvent.Timestamp == default
            ? DateTimeOffset.UtcNow
            : sessionEvent.Timestamp;

        var builder = new StringBuilder()
            .Append('[').Append(timestamp.ToLocalTime().ToString("HH:mm:ss.fff")).Append("] ")
            .Append(sessionEvent.Type);

        if (sessionEvent.Status is not null)
            builder.Append(" status=").Append(sessionEvent.Status);

        if (!string.IsNullOrWhiteSpace(sessionEvent.Message))
            builder.Append(": ").Append(sessionEvent.Message);

        lock (_writeLock)
        {
            File.AppendAllText(path, builder.ToString() + Environment.NewLine, Encoding.UTF8);
        }

        return Task.CompletedTask;
    }
}
