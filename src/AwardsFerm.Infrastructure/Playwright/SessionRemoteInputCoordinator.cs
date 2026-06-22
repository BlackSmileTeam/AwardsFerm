using System.Collections.Concurrent;
using Microsoft.Playwright;

namespace AwardsFerm.Infrastructure.Playwright;

public sealed class SessionRemoteInputCoordinator
{
    private readonly ConcurrentDictionary<string, Func<IPage?>> _pageResolvers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

    public void Register(string profileId, Func<IPage?> pageResolver)
    {
        if (string.IsNullOrWhiteSpace(profileId))
            return;

        _pageResolvers[profileId] = pageResolver;
    }

    public void Clear(string profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId))
            return;

        _pageResolvers.TryRemove(profileId, out _);
        _locks.TryRemove(profileId, out _);
    }

    public async Task ClickAsync(
        string profileId,
        double xRatio,
        double yRatio,
        CancellationToken cancellationToken = default)
    {
        if (!_pageResolvers.TryGetValue(profileId, out var resolver))
            throw new InvalidOperationException("Сессия не активна — клик недоступен.");

        var gate = _locks.GetOrAdd(profileId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var page = resolver();
            if (page is null || page.IsClosed)
                throw new InvalidOperationException("Страница браузера недоступна.");

            xRatio = Math.Clamp(xRatio, 0, 1);
            yRatio = Math.Clamp(yRatio, 0, 1);

            var viewport = page.ViewportSize;
            var width = viewport?.Width ?? 1280;
            var height = viewport?.Height ?? 800;
            var x = (int)Math.Round(width * xRatio);
            var y = (int)Math.Round(height * yRatio);

            await page.BringToFrontAsync();
            await page.Mouse.MoveAsync(x, y);
            await page.Mouse.DownAsync();
            await Task.Delay(40);
            await page.Mouse.UpAsync();
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task ReloadPreviewPageAsync(string profileId, CancellationToken cancellationToken = default)
    {
        if (!_pageResolvers.TryGetValue(profileId, out var resolver))
            throw new InvalidOperationException("Сессия не активна — обновление недоступно.");

        var gate = _locks.GetOrAdd(profileId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var page = resolver();
            if (page is null || page.IsClosed)
                throw new InvalidOperationException("Страница браузера недоступна.");

            await page.BringToFrontAsync();
            await page.ReloadAsync(new PageReloadOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 45_000
            });
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task CloseCaptchaTabAsync(string profileId, CancellationToken cancellationToken = default)
    {
        if (!_pageResolvers.TryGetValue(profileId, out var resolver))
            throw new InvalidOperationException("Сессия не активна — закрытие вкладки недоступно.");

        var gate = _locks.GetOrAdd(profileId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var page = resolver();
            if (page is null || page.IsClosed)
                throw new InvalidOperationException("Страница браузера недоступна.");

            var captchaPage = await CaptchaHelper.FindManualCaptchaPageAsync(page.Context);
            if (captchaPage is null || captchaPage.IsClosed)
                throw new InvalidOperationException("Вкладка Captcha Verification не найдена.");

            await captchaPage.CloseAsync();

            foreach (var openPage in page.Context.Pages.Where(p => !p.IsClosed))
            {
                if (ReferenceEquals(openPage, captchaPage))
                    continue;

                try
                {
                    await openPage.BringToFrontAsync();
                    break;
                }
                catch
                {
                    // try next tab
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<string?> TryCaptureFrameAsync(
        string profileId,
        CancellationToken cancellationToken = default)
    {
        if (!_pageResolvers.TryGetValue(profileId, out var resolver))
            return null;

        var gate = _locks.GetOrAdd(profileId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var page = resolver();
            if (page is null)
                return null;

            return await SessionScreenshotHelper.CapturePageAsync(page, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }
}
