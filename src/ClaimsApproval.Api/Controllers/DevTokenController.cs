using ClaimsApproval.Api.Auth;
using ClaimsApproval.Api.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaimsApproval.Api.Controllers;

/// <summary>
/// Issues signed JWTs for local testing, so the API can be tried without an identity provider.
/// Disabled unless Jwt:EnableDevTokens is true. In production, tokens come from the company IdP.
/// </summary>
[ApiController]
[Route("dev/token")]
[AllowAnonymous]
public class DevTokenController(JwtOptions options) : ControllerBase
{
    private static readonly HashSet<string> KnownRoles = [Roles.Employee, Roles.Manager, Roles.Finance];

    [HttpPost]
    public IActionResult Issue(DevTokenRequest request)
    {
        if (!options.EnableDevTokens) return NotFound();
        var unknown = request.Roles.Where(r => !KnownRoles.Contains(r)).ToList();
        if (unknown.Count > 0)
        {
            return ValidationProblem($"Unknown role(s): {string.Join(", ", unknown)}");
        }

        var token = AuthSetup.IssueToken(options, request.UserId, request.Roles, TimeSpan.FromHours(8));
        return Ok(new { accessToken = token, tokenType = "Bearer", expiresInSeconds = 8 * 3600 });
    }
}
