using AwardsFerm.Core.Models;
using AwardsFerm.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace AwardsFerm.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/[controller]")]
public sealed class SessionsController : ControllerBase
{
    private readonly SessionManager _sessionManager;
    private readonly SessionRunnerService _runner;
    private readonly UserAccountResolver _resolver;

    public SessionsController(SessionManager sessionManager, SessionRunnerService runner, UserAccountResolver resolver)
    {
        _sessionManager = sessionManager;
        _runner = runner;
        _resolver = resolver;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<SessionInfo>>> GetAll(CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        var filtered = new List<SessionInfo>();
        foreach (var session in _sessionManager.GetAll())
        {
            if (session.AdAccountId is null) continue;
            if (await _resolver.UserOwnsAccountAsync(userId, session.AdAccountId.Value, cancellationToken))
                filtered.Add(session);
        }
        return Ok(filtered);
    }

    [HttpGet("current")]
    public async Task<ActionResult<SessionInfo?>> GetCurrent(CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        var current = _sessionManager.GetCurrent();
        if (current?.AdAccountId is null) return Ok(null);
        if (!await _resolver.UserOwnsAccountAsync(userId, current.AdAccountId.Value, cancellationToken))
            return Ok(null);
        return Ok(current);
    }

    [HttpGet("{sessionId}")]
    public async Task<ActionResult<SessionInfo>> GetById(string sessionId, CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        var session = _sessionManager.GetById(sessionId);
        if (session is null) return NotFound();
        if (session.AdAccountId is null) return NotFound();
        if (!await _resolver.UserOwnsAccountAsync(userId, session.AdAccountId.Value, cancellationToken))
            return NotFound();
        return Ok(session);
    }

    [HttpGet("{sessionId}/log")]
    public async Task<IActionResult> DownloadLog(string sessionId, CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        var session = _sessionManager.GetById(sessionId);
        if (session is null) return NotFound();
        if (session.AdAccountId is null) return NotFound();
        if (!await _resolver.UserOwnsAccountAsync(userId, session.AdAccountId.Value, cancellationToken))
            return NotFound();

        var fileName = $"{sessionId}.log";
        var file = await _runner.GetSessionLogFileAsync(sessionId, cancellationToken);
        if (file is { Length: > 0 })
            return File(file, "text/plain; charset=utf-8", fileName);

        if (session.Logs.Count == 0 && session.DiagnosticLogs.Count == 0)
            return NotFound();

        return File(SessionRunnerService.BuildLogFile(session), "text/plain; charset=utf-8", fileName);
    }

    [HttpPost("start")]
    public async Task<ActionResult<SessionInfo>> Start(
        [FromBody] StartSessionRequest? request,
        CancellationToken cancellationToken)
    {
        request ??= new StartSessionRequest();
        request.Options ??= new YandexGamesSearchOptions { Headless = false };
        var userId = GetUserId();

        if (request.AdAccountId is null && !string.IsNullOrWhiteSpace(request.ProfileId))
        {
            request.AdAccountId = await _resolver.ResolveAdAccountByProfileAsync(userId, request.ProfileId, cancellationToken);
        }
        if (request.AdAccountId is null)
            return BadRequest("adAccountId is required.");
        if (!await _resolver.UserOwnsAccountAsync(userId, request.AdAccountId.Value, cancellationToken))
            return Forbid();

        try
        {
            var session = await _runner.StartAsync(request, cancellationToken);
            return Ok(session);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(ex.Message);
        }
    }

    [HttpPost("profile/{profileId}/stop")]
    public async Task<IActionResult> StopByProfile(string profileId, CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        var adAccountId = await _resolver.ResolveAdAccountByProfileAsync(userId, profileId, cancellationToken);
        if (adAccountId is null)
            return NotFound();
        if (!await _resolver.UserOwnsAccountAsync(userId, adAccountId.Value, cancellationToken))
            return Forbid();

        await _runner.StopProfileAsync(profileId, cancellationToken);
        return NoContent();
    }

    [HttpPost("{sessionId}/stop")]
    public async Task<IActionResult> StopById(string sessionId, CancellationToken cancellationToken)
    {
        var session = _sessionManager.GetById(sessionId);
        if (session is null)
            return NotFound();
        var userId = GetUserId();
        if (session.AdAccountId is not null &&
            !await _resolver.UserOwnsAccountAsync(userId, session.AdAccountId.Value, cancellationToken))
            return Forbid();

        await _runner.StopProfileAsync(session.ProfileId, cancellationToken);
        return NoContent();
    }

    [HttpPost("profile/{profileId}/pause")]
    public async Task<IActionResult> PauseByProfile(string profileId, CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        var adAccountId = await _resolver.ResolveAdAccountByProfileAsync(userId, profileId, cancellationToken);
        if (adAccountId is null)
            return NotFound();
        if (!await _resolver.UserOwnsAccountAsync(userId, adAccountId.Value, cancellationToken))
            return Forbid();

        try
        {
            await _runner.PauseProfileAsync(profileId, cancellationToken);
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(ex.Message);
        }
    }

    [HttpPost("profile/{profileId}/resume")]
    public async Task<IActionResult> ResumeByProfile(string profileId, CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        var adAccountId = await _resolver.ResolveAdAccountByProfileAsync(userId, profileId, cancellationToken);
        if (adAccountId is null)
            return NotFound();
        if (!await _resolver.UserOwnsAccountAsync(userId, adAccountId.Value, cancellationToken))
            return Forbid();

        try
        {
            await _runner.ResumeProfileAsync(profileId, cancellationToken);
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(ex.Message);
        }
    }

    [HttpPost("profile/{profileId}/preview")]
    public async Task<IActionResult> SetPreviewByProfile(
        string profileId,
        [FromBody] PreviewRequest request,
        CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        var adAccountId = await _resolver.ResolveAdAccountByProfileAsync(userId, profileId, cancellationToken);
        if (adAccountId is null)
            return NotFound();
        if (!await _resolver.UserOwnsAccountAsync(userId, adAccountId.Value, cancellationToken))
            return Forbid();

        await _runner.SetPreviewAsync(profileId, request.Enabled, cancellationToken);
        return NoContent();
    }

    [HttpPost("profile/{profileId}/preview/click")]
    public async Task<IActionResult> PreviewClickByProfile(
        string profileId,
        [FromBody] PreviewClickRequest request,
        CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        var adAccountId = await _resolver.ResolveAdAccountByProfileAsync(userId, profileId, cancellationToken);
        if (adAccountId is null)
            return NotFound();
        if (!await _resolver.UserOwnsAccountAsync(userId, adAccountId.Value, cancellationToken))
            return Forbid();

        try
        {
            await _runner.PreviewClickAsync(profileId, request.XRatio, request.YRatio, cancellationToken);
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(ex.Message);
        }
    }

    [HttpPost("profile/{profileId}/preview/reload")]
    public async Task<IActionResult> PreviewReloadByProfile(string profileId, CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        var adAccountId = await _resolver.ResolveAdAccountByProfileAsync(userId, profileId, cancellationToken);
        if (adAccountId is null)
            return NotFound();
        if (!await _resolver.UserOwnsAccountAsync(userId, adAccountId.Value, cancellationToken))
            return Forbid();

        try
        {
            await _runner.PreviewReloadAsync(profileId, cancellationToken);
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(ex.Message);
        }
    }

    [HttpPost("profile/{profileId}/preview/close-captcha-tab")]
    public async Task<IActionResult> PreviewCloseCaptchaTabByProfile(string profileId, CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        var adAccountId = await _resolver.ResolveAdAccountByProfileAsync(userId, profileId, cancellationToken);
        if (adAccountId is null)
            return NotFound();
        if (!await _resolver.UserOwnsAccountAsync(userId, adAccountId.Value, cancellationToken))
            return Forbid();

        try
        {
            await _runner.PreviewCloseCaptchaTabAsync(profileId, cancellationToken);
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(ex.Message);
        }
    }

    [HttpGet("profile/{profileId}/preview/tabs")]
    public async Task<IActionResult> GetBrowserTabsByProfile(string profileId, CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        var adAccountId = await _resolver.ResolveAdAccountByProfileAsync(userId, profileId, cancellationToken);
        if (adAccountId is null)
            return NotFound();
        if (!await _resolver.UserOwnsAccountAsync(userId, adAccountId.Value, cancellationToken))
            return Forbid();

        try
        {
            var tabs = await _runner.ListBrowserTabsAsync(profileId, cancellationToken);
            return Ok(tabs);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(ex.Message);
        }
    }

    [HttpPost("profile/{profileId}/preview/tabs/{index:int}/reload")]
    public async Task<IActionResult> PreviewReloadTabByProfile(
        string profileId,
        int index,
        CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        var adAccountId = await _resolver.ResolveAdAccountByProfileAsync(userId, profileId, cancellationToken);
        if (adAccountId is null)
            return NotFound();
        if (!await _resolver.UserOwnsAccountAsync(userId, adAccountId.Value, cancellationToken))
            return Forbid();

        try
        {
            await _runner.PreviewReloadTabAsync(profileId, index, cancellationToken);
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(ex.Message);
        }
    }

    [HttpDelete("profile/{profileId}/preview/tabs/{index:int}")]
    public async Task<IActionResult> CloseBrowserTabByProfile(
        string profileId,
        int index,
        CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        var adAccountId = await _resolver.ResolveAdAccountByProfileAsync(userId, profileId, cancellationToken);
        if (adAccountId is null)
            return NotFound();
        if (!await _resolver.UserOwnsAccountAsync(userId, adAccountId.Value, cancellationToken))
            return Forbid();

        try
        {
            await _runner.CloseBrowserTabAsync(profileId, index, cancellationToken);
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(ex.Message);
        }
    }

    [HttpGet("profile/{profileId}/preview/frame")]
    public async Task<IActionResult> GetPreviewFrameByProfile(string profileId, CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        var adAccountId = await _resolver.ResolveAdAccountByProfileAsync(userId, profileId, cancellationToken);
        if (adAccountId is null)
            return NotFound();
        if (!await _resolver.UserOwnsAccountAsync(userId, adAccountId.Value, cancellationToken))
            return Forbid();

        var frame = await _runner.GetPreviewFrameAsync(profileId, cancellationToken);
        return frame is null ? NoContent() : Ok(new { imageBase64 = frame });
    }

    [HttpPost("{sessionId}/pause")]
    public async Task<IActionResult> PauseById(string sessionId, CancellationToken cancellationToken)
    {
        var session = _sessionManager.GetById(sessionId);
        if (session is null)
            return NotFound();
        var userId = GetUserId();
        if (session.AdAccountId is not null &&
            !await _resolver.UserOwnsAccountAsync(userId, session.AdAccountId.Value, cancellationToken))
            return Forbid();

        try
        {
            await _runner.PauseProfileAsync(session.ProfileId, cancellationToken);
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(ex.Message);
        }
    }

    [HttpPost("stop")]
    public async Task<IActionResult> StopAll(CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        foreach (var session in _sessionManager.GetAll()
                     .Where(s => s.Status is SessionStatus.Starting or SessionStatus.Running or SessionStatus.Paused))
        {
            if (session.AdAccountId is not null &&
                !await _resolver.UserOwnsAccountAsync(userId, session.AdAccountId.Value, cancellationToken))
                continue;
            await _runner.StopProfileAsync(session.ProfileId, cancellationToken);
        }

        return NoContent();
    }

    private long GetUserId()
    {
        var value = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!long.TryParse(value, out var userId))
            throw new UnauthorizedAccessException();
        return userId;
    }
}

public sealed class PreviewRequest
{
    public bool Enabled { get; set; }
}

public sealed class PreviewClickRequest
{
    public double XRatio { get; set; }
    public double YRatio { get; set; }
}
