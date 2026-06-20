using System.Globalization;
using System.Text.Json;
using AwardsFerm.Api.Options;
using AwardsFerm.Core.Models;
using Microsoft.Extensions.Options;

namespace AwardsFerm.Api.Services;

public sealed class YandexRsyaStatisticsService
{
    private static readonly string[] MeasureFields =
    [
        "partner_wo_nds",
        "shows",
        "clicks",
        "hits",
        "fillrate"
    ];

    private readonly HttpClient _httpClient;
    private readonly YandexRsyaOptions _options;
    private readonly ILogger<YandexRsyaStatisticsService> _logger;
    private readonly string? _tokenFilePath;

    public YandexRsyaStatisticsService(
        HttpClient httpClient,
        IOptions<YandexRsyaOptions> options,
        ILogger<YandexRsyaStatisticsService> logger,
        IWebHostEnvironment environment)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
        _tokenFilePath = ResolveTokenFilePath(environment.ContentRootPath);
    }

    public async Task<RsyaDashboard> GetDashboardAsync(CancellationToken cancellationToken = default)
    {
        var token = ResolveToken();
        if (string.IsNullOrWhiteSpace(token))
            return RsyaDashboard.NotConfigured();

        return await GetDashboardForTokenAsync(token, cancellationToken);
    }

    public async Task<RsyaDashboard> GetDashboardForTokenAsync(string token, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token))
            return RsyaDashboard.NotConfigured();

        try
        {
            var todayTask = FetchPeriodSafeAsync(token, "today", includeDailyPoints: false, cancellationToken);
            var yesterdayTask = FetchPeriodSafeAsync(token, "yesterday", includeDailyPoints: false, cancellationToken);
            var monthTask = FetchPeriodSafeAsync(token, "thismonth", includeDailyPoints: false, cancellationToken);
            var chartTask = FetchPeriodSafeAsync(token, "30days", includeDailyPoints: true, cancellationToken);

            await Task.WhenAll(todayTask, yesterdayTask, monthTask, chartTask);

            var today = await todayTask;
            var yesterday = await yesterdayTask;
            var month = await monthTask;
            var chart = await chartTask;
            var weekStats = SumWeekFromDailyChart(chart.DailyPoints);

            return new RsyaDashboard
            {
                Configured = true,
                Currency = _options.Currency,
                ReportTitle = chart.ReportTitle ?? month.ReportTitle,
                Today = today.Stats,
                Yesterday = yesterday.Stats,
                ThisWeek = weekStats,
                ThisMonth = month.Stats,
                DailyChart = chart.DailyPoints,
                UpdatedAt = DateTimeOffset.UtcNow
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load RSYA statistics");
            return RsyaDashboard.Failed(ex.Message);
        }
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ResolveToken());

    private async Task<PeriodReport> FetchPeriodSafeAsync(
        string token,
        string period,
        bool includeDailyPoints,
        CancellationToken cancellationToken)
    {
        try
        {
            return await FetchPeriodAsync(token, period, includeDailyPoints, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load RSYA period {Period}", period);
            return new PeriodReport(new RsyaPeriodStats(), [], null);
        }
    }

    private static RsyaPeriodStats SumWeekFromDailyChart(IReadOnlyList<RsyaDailyPoint> points)
    {
        if (points.Count == 0)
            return new RsyaPeriodStats();

        var tz = TimeZoneInfo.FindSystemTimeZoneById(
            OperatingSystem.IsWindows() ? "Russian Standard Time" : "Europe/Moscow");
        var today = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz).Date;
        var weekStart = today.AddDays(-6);

        decimal reward = 0;
        long shows = 0;
        long clicks = 0;
        long hits = 0;

        foreach (var point in points)
        {
            if (!TryParseChartDate(point.Date, out var date))
                continue;

            if (date < weekStart || date > today)
                continue;

            reward += point.Reward;
            shows += point.Shows;
            clicks += point.Clicks;
        }

        return new RsyaPeriodStats
        {
            Reward = reward,
            Shows = shows,
            Clicks = clicks,
            Hits = hits
        };
    }

    private static bool TryParseChartDate(string raw, out DateTime date)
    {
        if (DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out date))
            return true;

        return DateTime.TryParse(raw, CultureInfo.GetCultureInfo("ru-RU"), DateTimeStyles.None, out date);
    }

    private async Task<PeriodReport> FetchPeriodAsync(
        string token,
        string period,
        bool includeDailyPoints,
        CancellationToken cancellationToken)
    {
        var query = BuildQuery(period, includeDailyPoints: includeDailyPoints);
        using var request = new HttpRequestMessage(HttpMethod.Get, query);
        request.Headers.TryAddWithoutValidation("Authorization", FormatAuthHeader(token));
        request.Headers.TryAddWithoutValidation("Accept", "application/json");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"РСЯ API: HTTP {(int)response.StatusCode} — {TrimError(body)}");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        if (root.TryGetProperty("result", out var result) && result.GetString() == "error")
            throw new InvalidOperationException($"РСЯ API: {ExtractApiError(root)}");

        if (!root.TryGetProperty("data", out var data))
            throw new InvalidOperationException("РСЯ API: пустой ответ");

        var currencyId = ResolveCurrencyId(data, _options.Currency);
        var stats = ParseTotals(data, currencyId);
        var dailyPoints = includeDailyPoints ? ParseDailyPoints(data, currencyId) : [];

        var reportTitle = data.TryGetProperty("report_title", out var titleEl)
            ? titleEl.GetString()
            : null;

        return new PeriodReport(stats, dailyPoints, reportTitle);
    }

    private string BuildQuery(string period, bool includeDailyPoints) =>
        BuildQuery(period, null, includeDailyPoints);

    private string BuildQuery(string? period, string? periodEnd, bool includeDailyPoints)
    {
        var parts = new List<string>
        {
            $"lang=ru",
            "pretty=1",
            $"currency={Uri.EscapeDataString(_options.Currency)}",
            "stat_type=main",
            "timezone=Europe/Moscow"
        };

        if (!string.IsNullOrWhiteSpace(periodEnd))
        {
            parts.Add($"period={Uri.EscapeDataString(period!)}");
            parts.Add($"period={Uri.EscapeDataString(periodEnd)}");
        }
        else
        {
            parts.Add($"period={Uri.EscapeDataString(period!)}");
        }

        if (includeDailyPoints)
            parts.Add($"dimension_field={Uri.EscapeDataString("date|day")}");

        foreach (var field in MeasureFields)
            parts.Add($"field={Uri.EscapeDataString(field)}");

        return $"{_options.ApiBaseUrl}?{string.Join("&", parts)}";
    }

    private static RsyaPeriodStats ParseTotals(JsonElement data, string currencyId)
    {
        if (!data.TryGetProperty("totals", out var totals)
            || !totals.TryGetProperty(currencyId, out var values)
            || values.ValueKind != JsonValueKind.Array
            || values.GetArrayLength() == 0)
        {
            return new RsyaPeriodStats();
        }

        return ParseMeasures(values[0]);
    }

    private static List<RsyaDailyPoint> ParseDailyPoints(JsonElement data, string currencyId)
    {
        if (!data.TryGetProperty("points", out var points) || points.ValueKind != JsonValueKind.Array)
            return [];

        var result = new List<RsyaDailyPoint>();

        foreach (var point in points.EnumerateArray())
        {
            var date = ExtractDate(point);
            if (date is null)
                continue;

            var measures = point.TryGetProperty("measures", out var measuresEl) && measuresEl.GetArrayLength() > 0
                ? measuresEl[0]
                : default;

            if (measures.ValueKind == JsonValueKind.Undefined)
                continue;

            var stats = ParseMeasures(measures);
            result.Add(new RsyaDailyPoint
            {
                Date = date,
                Reward = stats.Reward,
                Shows = stats.Shows,
                Clicks = stats.Clicks
            });
        }

        return result
            .OrderBy(p => p.Date, StringComparer.Ordinal)
            .TakeLast(14)
            .ToList();
    }

    private static string? ExtractDate(JsonElement point)
    {
        if (!point.TryGetProperty("dimensions", out var dimensions))
            return null;

        if (dimensions.TryGetProperty("date", out var dateEl))
        {
            if (dateEl.ValueKind == JsonValueKind.Array && dateEl.GetArrayLength() > 0)
                return dateEl[0].GetString();
            if (dateEl.ValueKind == JsonValueKind.String)
                return dateEl.GetString();
        }

        return null;
    }

    private static RsyaPeriodStats ParseMeasures(JsonElement measures)
    {
        return new RsyaPeriodStats
        {
            Reward = ReadDecimal(measures, "partner_wo_nds"),
            Shows = ReadLong(measures, "shows"),
            Clicks = ReadLong(measures, "clicks"),
            Hits = ReadLong(measures, "hits"),
            FillRate = ReadNullableDouble(measures, "fillrate")
        };
    }

    private static string ResolveCurrencyId(JsonElement data, string currencyCode)
    {
        if (!data.TryGetProperty("currencies", out var currencies) || currencies.ValueKind != JsonValueKind.Array)
            return "2";

        foreach (var currency in currencies.EnumerateArray())
        {
            if (currency.TryGetProperty("code", out var code)
                && string.Equals(code.GetString(), currencyCode, StringComparison.OrdinalIgnoreCase)
                && currency.TryGetProperty("id", out var id))
            {
                return id.GetString() ?? "2";
            }
        }

        return "2";
    }

    private static decimal ReadDecimal(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var value))
            return 0;

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDecimal(),
            JsonValueKind.String when decimal.TryParse(value.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => 0
        };
    }

    private static long ReadLong(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var value))
            return 0;

        return value.ValueKind switch
        {
            JsonValueKind.Number => (long)value.GetDouble(),
            JsonValueKind.String when long.TryParse(value.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => 0
        };
    }

    private static double? ReadNullableDouble(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null
        };
    }

    private static string ExtractApiError(JsonElement root)
    {
        if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
            return errors[0].ToString();

        if (root.TryGetProperty("error", out var error))
            return error.ToString();

        return "неизвестная ошибка";
    }

    private static string TrimError(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return "пустой ответ";

        return body.Length > 200 ? body[..200] + "…" : body;
    }

    private string? ResolveToken()
    {
        if (!string.IsNullOrWhiteSpace(_options.OAuthToken))
            return SanitizeToken(_options.OAuthToken);

        var envToken = Environment.GetEnvironmentVariable("RSYA_OAUTH_TOKEN")
                       ?? Environment.GetEnvironmentVariable("YANDEX_RSYA_OAUTH_TOKEN");
        if (!string.IsNullOrWhiteSpace(envToken))
            return SanitizeToken(envToken);

        if (_tokenFilePath is not null && File.Exists(_tokenFilePath))
        {
            var fromFile = File.ReadAllText(_tokenFilePath);
            var sanitized = SanitizeToken(fromFile);
            if (!string.IsNullOrWhiteSpace(sanitized))
                return sanitized;
        }

        return null;
    }

    private static string? SanitizeToken(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var token = raw.Trim().TrimStart('\uFEFF');

        if (token.StartsWith("OAuth ", StringComparison.OrdinalIgnoreCase))
            token = token[6..].Trim();

        var sb = new System.Text.StringBuilder(token.Length);
        foreach (var ch in token)
        {
            if (ch is >= (char)32 and <= (char)126)
                sb.Append(ch);
        }

        var result = sb.ToString().Trim();
        return result.Length > 0 ? result : null;
    }

    private static string? ResolveTokenFilePath(string contentRoot)
    {
        var dir = new DirectoryInfo(contentRoot);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "profiles", "rsya-token.txt");
            if (Directory.Exists(Path.Combine(dir.FullName, "profiles")))
                return candidate;
            dir = dir.Parent;
        }

        return null;
    }

    private static string FormatAuthHeader(string token)
    {
        token = token.Trim();
        return token.StartsWith("OAuth ", StringComparison.OrdinalIgnoreCase) ? token : $"OAuth {token}";
    }

    private sealed record PeriodReport(
        RsyaPeriodStats Stats,
        IReadOnlyList<RsyaDailyPoint> DailyPoints,
        string? ReportTitle);
}
