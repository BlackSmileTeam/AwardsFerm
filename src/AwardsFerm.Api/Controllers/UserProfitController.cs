using System.Security.Claims;
using AwardsFerm.Api.Models;
using AwardsFerm.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AwardsFerm.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/[controller]")]
public sealed class UserProfitController : ControllerBase
{
    private readonly UserProfitService _service;

    public UserProfitController(UserProfitService service)
    {
        _service = service;
    }

    [HttpGet]
    public async Task<ActionResult<UserProfitSummaryDto>> Get(CancellationToken ct)
    {
        var idValue = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!long.TryParse(idValue, out var userId))
            return Unauthorized();

        return Ok(await _service.GetSummaryAsync(userId, ct));
    }
}
