using AwardsFerm.Core.Interfaces;
using AwardsFerm.Core.Models;
using AwardsFerm.Infrastructure.Behavior;
using Microsoft.Playwright;

namespace AwardsFerm.Infrastructure.Playwright;

internal static class YandexWarmupHelper
{
    private static readonly Random Random = new();

    private static readonly string[] NewsHubUrls =
    [
        "https://dzen.ru/news",
        "https://yandex.ru/news",
        "https://yandex.ru/"
    ];

    public static async Task BrowseNewsBlocksAsync(
        IPage page,
        string sessionId,
        ISessionEventReporter reporter,
        CancellationToken cancellationToken = default,
        int minArticles = 2,
        int maxArticles = 4)
    {
        if (page.IsClosed)
            return;

        await reporter.ReportAsync(new SessionEvent
        {
            SessionId = sessionId,
            Type = SessionEventType.Log,
            Message = "Прогрев: просмотр новостных блоков…"
        }, cancellationToken);

        if (minArticles > maxArticles)
            (minArticles, maxArticles) = (maxArticles, minArticles);

        var articlesToRead = Random.Next(minArticles, maxArticles + 1);
        var visited = 0;
        var attempts = 0;
        var maxAttempts = Math.Max(articlesToRead * 2, 4);
        var triedHrefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var hubUrl in NewsHubUrls)
        {
            if (visited >= articlesToRead)
                break;

            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await page.GotoAsync(hubUrl, new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                    Timeout = 60_000
                });
                await HumanBehavior.DelayAsync(2500, 4000, cancellationToken);
                await YandexUiHelper.DismissPopupsAsync(page, cancellationToken);
                await CaptchaHelper.WaitForManualSolveAsync(page, sessionId, reporter, cancellationToken);
                await HumanBehavior.ScrollNaturallyAsync(page, cancellationToken);

                var hrefs = await CollectNewsHrefsAsync(page);
                await reporter.ReportAsync(new SessionEvent
                {
                    SessionId = sessionId,
                    Type = SessionEventType.Log,
                    Message = $"Найдено ссылок на новости: {hrefs.Count} ({hubUrl})"
                }, cancellationToken);

                foreach (var href in hrefs)
                {
                    if (visited >= articlesToRead || attempts >= maxAttempts)
                        break;

                    if (!triedHrefs.Add(href))
                        continue;

                    attempts++;
                    if (await OpenNewsArticleAsync(page, href, sessionId, reporter, cancellationToken))
                        visited++;
                }
            }
            catch
            {
                // пробуем следующий источник
            }
        }

        if (!page.Url.Contains("yandex.ru", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                await page.GotoAsync("https://yandex.ru/", new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                    Timeout = 30_000
                });
            }
            catch
            {
                // ignore
            }
        }

        await reporter.ReportAsync(new SessionEvent
        {
            SessionId = sessionId,
            Type = SessionEventType.Log,
            Message = $"Прогрев: просмотрено новостей — {visited}"
        }, cancellationToken);
    }

    private static async Task<List<string>> CollectNewsHrefsAsync(IPage page)
    {
        try
        {
            var hrefs = await page.EvaluateAsync<string[]>(
                """
                () => {
                  const out = [];
                  const seen = new Set();
                  const isNews = (h) => {
                    if (!h || h.startsWith('#') || h.startsWith('javascript:')) return false;
                    const u = h.toLowerCase();
                    if (u.includes('/games')) return false;
                    if (u.includes('/news/rubric/') || u.includes('/news/region/')) return false;
                    return u.includes('dzen.ru/a/') ||
                           u.includes('/news/story/') ||
                           u.includes('dzen.ru/news/story/') ||
                           (u.includes('yandex.ru/news/') && !u.endsWith('/news') && !u.endsWith('/news/'));
                  };
                  for (const a of document.querySelectorAll('a[href]')) {
                    let h = a.href;
                    if (!isNews(h)) continue;
                    if (seen.has(h)) continue;
                    const r = a.getBoundingClientRect();
                    if (r.width < 40 || r.height < 10) continue;
                    seen.add(h);
                    out.push(h);
                  }
                  return out.slice(0, 25);
                }
                """);

            return hrefs?.Where(h => !string.IsNullOrWhiteSpace(h)).ToList() ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static async Task<bool> OpenNewsArticleAsync(
        IPage page,
        string href,
        string sessionId,
        ISessionEventReporter reporter,
        CancellationToken cancellationToken)
    {
        try
        {
            var shortHref = href.Length > 70 ? href[..67] + "…" : href;
            await reporter.ReportAsync(new SessionEvent
            {
                SessionId = sessionId,
                Type = SessionEventType.Log,
                Message = $"Открываем новость: {shortHref}"
            }, cancellationToken);

            var urlBefore = page.Url;
            await page.GotoAsync(href, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 45_000
            });
            await HumanBehavior.DelayAsync(2000, 4000, cancellationToken);

            if (page.Url.Equals(urlBefore, StringComparison.OrdinalIgnoreCase))
                return false;

            if (page.Url.Contains("/news/rubric/", StringComparison.OrdinalIgnoreCase)
                || page.Url.Contains("/news/region/", StringComparison.OrdinalIgnoreCase))
                return false;

            await CaptchaHelper.WaitForManualSolveAsync(page, sessionId, reporter, cancellationToken);
            await HumanBehavior.ScrollNaturallyAsync(page, cancellationToken);
            await HumanBehavior.MoveMouseRandomlyAsync(page, cancellationToken);
            await HumanBehavior.DelayAsync(5000, 9000, cancellationToken);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
