using AwardsFerm.Core.Interfaces;
using AwardsFerm.Core.Models;
using Microsoft.Playwright;

namespace AwardsFerm.Infrastructure.Playwright;

internal sealed class LandscapeState
{
    public bool Applied { get; set; }
}

internal sealed class SessionStuckTracker
{
    private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);

    public bool Register(string action, int maxRepeats = 3)
    {
        _counts.TryGetValue(action, out var count);
        count++;
        _counts[action] = count;
        return count >= maxRepeats;
    }

    public void Reset(string action) => _counts.Remove(action);
}

internal sealed class SessionStuckException : Exception
{
    public SessionStuckException(string message) : base(message) { }
}

internal static class SessionScreenDiagnostic
{
    private const int MaxTextLength = 5000;

    public static async Task<string> CaptureVisibleTextAsync(IPage page)
    {
        if (page.IsClosed)
            return "(страница закрыта)";

        try
        {
            var text = await page.EvaluateAsync<string?>(
                """
                () => {
                    const parts = [];
                    const body = document.body?.innerText?.trim();
                    if (body) parts.push(body);
                    for (const iframe of document.querySelectorAll('iframe')) {
                        try {
                            const doc = iframe.contentDocument;
                            const t = doc?.body?.innerText?.trim();
                            if (t) parts.push('[iframe] ' + t);
                        } catch { /* cross-origin */ }
                    }
                    return parts.join('\n---\n').slice(0, 6000) || null;
                }
                """);

            return Normalize(text ?? string.Empty);
        }
        catch (Exception ex)
        {
            return $"(не удалось снять текст экрана: {ex.Message})";
        }
    }

    public static async Task ReportDiagnosticAsync(
        string sessionId,
        IPage page,
        string reason,
        ISessionEventReporter reporter,
        CancellationToken cancellationToken = default)
    {
        var screenText = await CaptureVisibleTextAsync(page);
        var payload =
            $"Причина: {reason}\n" +
            $"URL: {page.Url}\n" +
            $"--- Содержимое экрана ---\n{screenText}";

        await reporter.ReportAsync(new SessionEvent
        {
            SessionId = sessionId,
            Type = SessionEventType.DiagnosticLog,
            Message = payload
        }, cancellationToken);
    }

    public static async Task TriggerRestartAsync(
        string sessionId,
        IPage page,
        string reason,
        ISessionEventReporter reporter,
        CancellationToken cancellationToken = default)
    {
        await ReportDiagnosticAsync(sessionId, page, reason, reporter, cancellationToken);

        await reporter.ReportAsync(new SessionEvent
        {
            SessionId = sessionId,
            Type = SessionEventType.Log,
            Message = $"⚠ {reason} — перезапуск сессии (см. диагностический лог)"
        }, cancellationToken);

        throw new SessionStuckException(reason);
    }

    private static string Normalize(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "(экран пуст или текст недоступен)";

        text = text.Replace("\r\n", "\n").Trim();
        if (text.Length <= MaxTextLength)
            return text;

        return text[..MaxTextLength] + "\n… (обрезано)";
    }
}
