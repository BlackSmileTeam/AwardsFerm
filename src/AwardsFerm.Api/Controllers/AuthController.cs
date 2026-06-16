using AwardsFerm.Api.Auth;
using AwardsFerm.Api.Data;
using AwardsFerm.Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace AwardsFerm.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class AuthController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly JwtTokenService _jwt;

    public AuthController(AppDbContext db, JwtTokenService jwt)
    {
        _db = db;
        _jwt = jwt;
    }

    [HttpPost("login")]
    public async Task<ActionResult<LoginResponse>> Login([FromBody] LoginRequest request, CancellationToken ct)
    {
        var login = request.Login.Trim();
        var user = await _db.Users.FirstOrDefaultAsync(x => x.Login == login, ct);
        if (user is null)
            return Unauthorized("Неверный логин или пароль.");

        if (!PasswordHasher.Verify(request.Password, user.PasswordHash, user.PasswordSalt))
            return Unauthorized("Неверный логин или пароль.");

        return Ok(new LoginResponse
        {
            Token = _jwt.Create(user),
            Login = user.Login
        });
    }

    [Authorize]
    [HttpPost("change-password")]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest request, CancellationToken ct)
    {
        var idValue = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!long.TryParse(idValue, out var userId))
            return Unauthorized();

        var user = await _db.Users.FirstOrDefaultAsync(x => x.Id == userId, ct);
        if (user is null)
            return Unauthorized();

        if (string.IsNullOrWhiteSpace(request.NewPassword) || request.NewPassword.Length < 8)
            return BadRequest("Новый пароль должен быть не короче 8 символов.");

        if (!PasswordHasher.Verify(request.CurrentPassword, user.PasswordHash, user.PasswordSalt))
            return BadRequest("Текущий пароль указан неверно.");

        if (request.CurrentPassword == request.NewPassword)
            return BadRequest("Новый пароль должен отличаться от текущего.");

        var (hash, salt) = PasswordHasher.Hash(request.NewPassword);
        user.PasswordHash = hash;
        user.PasswordSalt = salt;
        await _db.SaveChangesAsync(ct);

        return NoContent();
    }
}
