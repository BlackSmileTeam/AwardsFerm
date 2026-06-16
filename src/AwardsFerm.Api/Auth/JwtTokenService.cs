using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using AwardsFerm.Api.Data.Entities;
using AwardsFerm.Api.Options;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace AwardsFerm.Api.Auth;

public sealed class JwtTokenService
{
    private readonly AuthOptions _options;
    private readonly SymmetricSecurityKey _key;

    public JwtTokenService(IOptions<AuthOptions> options)
    {
        _options = options.Value;
        _key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.JwtSecret));
    }

    public string Create(UserEntity user)
    {
        var creds = new SigningCredentials(_key, SecurityAlgorithms.HmacSha256);
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.Login)
        };

        var token = new JwtSecurityToken(
            issuer: _options.JwtIssuer,
            audience: _options.JwtAudience,
            claims: claims,
            expires: DateTime.UtcNow.AddHours(_options.JwtExpiresHours),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
