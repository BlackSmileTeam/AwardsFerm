using Microsoft.Playwright;

namespace AwardsFerm.Infrastructure.Playwright;

internal static class SessionScreenshotHelper
{
    public static async Task<string?> CapturePageAsync(IPage page, CancellationToken cancellationToken = default)
    {
        if (page.IsClosed)
            return null;

        try
        {
            await page.BringToFrontAsync();
            var bytes = await page.ScreenshotAsync(new PageScreenshotOptions
            {
                Type = ScreenshotType.Jpeg,
                Quality = 55,
                FullPage = false,
                Timeout = 15_000,
                Animations = ScreenshotAnimations.Disabled,
                Caret = ScreenshotCaret.Hide
            });

            return bytes.Length == 0 ? null : Convert.ToBase64String(bytes);
        }
        catch
        {
            return null;
        }
    }
}
