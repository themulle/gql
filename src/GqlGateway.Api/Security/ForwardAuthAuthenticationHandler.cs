using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using GqlGateway.Application.Interfaces;
using GqlGateway.Domain.Common;
using GqlGateway.Domain.Options;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GqlGateway.Api.Security;

public sealed class ForwardAuthAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "ForwardAuth";
    private readonly GatewayOptions _gatewayOptions;
    private readonly IKeyVaultSecretProvider? _secretProvider;
    private readonly ITrustedProxyValidator _proxyValidator;
    private readonly bool _isDevelopment;
    private readonly byte[]? _precomputedSharedSecretBytes;

    public ForwardAuthAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IOptions<GatewayOptions> gatewayOptions,
        ITrustedProxyValidator proxyValidator,
        IKeyVaultSecretProvider? secretProvider = null,
        IWebHostEnvironment? environment = null)
        : base(options, logger, encoder)
    {
        _gatewayOptions = gatewayOptions?.Value ?? new GatewayOptions();
        _proxyValidator = proxyValidator;
        _secretProvider = secretProvider;
        _isDevelopment = environment?.IsDevelopment() ?? false;

        var configuredSecret = _gatewayOptions.Authentication.ForwardAuth.SharedSecret;
        if (!string.IsNullOrWhiteSpace(configuredSecret))
        {
            _precomputedSharedSecretBytes = Encoding.UTF8.GetBytes(configuredSecret);
        }
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var forwardAuthOptions = _gatewayOptions.Authentication.ForwardAuth;
        if (!forwardAuthOptions.Enabled)
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var headers = Request.Headers;

        // 1. Identify User Header (Traefik or generic ingress - strictly configured or default)
        string? username = null;
        var userHeader = !string.IsNullOrWhiteSpace(forwardAuthOptions.UserHeader)
            ? forwardAuthOptions.UserHeader
            : "X-Forwarded-User";

        if (headers.TryGetValue(userHeader, out var userVal) && !string.IsNullOrWhiteSpace(userVal))
        {
            username = userVal.ToString().Trim();
        }

        if (string.IsNullOrWhiteSpace(username))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        // 2. Zero-Trust Verification: Ensure request originates from a trusted reverse proxy / Ingress
        if (forwardAuthOptions.RequireTrustedProxy)
        {
            IPAddress? rawRemoteIp = null;
            if (Context.Items.TryGetValue("OriginalTcpRemoteIp", out var origIpObj))
            {
                if (origIpObj is IPAddress origIp)
                {
                    rawRemoteIp = origIp;
                }
                else if (origIpObj is string origIpStr && IPAddress.TryParse(origIpStr, out var parsedIp))
                {
                    rawRemoteIp = parsedIp;
                }
            }

            rawRemoteIp ??= Context.Connection.RemoteIpAddress;
            var remoteIp = rawRemoteIp ?? (_isDevelopment ? IPAddress.Loopback : null);

            if (remoteIp == null)
            {
                Logger.LogWarning("ForwardAuth rejected: Remote client IP address could not be determined.");
                return Task.FromResult(AuthenticateResult.Fail("Remote client IP address could not be determined."));
            }

            if (!_proxyValidator.IsProxyTrusted(remoteIp))
            {
                Logger.LogWarning(
                    "ForwardAuth rejected: Header spoofing attempt detected. Request came from untrusted IP '{RemoteIp}'.",
                    remoteIp);

                return Task.FromResult(AuthenticateResult.Fail("Untrusted proxy IP for ForwardAuth."));
            }
        }
        else if (!_isDevelopment)
        {
            Logger.LogWarning("ForwardAuth rejected: Disabling RequireTrustedProxy is strictly prohibited outside the Development environment.");
            return Task.FromResult(AuthenticateResult.Fail("Outside of Development, RequireTrustedProxy must be enabled."));
        }

        // 3. Shared Secret Validation (defense-in-depth between Traefik and Gateway)
        bool hasSecretConfigured = _precomputedSharedSecretBytes != null ||
                                   !string.IsNullOrWhiteSpace(forwardAuthOptions.SharedSecretKeyVaultRef);

        if (!_isDevelopment && !hasSecretConfigured)
        {
            Logger.LogWarning("ForwardAuth rejected: Outside of Development environment, a shared secret is strictly required.");
            return Task.FromResult(AuthenticateResult.Fail("Outside of Development, ForwardAuth requires a configured shared secret."));
        }

        if (hasSecretConfigured)
        {
            byte[]? expectedBytes = _precomputedSharedSecretBytes;
            if (expectedBytes == null && !string.IsNullOrWhiteSpace(forwardAuthOptions.SharedSecretKeyVaultRef) && _secretProvider != null)
            {
                expectedBytes = _secretProvider.GetSecretBytes(forwardAuthOptions.SharedSecretKeyVaultRef);
            }

            if (expectedBytes == null || expectedBytes.Length == 0)
            {
                Logger.LogWarning("ForwardAuth rejected: Configured shared secret could not be resolved.");
                return Task.FromResult(AuthenticateResult.Fail("Configured ForwardAuth shared secret could not be resolved."));
            }

            var secretHeader = string.IsNullOrWhiteSpace(forwardAuthOptions.SharedSecretHeader)
                ? "X-Forwarded-Secret"
                : forwardAuthOptions.SharedSecretHeader;

            if (!headers.TryGetValue(secretHeader, out var actualSecretVal) ||
                string.IsNullOrWhiteSpace(actualSecretVal))
            {
                Logger.LogWarning("ForwardAuth rejected: Missing required shared secret header '{SecretHeader}'.", secretHeader);
                return Task.FromResult(AuthenticateResult.Fail("Missing ForwardAuth shared secret header."));
            }

            var actualSecretStr = actualSecretVal.ToString();
            if (actualSecretStr.Length > 512)
            {
                Logger.LogWarning("ForwardAuth rejected: Shared secret header length exceeds maximum permitted limit.");
                return Task.FromResult(AuthenticateResult.Fail("Invalid ForwardAuth shared secret."));
            }

            var actualBytes = Encoding.UTF8.GetBytes(actualSecretStr);
            var hashExpected = SHA256.HashData(expectedBytes);
            var hashActual = SHA256.HashData(actualBytes);

            if (!CryptographicOperations.FixedTimeEquals(hashExpected, hashActual))
            {
                Logger.LogWarning("ForwardAuth rejected: Shared secret mismatch.");
                return Task.FromResult(AuthenticateResult.Fail("Invalid ForwardAuth shared secret."));
            }
        }

        // 4. Construct Claims Principal - Always namespace ForwardAuth user SIDs to prevent arbitrary AD/Windows SID spoofing
        var userSid = username.StartsWith("S-1-5-21-FORWARD-", StringComparison.OrdinalIgnoreCase)
            ? username
            : $"S-1-5-21-FORWARD-{username.ToUpperInvariant()}";

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, username),
            new(ClaimTypes.NameIdentifier, username),
            new(ClaimTypes.PrimarySid, userSid),
            new("objectSid", userSid)
        };

        // Extract Email if present (strictly configured or default)
        var emailHeader = !string.IsNullOrWhiteSpace(forwardAuthOptions.EmailHeader)
            ? forwardAuthOptions.EmailHeader
            : "X-Forwarded-Email";

        if (headers.TryGetValue(emailHeader, out var emailVal) && !string.IsNullOrWhiteSpace(emailVal))
        {
            claims.Add(new Claim(ClaimTypes.Email, emailVal.ToString().Trim()));
        }

        // Extract Groups (strictly configured or default, comma or semicolon separated)
        var groupsHeader = !string.IsNullOrWhiteSpace(forwardAuthOptions.GroupsHeader)
            ? forwardAuthOptions.GroupsHeader
            : "X-Forwarded-Groups";

        string? groupsRaw = null;
        if (headers.TryGetValue(groupsHeader, out var customGroupsVal) && !string.IsNullOrWhiteSpace(customGroupsVal))
        {
            groupsRaw = customGroupsVal.ToString();
        }

        if (!string.IsNullOrWhiteSpace(groupsRaw))
        {
            var groups = groupsRaw.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var g in groups)
            {
                claims.Add(new Claim(ClaimTypes.GroupSid, g));
            }
        }

        // Extract Roles (strictly configured or default, comma or semicolon separated)
        var rolesHeader = !string.IsNullOrWhiteSpace(forwardAuthOptions.RolesHeader)
            ? forwardAuthOptions.RolesHeader
            : "X-Forwarded-Roles";

        string? rolesRaw = null;
        if (headers.TryGetValue(rolesHeader, out var customRolesVal) && !string.IsNullOrWhiteSpace(customRolesVal))
        {
            rolesRaw = customRolesVal.ToString();
        }

        if (!string.IsNullOrWhiteSpace(rolesRaw))
        {
            var roles = rolesRaw.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var r in roles)
            {
                claims.Add(new Claim(ClaimTypes.Role, r));
            }
        }

        // Extract Tenant (strictly configured or default)
        var tenantHeader = !string.IsNullOrWhiteSpace(forwardAuthOptions.TenantHeader)
            ? forwardAuthOptions.TenantHeader
            : "X-Forwarded-Tenant";

        string? tenant = null;
        if (headers.TryGetValue(tenantHeader, out var customTenantVal) && !string.IsNullOrWhiteSpace(customTenantVal))
        {
            tenant = customTenantVal.ToString().Trim();
        }
        else if (!string.IsNullOrWhiteSpace(forwardAuthOptions.DefaultTenantId))
        {
            tenant = forwardAuthOptions.DefaultTenantId;
        }
        else
        {
            tenant = TenantId.LegacySingleTenant.Value;
        }

        claims.Add(new Claim("tenant_id", tenant));
        claims.Add(new Claim("tenant", tenant));
        claims.Add(new Claim("tid", tenant));

        // Map default enterprise user role if none assigned
        if (!claims.Any(c => c.Type == ClaimTypes.Role))
        {
            claims.Add(new Claim(ClaimTypes.Role, "GatewayUser"));
        }

        var identity = new ClaimsIdentity(claims, SchemeName, ClaimTypes.Name, ClaimTypes.Role);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
