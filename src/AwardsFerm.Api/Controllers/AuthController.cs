using AwardsFerm.Api.Auth;
using AwardsFerm.Api.Data;
using AwardsFerm.Api.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

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
}
