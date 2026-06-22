using Microsoft.Playwright;

namespace AwardsFerm.Infrastructure.Playwright;

public sealed class ActivePageHolder
{
    public required IBrowserContext Context { get; init; }
    public required IPage Page { get; set; }
    public string? UrlPart { get; set; }

    /// <summary>Вкладка с ручной капчей — приоритет для «Просмотра».</summary>
    public IPage? CaptchaFocusPage { get; set; }

    public IPage ResolveForPreview()
    {
        if (CaptchaFocusPage is { IsClosed: false } captchaPage)
            return captchaPage;

        return Resolve();
    }

    public IPage Resolve()
    {
        if (!Page.IsClosed)
            return Page;

        var byUrl = Context.Pages.LastOrDefault(p =>
            !p.IsClosed &&
            !string.IsNullOrEmpty(UrlPart) &&
            p.Url.Contains(UrlPart, StringComparison.OrdinalIgnoreCase));

        if (byUrl is not null)
        {
            Page = byUrl;
            return byUrl;
        }

        var anyOpen = Context.Pages.LastOrDefault(p => !p.IsClosed);
        if (anyOpen is not null)
        {
            Page = anyOpen;
            return anyOpen;
        }

        throw new PlaywrightException("Нет открытых вкладок браузера.");
    }

    public bool TryResolve(out IPage? page)
    {
        try
        {
            page = Resolve();
            return true;
        }
        catch
        {
            page = null;
            return false;
        }
    }
}
