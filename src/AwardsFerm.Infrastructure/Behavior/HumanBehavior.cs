using Microsoft.Playwright;

namespace AwardsFerm.Infrastructure.Behavior;

public static class HumanBehavior
{
    private static readonly Random Random = new();

    public static async Task DelayAsync(int minMs, int maxMs, CancellationToken cancellationToken = default)
    {
        if (minMs > maxMs)
            (minMs, maxMs) = (maxMs, minMs);

        if (minMs == maxMs)
        {
            await Task.Delay(minMs, cancellationToken);
            return;
        }

        await Task.Delay(Random.Next(minMs, maxMs + 1), cancellationToken);
    }

    public static async Task TypeHumanAsync(ILocator locator, string text, CancellationToken cancellationToken = default)
    {
        await locator.ClickAsync();
        await DelayAsync(200, 500, cancellationToken);

        foreach (var ch in text)
        {
            await locator.PressSequentiallyAsync(ch.ToString(), new LocatorPressSequentiallyOptions { Delay = Random.Next(80, 180) });
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    public static async Task ScrollNaturallyAsync(IPage page, CancellationToken cancellationToken = default)
    {
        var scrolls = Random.Next(2, 5);
        for (var i = 0; i < scrolls; i++)
        {
            await page.Mouse.WheelAsync(0, Random.Next(150, 450));
            await DelayAsync(600, 1800, cancellationToken);
        }
    }

    public static async Task MoveAndClickAsync(IPage page, ILocator locator, CancellationToken cancellationToken = default)
    {
        await locator.ScrollIntoViewIfNeededAsync();
        await DelayAsync(300, 800, cancellationToken);

        var box = await locator.BoundingBoxAsync();
        if (box is null)
        {
            await locator.ClickAsync();
            return;
        }

        var x = box.X + box.Width * (0.25 + Random.NextDouble() * 0.5);
        var y = box.Y + box.Height * (0.25 + Random.NextDouble() * 0.5);
        await page.Mouse.MoveAsync((float)x, (float)y, new MouseMoveOptions { Steps = Random.Next(8, 20) });
        await DelayAsync(150, 400, cancellationToken);
        await page.Mouse.ClickAsync((float)x, (float)y);
    }

    public static async Task MoveMouseRandomlyAsync(IPage page, CancellationToken cancellationToken = default)
    {
        var vp = page.ViewportSize;
        var width = Math.Max(vp?.Width ?? 1920, 320);
        var height = Math.Max(vp?.Height ?? 1080, 240);

        var marginX = Math.Clamp(width / 5, 40, 200);
        var marginY = Math.Clamp(height / 5, 40, 150);
        var minX = marginX;
        var maxX = Math.Max(minX + 1, width - marginX);
        var minY = marginY;
        var maxY = Math.Max(minY + 1, height - marginY);

        for (var i = 0; i < Random.Next(3, 7); i++)
        {
            var x = Random.Next(minX, maxX);
            var y = Random.Next(minY, maxY);
            await page.Mouse.MoveAsync(x, y, new MouseMoveOptions { Steps = Random.Next(10, 25) });
            await DelayAsync(400, 1200, cancellationToken);
        }
    }

    public static async Task PlayInGameAsync(IPage page, int minSeconds, int maxSeconds, CancellationToken cancellationToken = default)
    {
        var canvas = await WaitForCanvasAsync(page, cancellationToken, maxWaitSeconds: 60);
        if (canvas is null)
        {
            // Fallback: взаимодействие с центром экрана (игра может быть в iframe без доступного canvas)
            await PlayOnPageAsync(page, minSeconds, maxSeconds, cancellationToken);
            return;
        }

        var totalSeconds = Random.Next(minSeconds, maxSeconds + 1);
        var end = DateTimeOffset.UtcNow.AddSeconds(totalSeconds);

        while (DateTimeOffset.UtcNow < end)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (page.IsClosed)
                return;

            try
            {
                // Переищем canvas — после загрузки он мог появиться в другом фрейме
                canvas = await FindLargestCanvasAsync(page) ?? canvas;

                var box = await canvas.BoundingBoxAsync();
                if (box is { Width: > 50, Height: > 50 })
                {
                    var x = box.X + box.Width * (0.2 + Random.NextDouble() * 0.6);
                    var y = box.Y + box.Height * (0.2 + Random.NextDouble() * 0.6);
                    await page.Mouse.MoveAsync((float)x, (float)y, new MouseMoveOptions { Steps = Random.Next(8, 20) });

                    if (Random.Next(3) == 0)
                        await page.Mouse.ClickAsync((float)x, (float)y);

                    await DelayAsync(300, 900, cancellationToken);
                }
            }
            catch (PlaywrightException) when (page.IsClosed)
            {
                return;
            }

            await DelayAsync(800, 2500, cancellationToken);
        }
    }

    private static async Task<ILocator?> WaitForCanvasAsync(IPage page, CancellationToken cancellationToken, int maxWaitSeconds)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(maxWaitSeconds);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (page.IsClosed)
                return null;

            var canvas = await FindLargestCanvasAsync(page);
            if (canvas is not null)
                return canvas;

            await Task.Delay(1000, cancellationToken);
        }

        return await FindLargestCanvasAsync(page);
    }

    private static async Task<ILocator?> FindLargestCanvasAsync(IPage page)
    {
        ILocator? best = null;
        double bestArea = 0;

        async Task ConsiderAsync(ILocator locator)
        {
            var count = await locator.CountAsync();
            for (var i = 0; i < count; i++)
            {
                var item = locator.Nth(i);
                if (!await item.IsVisibleAsync())
                    continue;

                var box = await item.BoundingBoxAsync();
                if (box is null)
                    continue;

                var area = box.Width * box.Height;
                if (area > bestArea && box.Width >= 300 && box.Height >= 200)
                {
                    bestArea = area;
                    best = item;
                }
            }
        }

        await ConsiderAsync(page.Locator("canvas"));

        foreach (var frame in page.Frames)
        {
            try
            {
                await ConsiderAsync(frame.Locator("canvas"));
            }
            catch
            {
                // detached frame
            }
        }

        return best;
    }

    public static async Task PlayOnPageAsync(IPage page, int minSeconds, int maxSeconds, CancellationToken cancellationToken = default)
    {
        var totalSeconds = Random.Next(minSeconds, maxSeconds + 1);
        var end = DateTimeOffset.UtcNow.AddSeconds(totalSeconds);

        while (DateTimeOffset.UtcNow < end)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (page.IsClosed)
                return;

            try
            {
                var action = Random.Next(4);
                switch (action)
                {
                    case 0:
                        await ScrollNaturallyAsync(page, cancellationToken);
                        break;
                    case 1:
                        if (await ClickInGameAreaAsync(page, cancellationToken))
                            break;
                        goto case 2;
                    case 2:
                    {
                        var vp = page.ViewportSize;
                        var width = Math.Max(vp?.Width ?? 1920, 320);
                        var height = Math.Max(vp?.Height ?? 1080, 240);
                        var marginX = Math.Clamp(width / 5, 40, 100);
                        var marginY = Math.Clamp(height / 5, 40, 100);
                        if (width > marginX * 2 && height > marginY * 2)
                        {
                            var x = Random.Next(marginX, width - marginX);
                            var y = Random.Next(marginY, height - marginY);
                            await page.Mouse.MoveAsync(x, y, new MouseMoveOptions { Steps = Random.Next(5, 15) });
                        }
                        break;
                    }
                    default:
                        await DelayAsync(1000, 3000, cancellationToken);
                        break;
                }
            }
            catch (PlaywrightException) when (page.IsClosed)
            {
                return;
            }
        }
    }

    private static async Task<bool> ClickInGameAreaAsync(IPage page, CancellationToken cancellationToken)
    {
        foreach (var selector in new[] { "#game-frame", ".game-root iframe", ".game-root" })
        {
            try
            {
                var locator = page.Locator(selector).First;
                if (await locator.CountAsync() == 0 || !await locator.IsVisibleAsync())
                    continue;

                var box = await locator.BoundingBoxAsync();
                if (box is null || box.Width < 200 || box.Height < 150)
                    continue;

                var x = (float)(box.X + box.Width * (0.3 + Random.NextDouble() * 0.4));
                var y = (float)(box.Y + box.Height * (0.3 + Random.NextDouble() * 0.4));
                await page.Mouse.MoveAsync(x, y, new MouseMoveOptions { Steps = Random.Next(5, 12) });
                await page.Mouse.ClickAsync(x, y);
                await DelayAsync(400, 900, cancellationToken);
                return true;
            }
            catch
            {
                // try next selector
            }
        }

        return false;
    }
}
