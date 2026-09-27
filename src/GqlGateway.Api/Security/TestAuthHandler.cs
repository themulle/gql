using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using GqlGateway.Domain.Options;

namespace GqlGateway.Api.Security;

public sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "TestAuth";

    private readonly IOptions<GatewayOptions>? _gatewayOptions;

    public TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IOptions<GatewayOptions>? gatewayOptions = null)
        : base(options, logger, encoder)
    {
        _gatewayOptions = gatewayOptions;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var headers = Request.Headers;

        if (!headers.TryGetValue("X-Test-User-Sid", out var userSidVal) || string.IsNullOrWhiteSpace(userSidVal))
        {
            if (headers.TryGetValue("X-Test-AppId", out var appIdOnly) && !string.IsNullOrWhiteSpace(appIdOnly))
            {
                userSidVal = appIdOnly;
            }
            else if (_gatewayOptions?.Value.IsAnonymousAccessAllowed == true)
            {
                // Insecure Getting-Started: Allow anonymous access with DeveloperAdmin identity
                List<Claim> anonClaims =
                [
                    new(ClaimTypes.PrimarySid, "S-1-5-21-DEV-ANONYMOUS"),
                    new(ClaimTypes.Name, "DEV_ANONYMOUS"),
                    new(ClaimTypes.NameIdentifier, "S-1-5-21-DEV-ANONYMOUS"),
                    new("objectSid", "S-1-5-21-DEV-ANONYMOUS"),
                    new(ClaimTypes.Role, "DeveloperAdmin"),
                    new(ClaimTypes.Role, "GovernanceAdmin"),
                    new(ClaimTypes.Role, "ClusterAdmin")
                ];
                var anonIdentity = new ClaimsIdentity(anonClaims, SchemeName, ClaimTypes.Name, ClaimTypes.Role);
                var anonPrincipal = new ClaimsPrincipal(anonIdentity);
                var anonTicket = new AuthenticationTicket(anonPrincipal, SchemeName);
                return Task.FromResult(AuthenticateResult.Success(anonTicket));
            }
            else
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }
        }

        var userSid = userSidVal.ToString();

        var userName = headers.TryGetValue("X-Test-User-Name", out var userNameVal) && !string.IsNullOrWhiteSpace(userNameVal)
            ? userNameVal.ToString()
            : $"CORP\\{userSid}";

        List<Claim> claims =
        [
            new(ClaimTypes.PrimarySid, userSid),
            new(ClaimTypes.Name, userName),
            new(ClaimTypes.NameIdentifier, userSid),
            new("objectSid", userSid)
        ];

        if (headers.TryGetValue("X-Test-AppId", out var appIdHeader) && !string.IsNullOrWhiteSpace(appIdHeader))
        {
            claims.Add(new Claim("appid", appIdHeader.ToString().Trim()));
        }

        if (headers.TryGetValue("X-Test-IdTyp", out var idTypHeader) && !string.IsNullOrWhiteSpace(idTypHeader))
        {
            claims.Add(new Claim("idtyp", idTypHeader.ToString().Trim()));
        }

        if (headers.TryGetValue("X-Test-Group-Sids", out var groupSidsVal) && !string.IsNullOrWhiteSpace(groupSidsVal))
        {
            var groups = groupSidsVal.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var g in groups)
            {
                claims.Add(new Claim(ClaimTypes.GroupSid, g));
            }
        }

        if (headers.TryGetValue("X-Test-Roles", out var rolesVal) && !string.IsNullOrWhiteSpace(rolesVal))
        {
            var roles = rolesVal.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var r in roles)
            {
                claims.Add(new Claim(ClaimTypes.Role, r));
            }
        }

        if (headers.TryGetValue("X-Test-Tenant", out var tenantVal) && !string.IsNullOrWhiteSpace(tenantVal))
        {
            claims.Add(new Claim("tenant", tenantVal.ToString().Trim()));
        }

        var identity = new ClaimsIdentity(claims, SchemeName, ClaimTypes.Name, ClaimTypes.Role);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = Microsoft.AspNetCore.Http.StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    }
}
