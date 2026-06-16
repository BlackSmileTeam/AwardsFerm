using AwardsFerm.Api.Services;
using AwardsFerm.Core.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace AwardsFerm.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/[controller]")]
public sealed class SlotsController : ControllerBase
{
    private readonly SessionSlotStore _slotStore;
    private readonly SessionRunnerService _runner;
    private readonly UserAccountResolver _resolver;

    public SlotsController(SessionSlotStore slotStore, SessionRunnerService runner, UserAccountResolver resolver)
    {
        _slotStore = slotStore;
        _runner = runner;
        _resolver = resolver;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<SessionSlotDefinition>>> GetAll([FromQuery] long adAccountId, CancellationToken ct)
    {
        var userId = GetUserId();
        if (!await _resolver.UserOwnsAccountAsync(userId, adAccountId, ct))
            return Forbid();
        return Ok(_slotStore.GetAll(adAccountId));
    }

    [HttpPost]
    public async Task<ActionResult<SessionSlotDefinition>> Create([FromBody] CreateSessionSlotRequest? request, CancellationToken ct)
    {
        try
        {
            if (request?.AdAccountId is null) return BadRequest("adAccountId is required.");
            var userId = GetUserId();
            if (!await _resolver.UserOwnsAccountAsync(userId, request.AdAccountId.Value, ct))
                return Forbid();
            return Ok(_slotStore.Add(request.AdAccountId.Value, request.Label));
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(ex.Message);
        }
    }

    [HttpPatch("{profileId}")]
    public async Task<ActionResult<SessionSlotDefinition>> Update(
        string profileId,
        [FromQuery] long adAccountId,
        [FromBody] UpdateSessionSlotRequest request,
        CancellationToken ct)
    {
        try
        {
            var userId = GetUserId();
            if (!await _resolver.UserOwnsAccountAsync(userId, adAccountId, ct))
                return Forbid();
            return Ok(_slotStore.Update(adAccountId, profileId, request));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    [HttpDelete("{profileId}")]
    public async Task<IActionResult> Delete(string profileId, [FromQuery] long adAccountId, CancellationToken cancellationToken)
    {
        try
        {
            var userId = GetUserId();
            if (!await _resolver.UserOwnsAccountAsync(userId, adAccountId, cancellationToken))
                return Forbid();
            await _runner.StopProfileAsync(profileId, cancellationToken);
            _slotStore.Remove(adAccountId, profileId);
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    private long GetUserId()
    {
        var value = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!long.TryParse(value, out var userId))
            throw new UnauthorizedAccessException();
        return userId;
    }
}
