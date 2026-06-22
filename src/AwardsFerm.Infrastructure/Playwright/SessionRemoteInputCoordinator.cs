using System.Collections.Concurrent;
using AwardsFerm.Core.Models;
using Microsoft.Playwright;

namespace AwardsFerm.Infrastructure.Playwright;

public sealed class SessionRemoteInputCoordinator
{
    private readonly ConcurrentDictionary<string, Func<ActivePageHolder?>> _holderResolvers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

    public void Register(string profileId, Func<ActivePageHolder?> holderResolver)
    {
        if (string.IsNullOrWhiteSpace(profileId))
            return;

        _holderResolvers[profileId] = holderResolver;
    }

    public void Clear(string profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId))
            return;

        _holderResolvers.TryRemove(profileId, out _);
        _locks.TryRemove(profileId, out _);
    }

    public async Task ClickAsync(
        string profileId,
        double xRatio,
        double yRatio,
        CancellationToken cancellationToken = default)
    {
        var holder = RequireHolder(profileId);

        var gate = _locks.GetOrAdd(profileId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var page = RequirePreviewPage(holder);

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
        var holder = RequireHolder(profileId);

        var gate = _locks.GetOrAdd(profileId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var page = RequirePreviewPage(holder);

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
        var holder = RequireHolder(profileId);

        var gate = _locks.GetOrAdd(profileId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var captchaPage = await CaptchaHelper.FindManualCaptchaPageAsync(holder.Context);
            if (captchaPage is null || captchaPage.IsClosed)
                throw new InvalidOperationException("Вкладка Captcha Verification не найдена.");

            if (ReferenceEquals(holder.CaptchaFocusPage, captchaPage))
                holder.CaptchaFocusPage = null;

            await captchaPage.CloseAsync();

            foreach (var openPage in holder.Context.Pages.Where(p => !p.IsClosed))
            {
                if (ReferenceEquals(openPage, captchaPage))
                    continue;

                try
                {
                    await openPage.BringToFrontAsync();
                    holder.Page = openPage;
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

    public async Task<IReadOnlyList<BrowserTabInfo>> ListTabsAsync(
        string profileId,
        CancellationToken cancellationToken = default)
    {
        var holder = RequireHolder(profileId);

        var gate = _locks.GetOrAdd(profileId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var pages = holder.Context.Pages.Where(p => !p.IsClosed).ToList();
            IPage? activePage = null;
            try
            {
                activePage = holder.ResolveForPreview();
            }
            catch
            {
                // no active page
            }

            var captchaPage = await CaptchaHelper.FindManualCaptchaPageAsync(holder.Context);
            var result = new List<BrowserTabInfo>(pages.Count);

            for (var i = 0; i < pages.Count; i++)
            {
                var tabPage = pages[i];
                string title;
                try
                {
                    title = await tabPage.TitleAsync();
                }
                catch
                {
                    title = string.Empty;
                }

                result.Add(new BrowserTabInfo
                {
                    Index = i,
                    Url = tabPage.Url,
                    Title = title,
                    IsActive = activePage is not null && ReferenceEquals(tabPage, activePage),
                    IsCaptcha = captchaPage is not null && ReferenceEquals(tabPage, captchaPage)
                });
            }

            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task CloseTabByIndexAsync(
        string profileId,
        int index,
        CancellationToken cancellationToken = default)
    {
        var holder = RequireHolder(profileId);

        var gate = _locks.GetOrAdd(profileId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var pages = holder.Context.Pages.Where(p => !p.IsClosed).ToList();
            if (index < 0 || index >= pages.Count)
                throw new InvalidOperationException("Вкладка не найдена.");

            var target = pages[index];
            if (ReferenceEquals(holder.CaptchaFocusPage, target))
                holder.CaptchaFocusPage = null;

            await target.CloseAsync();

            if (holder.Page.IsClosed)
            {
                var next = holder.Context.Pages.LastOrDefault(p => !p.IsClosed);
                if (next is not null)
                    holder.Page = next;
            }

            var openPage = holder.Context.Pages.LastOrDefault(p => !p.IsClosed);
            if (openPage is not null)
            {
                try
                {
                    await openPage.BringToFrontAsync();
                    holder.Page = openPage;
                }
                catch
                {
                    // best-effort
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
        if (!_holderResolvers.TryGetValue(profileId, out var resolver))
            return null;

        var holder = resolver();
        if (holder is null)
            return null;

        var gate = _locks.GetOrAdd(profileId, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken))
            return null;
        try
        {
            IPage? page;
            try
            {
                page = holder.ResolveForPreview();
            }
            catch
            {
                return null;
            }

            if (page is null)
                return null;

            return await SessionScreenshotHelper.CapturePageAsync(page, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private ActivePageHolder RequireHolder(string profileId)
    {
        if (!_holderResolvers.TryGetValue(profileId, out var resolver))
            throw new InvalidOperationException("Сессия не активна.");

        var holder = resolver();
        if (holder is null)
            throw new InvalidOperationException("Сессия не активна.");

        return holder;
    }

    private static IPage RequirePreviewPage(ActivePageHolder holder)
    {
        try
        {
            var page = holder.ResolveForPreview();
            if (page.IsClosed)
                throw new InvalidOperationException("Страница браузера недоступна.");

            return page;
        }
        catch (PlaywrightException ex)
        {
            throw new InvalidOperationException("Страница браузера недоступна.", ex);
        }
    }
}
