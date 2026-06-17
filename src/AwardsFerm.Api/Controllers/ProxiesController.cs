using AwardsFerm.Api.Services;
using AwardsFerm.Core.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace AwardsFerm.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/[controller]")]
public sealed class ProxiesController : ControllerBase
{
    private readonly ProxyStore _proxyStore;

    public ProxiesController(ProxyStore proxyStore)
    {
        _proxyStore = proxyStore;
    }

    [HttpGet]
    public ActionResult<IReadOnlyList<ProxyDefinition>> GetAll()
    {
        return Ok(_proxyStore.GetAll(GetUserId()));
    }

    [HttpPost]
    public ActionResult<ProxyDefinition> Create([FromBody] CreateProxyRequest request)
    {
        try
        {
            return Ok(_proxyStore.Create(GetUserId(), request));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    [HttpPatch("{proxyId:long}")]
    public ActionResult<ProxyDefinition> Update(long proxyId, [FromBody] UpdateProxyRequest request)
    {
        try
        {
            return Ok(_proxyStore.Update(GetUserId(), proxyId, request));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    [HttpDelete("{proxyId:long}")]
    public IActionResult Delete(long proxyId)
    {
        try
        {
            _proxyStore.Delete(GetUserId(), proxyId);
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
