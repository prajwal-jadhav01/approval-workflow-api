using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace ClaimsApproval.Api.Auth;

public static class Roles
{
    public const string Employee = "employee";
    public const string Manager = "manager";
    public const string Finance = "finance";
}

public class JwtOptions
{
    public string Issuer { get; set; } = "claims-approval";
    public string Audience { get; set; } = "claims-approval";

    /// <summary>HMAC signing key, at least 32 bytes. Comes from configuration or a secret store.</summary>
    public string SigningKey { get; set; } = "";

    /// <summary>When true, POST /dev/token issues tokens for any user. Never enable in production.</summary>
    public bool EnableDevTokens { get; set; }
}

public static class AuthSetup
{
    public static IServiceCollection AddJwtAuth(this IServiceCollection services, IConfiguration config)
    {
        var options = config.GetSection("Jwt").Get<JwtOptions>() ?? new JwtOptions();
        if (Encoding.UTF8.GetByteCount(options.SigningKey) < 32)
        {
            throw new InvalidOperationException("Jwt:SigningKey must be set and at least 32 bytes long.");
        }

        services.AddSingleton(options);
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(o =>
            {
                o.MapInboundClaims = false;
                o.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = options.Issuer,
                    ValidAudience = options.Audience,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.SigningKey)),
                    NameClaimType = JwtRegisteredClaimNames.Sub,
                    RoleClaimType = "role",
                    ClockSkew = TimeSpan.FromSeconds(30),
                };
            });

        services.AddAuthorizationBuilder()
            .AddPolicy(Roles.Employee, p => p.RequireRole(Roles.Employee))
            .AddPolicy(Roles.Manager, p => p.RequireRole(Roles.Manager))
            .AddPolicy(Roles.Finance, p => p.RequireRole(Roles.Finance));
        return services;
    }

    public static string IssueToken(JwtOptions options, string userId, IEnumerable<string> roles, TimeSpan lifetime)
    {
        var claims = new List<Claim> { new(JwtRegisteredClaimNames.Sub, userId) };
        claims.AddRange(roles.Select(r => new Claim("role", r)));
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.SigningKey));
        var token = new JwtSecurityToken(options.Issuer, options.Audience, claims,
            expires: DateTime.UtcNow.Add(lifetime),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
