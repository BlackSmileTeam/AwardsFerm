using AwardsFerm.Core.Models;
using Microsoft.Playwright;

namespace AwardsFerm.Infrastructure.Playwright;

internal static class SessionLocationHelper
{
    public static async Task ApplyAsync(
        IBrowserContext context,
        IPage page,
        DesktopProfile profile,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await context.SetGeolocationAsync(new Geolocation
        {
            Latitude = (float)profile.Latitude,
            Longitude = (float)profile.Longitude
        });

        if (string.IsNullOrWhiteSpace(profile.Timezone))
            return;

        try
        {
            var cdp = await context.NewCDPSessionAsync(page);
            await cdp.SendAsync("Emulation.setTimezoneOverride", new Dictionary<string, object>
            {
                ["timezoneId"] = profile.Timezone
            });
        }
        catch
        {
            // CDP timezone override is best-effort.
        }
    }

    public static async Task<IpGeoLookup?> LookupByIpAsync(
        IPage page,
        string ip,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"http://ip-api.com/json/{ip}?fields=status,country,city,lat,lon,timezone";
            var response = await page.GotoAsync(url, new PageGotoOptions
            {
                Timeout = 10_000,
                WaitUntil = WaitUntilState.DOMContentLoaded
            });
            if (response is null || !response.Ok)
                return null;

            var body = await page.Locator("body").InnerTextAsync();
            if (string.IsNullOrWhiteSpace(body))
                return null;

            using var doc = System.Text.Json.JsonDocument.Parse(body.Trim());
            var root = doc.RootElement;
            if (!root.TryGetProperty("status", out var status) || status.GetString() != "success")
                return null;

            var city = root.TryGetProperty("city", out var cityEl) ? cityEl.GetString() : null;
            var country = root.TryGetProperty("country", out var countryEl) ? countryEl.GetString() : null;
            var timezone = root.TryGetProperty("timezone", out var tzEl) ? tzEl.GetString() : null;
            if (!root.TryGetProperty("lat", out var latEl) || !root.TryGetProperty("lon", out var lonEl))
                return null;

            var label = string.Join(", ", new[] { city, country }.Where(s => !string.IsNullOrWhiteSpace(s)));
            return new IpGeoLookup
            {
                Latitude = latEl.GetDouble(),
                Longitude = lonEl.GetDouble(),
                Timezone = timezone ?? "Europe/Moscow",
                Label = string.IsNullOrWhiteSpace(label) ? ip : label
            };
        }
        catch
        {
            return null;
        }
    }

    public static void ApplyLookupToProfile(DesktopProfile profile, IpGeoLookup lookup)
    {
        profile.Latitude = lookup.Latitude;
        profile.Longitude = lookup.Longitude;
        profile.GeoAnchorLatitude = lookup.Latitude;
        profile.GeoAnchorLongitude = lookup.Longitude;
        if (!string.IsNullOrWhiteSpace(lookup.Timezone))
            profile.Timezone = lookup.Timezone;
        profile.LocationLabel = lookup.Label;
    }

    public static void SetGeoAnchor(DesktopProfile profile)
    {
        profile.GeoAnchorLatitude = profile.Latitude;
        profile.GeoAnchorLongitude = profile.Longitude;
    }

    /// <summary>Случайное смещение вокруг якоря (~до 2–3 км).</summary>
    public static void ApplyRandomDrift(DesktopProfile profile, Random random, double latSpan = 0.025, double lonSpan = 0.035)
    {
        var anchorLat = profile.GeoAnchorLatitude;
        var anchorLon = profile.GeoAnchorLongitude;
        if (anchorLat == 0 && anchorLon == 0)
        {
            anchorLat = profile.Latitude;
            anchorLon = profile.Longitude;
        }

        profile.Latitude = anchorLat + (random.NextDouble() - 0.5) * latSpan;
        profile.Longitude = anchorLon + (random.NextDouble() - 0.5) * lonSpan;
    }

    public static string Format(DesktopProfile profile) =>
        string.IsNullOrWhiteSpace(profile.LocationLabel)
            ? $"{profile.Latitude:F4}, {profile.Longitude:F4} · {profile.Timezone}"
            : $"{profile.LocationLabel} ({profile.Latitude:F4}, {profile.Longitude:F4}) · {profile.Timezone}";
}

internal sealed class IpGeoLookup
{
    public double Latitude { get; init; }
    public double Longitude { get; init; }
    public string Timezone { get; init; } = "Europe/Moscow";
    public string Label { get; init; } = string.Empty;
}
