using AwardsFerm.Core.Interfaces;
using AwardsFerm.Infrastructure;
using AwardsFerm.Worker.Services;

var profilesRoot = FindProfilesRoot();
Directory.CreateDirectory(profilesRoot);

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddAwardsFermInfrastructure(profilesRoot);
builder.Services.AddSingleton<SessionExecutionService>();
builder.Services.AddSingleton<HttpSessionEventReporter>();
builder.Services.AddSingleton<SessionFileEventReporter>();
builder.Services.AddSingleton<ISessionEventReporter>(sp => new CompositeSessionEventReporter(
[
    sp.GetRequiredService<HttpSessionEventReporter>(),
    sp.GetRequiredService<SessionFileEventReporter>()
]));

builder.Services.AddHttpClient("api", client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
});

builder.Services.AddHttpClient("spysone", client =>
{
    client.Timeout = TimeSpan.FromSeconds(90);
});

builder.Services.AddHttpClient("proxymarket", client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
    client.BaseAddress = new Uri("https://api.dashboard.proxy.market");
});

builder.Services.AddHostedService<ProxyRefreshBackgroundService>();

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy => policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
});

var app = builder.Build();

app.UseCors();

app.MapPost("/internal/run", async (WorkerRunRequest request, SessionExecutionService executor, CancellationToken cancellationToken) =>
{
    try
    {
        await executor.StartAsync(request, cancellationToken);
        return Results.Accepted();
    }
    catch (InvalidOperationException ex)
    {
        return Results.Conflict(ex.Message);
    }
});

app.MapPost("/internal/stop/{profileId}", async (string profileId, SessionExecutionService executor, CancellationToken cancellationToken) =>
{
    await executor.StopAsync(profileId, cancellationToken);
    return Results.NoContent();
});

app.MapPost("/internal/pause/{profileId}", (string profileId, SessionExecutionService executor) =>
{
    executor.Pause(profileId);
    return Results.NoContent();
});

app.MapPost("/internal/resume/{profileId}", (string profileId, SessionExecutionService executor) =>
{
    executor.Resume(profileId);
    return Results.NoContent();
});

app.MapPost("/internal/preview/{profileId}", async (
    string profileId,
    PreviewRequest request,
    SessionExecutionService executor,
    CancellationToken cancellationToken) =>
{
    await executor.SetPreviewAsync(profileId, request.Enabled, cancellationToken);
    return Results.NoContent();
});

app.MapPost("/internal/preview/{profileId}/click", async (
    string profileId,
    PreviewClickRequest request,
    SessionExecutionService executor,
    CancellationToken cancellationToken) =>
{
    try
    {
        await executor.PreviewClickAsync(profileId, request.XRatio, request.YRatio, cancellationToken);
        return Results.NoContent();
    }
    catch (InvalidOperationException ex)
    {
        return Results.Conflict(ex.Message);
    }
});

app.MapGet("/internal/preview/{profileId}/frame", async (
    string profileId,
    SessionExecutionService executor,
    CancellationToken cancellationToken) =>
{
    var frame = await executor.GetPreviewFrameAsync(profileId, cancellationToken);
    return frame is null ? Results.NoContent() : Results.Ok(new { imageBase64 = frame });
});

app.MapPost("/internal/preview/{profileId}/reload", async (
    string profileId,
    SessionExecutionService executor,
    CancellationToken cancellationToken) =>
{
    try
    {
        await executor.PreviewReloadAsync(profileId, cancellationToken);
        return Results.NoContent();
    }
    catch (InvalidOperationException ex)
    {
        return Results.Conflict(ex.Message);
    }
});

app.MapPost("/internal/preview/{profileId}/close-captcha-tab", async (
    string profileId,
    SessionExecutionService executor,
    CancellationToken cancellationToken) =>
{
    try
    {
        await executor.PreviewCloseCaptchaTabAsync(profileId, cancellationToken);
        return Results.NoContent();
    }
    catch (InvalidOperationException ex)
    {
        return Results.Conflict(ex.Message);
    }
});

app.MapGet("/internal/preview/{profileId}/tabs", async (
    string profileId,
    SessionExecutionService executor,
    CancellationToken cancellationToken) =>
{
    try
    {
        var tabs = await executor.ListBrowserTabsAsync(profileId, cancellationToken);
        return Results.Ok(tabs);
    }
    catch (InvalidOperationException ex)
    {
        return Results.Conflict(ex.Message);
    }
});

app.MapPost("/internal/preview/{profileId}/tabs/{index:int}/reload", async (
    string profileId,
    int index,
    SessionExecutionService executor,
    CancellationToken cancellationToken) =>
{
    try
    {
        await executor.PreviewReloadTabAsync(profileId, index, cancellationToken);
        return Results.NoContent();
    }
    catch (InvalidOperationException ex)
    {
        return Results.Conflict(ex.Message);
    }
});

app.MapDelete("/internal/preview/{profileId}/tabs/{index:int}", async (
    string profileId,
    int index,
    SessionExecutionService executor,
    CancellationToken cancellationToken) =>
{
    try
    {
        await executor.CloseBrowserTabAsync(profileId, index, cancellationToken);
        return Results.NoContent();
    }
    catch (InvalidOperationException ex)
    {
        return Results.Conflict(ex.Message);
    }
});

app.MapGet("/internal/sessions/{sessionId}/log", (string sessionId, IConfiguration configuration) =>
{
    if (string.IsNullOrWhiteSpace(sessionId))
        return Results.BadRequest();

    var path = SessionLogDirectory.GetLogPath(configuration, sessionId);
    if (!File.Exists(path))
        return Results.NotFound();

    return Results.File(path, "text/plain; charset=utf-8", Path.GetFileName(path));
});

app.MapPost("/internal/stop", async (SessionExecutionService executor, CancellationToken cancellationToken) =>
{
    foreach (var profileId in new[] { "session-001", "session-002", "session-003" })
        await executor.StopAsync(profileId, cancellationToken);

    return Results.NoContent();
});

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

static string FindProfilesRoot()
{
    var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (dir is not null)
    {
        var candidate = Path.Combine(dir.FullName, "profiles");
        if (Directory.Exists(candidate))
            return candidate;
        dir = dir.Parent;
    }

    return Path.Combine(Directory.GetCurrentDirectory(), "profiles");
}

app.Run();

public sealed class PreviewRequest
{
    public bool Enabled { get; set; }
}

public sealed class PreviewClickRequest
{
    public double XRatio { get; set; }
    public double YRatio { get; set; }
}
