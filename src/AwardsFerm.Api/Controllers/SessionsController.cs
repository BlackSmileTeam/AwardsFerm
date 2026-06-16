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
