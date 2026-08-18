using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using ObsidianSync.Server.Data;

namespace ObsidianSync.Server.Security;

public sealed class JwtTokenService(IConfiguration configuration)
{
    private readonly string _issuer = configuration["JWT_ISSUER"] ?? configuration["Jwt:Issuer"] ?? "obsync";
    private readonly string _key = ResolveKey(configuration);

    public string CreateToken(User user)
    {
        var credentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_key)), SecurityAlgorithms.HmacSha256);
        var claims = new List<Claim>
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.UniqueName, user.UserName),
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.UserName)
        };
        if (user.IsAdmin)
        {
            claims.Add(new Claim("obsync_admin", "true"));
        }

        var token = new JwtSecurityToken(
            issuer: _issuer,
            audience: _issuer,
            claims: claims,
            expires: DateTime.UtcNow.AddDays(30),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public TokenValidationParameters CreateValidationParameters()
    {
        return new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_key)),
            ValidateIssuer = true,
            ValidIssuer = _issuer,
            ValidateAudience = true,
            ValidAudience = _issuer,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(1)
        };
    }

    private static string ResolveKey(IConfiguration configuration)
    {
        var key = configuration["JWT_SIGNING_KEY"] ?? configuration["Jwt:Key"];
        return key is { Length: >= 32 }
            ? key
            : throw new InvalidOperationException("JWT_SIGNING_KEY must be configured with at least 32 characters before the server starts.");
    }
}
