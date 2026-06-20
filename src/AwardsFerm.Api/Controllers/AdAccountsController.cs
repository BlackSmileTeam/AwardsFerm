using System.Security.Claims;
using AwardsFerm.Api.Auth;
using AwardsFerm.Api.Data;
using AwardsFerm.Api.Data.Entities;
using AwardsFerm.Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AwardsFerm.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/[controller]")]
public sealed class AdAccountsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly TokenEncryptionService _tokenEncryption;

    public AdAccountsController(AppDbContext db, TokenEncryptionService tokenEncryption)
    {
        _db = db;
        _tokenEncryption = tokenEncryption;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<AdAccountDto>>> GetAll(CancellationToken ct)
    {
        var userId = GetUserId();
        var accounts = await _db.AdAccounts
            .Where(x => x.UserId == userId)
            .OrderBy(x => x.Id)
            .Select(x => new AdAccountDto
            {
                Id = x.Id,
                Name = x.Name,
                GameTitle = x.GameTitle,
                GameUrl = x.GameUrl,
                CreatedAt = x.CreatedAt
            })
            .ToListAsync(ct);
        return Ok(accounts);
    }

    [HttpPost]
    public async Task<ActionResult<AdAccountDto>> Create([FromBody] CreateAdAccountRequest request, CancellationToken ct)
    {
        var userId = GetUserId();
        if (!await _db.Users.AnyAsync(x => x.Id == userId, ct))
            return Unauthorized("Пользователь не найден. Выйдите и войдите снова.");

        var account = new AdAccountEntity
        {
            UserId = userId,
            Name = request.Name.Trim(),
            GameTitle = request.GameTitle.Trim(),
            GameUrl = request.GameUrl.Trim(),
            TokenEncrypted = _tokenEncryption.Encrypt(request.Token.Trim())
        };

        _db.AdAccounts.Add(account);
        await _db.SaveChangesAsync(ct);

        return Ok(new AdAccountDto
        {
            Id = account.Id,
            Name = account.Name,
            GameTitle = account.GameTitle,
            GameUrl = account.GameUrl,
            CreatedAt = account.CreatedAt
        });
    }

    [HttpPatch("{id:long}")]
    public async Task<ActionResult<AdAccountDto>> Update(long id, [FromBody] UpdateAdAccountRequest request, CancellationToken ct)
    {
        var userId = GetUserId();
        var account = await _db.AdAccounts.FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId, ct);
        if (account is null) return NotFound();

        if (!string.IsNullOrWhiteSpace(request.Name)) account.Name = request.Name.Trim();
        if (!string.IsNullOrWhiteSpace(request.GameTitle)) account.GameTitle = request.GameTitle.Trim();
        if (!string.IsNullOrWhiteSpace(request.GameUrl)) account.GameUrl = request.GameUrl.Trim();
        if (!string.IsNullOrWhiteSpace(request.Token)) account.TokenEncrypted = _tokenEncryption.Encrypt(request.Token.Trim());

        await _db.SaveChangesAsync(ct);

        return Ok(new AdAccountDto
        {
            Id = account.Id,
            Name = account.Name,
            GameTitle = account.GameTitle,
            GameUrl = account.GameUrl,
            CreatedAt = account.CreatedAt
        });
    }

    [HttpDelete("{id:long}")]
    public async Task<IActionResult> Delete(long id, CancellationToken ct)
    {
        var userId = GetUserId();
        var account = await _db.AdAccounts.FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId, ct);
        if (account is null) return NotFound();
        _db.AdAccounts.Remove(account);
        await _db.SaveChangesAsync(ct);
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
