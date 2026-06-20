using AwardsFerm.Core.Interfaces;
using AwardsFerm.Infrastructure;
using AwardsFerm.Worker.Services;

var profilesRoot = FindProfilesRoot();
Directory.CreateDirectory(profilesRoot);

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddAwardsFermInfrastructure(profilesRoot);
builder.Services.AddSingleton<SessionExecutionService>();
builder.Services.AddSingleton<ISessionEventReporter, HttpSessionEventReporter>();

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

app.MapPost("/internal/preview/{profileId}", (string profileId, PreviewRequest request, SessionExecutionService executor) =>
{
    executor.SetPreview(profileId, request.Enabled);
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
