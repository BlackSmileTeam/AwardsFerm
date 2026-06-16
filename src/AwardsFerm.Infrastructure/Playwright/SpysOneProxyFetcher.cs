using System.Globalization;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;

namespace AwardsFerm.Infrastructure.Playwright;

/// <summary>
/// Загружает RU-прокси с spys.one через официальный TXT-фид spys.me (тот же источник, IP:PORT без JS).
/// </summary>
public static class SpysOneProxyFetcher
{
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";

    private static readonly Regex SpysMeLine = new(
        @"^(?<ip>\d{1,3}(?:\.\d{1,3}){3}):(?<port>\d+)\s+(?<country>[A-Z]{2})-",
        RegexOptions.Compiled);

    public static async Task<IReadOnlyList<string>> FetchRuProxyLinesAsync(
        HttpClient http,
        CancellationToken cancellationToken = default)
    {
        var entries = new Dictionary<string, ProxyLine>(StringComparer.OrdinalIgnoreCase);

        await TryAddFromSpysMeAsync(http, "https://spys.me/proxy.txt", "http", entries, cancellationToken);
        await TryAddFromSpysMeAsync(http, "https://spys.me/socks.txt", "socks5", entries, cancellationToken);

        return entries.Values
            .OrderByDescending(x => x.UptimePercent)
            .Take(150)
            .Select(x => x.ToFileLine())
            .ToList();
    }

    public static void WriteProxiesFile(string profilesRoot, IReadOnlyList<string> lines)
    {
        var path = Path.Combine(profilesRoot, "proxies.txt");
        var header =
            $"# Автозагрузка RU-прокси с spys.one (TXT: spys.me){Environment.NewLine}" +
            $"# Обновлено: {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss} UTC{Environment.NewLine}" +
            $"# Формат: url | lat | lon | timezone | locale | label{Environment.NewLine}";

        var body = lines.Count == 0
            ? $"{header}# Список пуст — сессии работают с реальным IP{Environment.NewLine}"
            : header + string.Join(Environment.NewLine, lines) + Environment.NewLine;

        var tmp = path + ".tmp";
        File.WriteAllText(tmp, body);
        File.Move(tmp, path, true);
        ProxyRotator.InvalidateCache();
    }

    private static async Task TryAddFromSpysMeAsync(
        HttpClient http,
        string url,
        string scheme,
        Dictionary<string, ProxyLine> entries,
        CancellationToken cancellationToken)
    {
        try
        {
            await AddFromSpysMeAsync(http, url, scheme, entries, cancellationToken);
        }
        catch
        {
            // один источник недоступен — продолжаем с остальными
        }
    }

    private static async Task AddFromSpysMeAsync(
        HttpClient http,
        string url,
        string scheme,
        Dictionary<string, ProxyLine> entries,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(25));

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd(UserAgent);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/plain"));

        using var response = await http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
        if (!response.IsSuccessStatusCode)
            return;

        await using var stream = await response.Content.ReadAsStreamAsync(timeoutCts.Token);
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream && entries.Count < 200)
        {
            var raw = (await reader.ReadLineAsync(timeoutCts.Token))?.Trim();
            if (string.IsNullOrWhiteSpace(raw)
                || raw.StartsWith('#')
                || raw.StartsWith("Proxy list", StringComparison.OrdinalIgnoreCase)
                || raw.StartsWith("Http proxy", StringComparison.OrdinalIgnoreCase)
                || raw.StartsWith("Socks proxy", StringComparison.OrdinalIgnoreCase)
                || raw.StartsWith("Support by", StringComparison.OrdinalIgnoreCase)
                || raw.StartsWith("BTC ", StringComparison.OrdinalIgnoreCase)
                || raw.StartsWith("IP address", StringComparison.OrdinalIgnoreCase))
                continue;

            var match = SpysMeLine.Match(raw);
            if (!match.Success || match.Groups["country"].Value != "RU")
                continue;

            AddEntry(entries, scheme, match.Groups["ip"].Value, match.Groups["port"].Value, null, 0);
        }
    }

    private static void AddEntry(
        Dictionary<string, ProxyLine> entries,
        string scheme,
        string ip,
        string port,
        string? city,
        int uptimePercent)
    {
        if (!int.TryParse(port, NumberStyles.Integer, CultureInfo.InvariantCulture, out var portNum)
            || portNum is < 1 or > 65535)
            return;

        var key = $"{scheme}://{ip}:{portNum}";
        var geo = RussiaGeo.PickForProfile(ip, new AwardsFerm.Core.Models.DesktopProfile { Id = ip });
        var label = city is null ? geo.Label : $"{city}, Россия";

        if (entries.TryGetValue(key, out var existing))
        {
            if (uptimePercent > existing.UptimePercent)
                entries[key] = new ProxyLine(scheme, ip, portNum, geo, label, uptimePercent);
            return;
        }

        entries[key] = new ProxyLine(scheme, ip, portNum, geo, label, uptimePercent);
    }

    private sealed record ProxyLine(
        string Scheme,
        string Ip,
        int Port,
        ProxyGeoLocation Geo,
        string Label,
        int UptimePercent)
    {
        public string ToFileLine() =>
            $"{Scheme}://{Ip}:{Port} | {Geo.Latitude.ToString(CultureInfo.InvariantCulture)} | " +
            $"{Geo.Longitude.ToString(CultureInfo.InvariantCulture)} | {Geo.Timezone} | {Geo.Locale} | {Label}";
    }
}
