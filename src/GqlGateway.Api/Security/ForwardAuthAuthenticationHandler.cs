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

        // 1. Identify User Header (Traefik or generic ingress)
        string? username = null;
        if (!string.IsNullOrWhiteSpace(forwardAuthOptions.UserHeader) &&
            headers.TryGetValue(forwardAuthOptions.UserHeader, out var customUserVal) &&
            !string.IsNullOrWhiteSpace(customUserVal))
        {
            username = customUserVal.ToString().Trim();
        }
        else if (headers.TryGetValue("X-Forwarded-User", out var fwdUserVal) && !string.IsNullOrWhiteSpace(fwdUserVal))
        {
            username = fwdUserVal.ToString().Trim();
        }
        else if (headers.TryGetValue("X-Auth-Request-User", out var authUserVal) && !string.IsNullOrWhiteSpace(authUserVal))
        {
            username = authUserVal.ToString().Trim();
        }
        else if (headers.TryGetValue("X-Forwarded-Preferred-Username", out var prefUserVal) && !string.IsNullOrWhiteSpace(prefUserVal))
        {
            username = prefUserVal.ToString().Trim();
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

        // 4. Construct Claims Principal
        var userSid = (username.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase) || Guid.TryParse(username, out _))
            ? username
            : $"S-1-5-21-FORWARD-{username.ToUpperInvariant()}";

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, username),
            new(ClaimTypes.NameIdentifier, username),
            new(ClaimTypes.PrimarySid, userSid),
            new("objectSid", userSid)
        };

        // Extract Email if present
        if (!string.IsNullOrWhiteSpace(forwardAuthOptions.EmailHeader) &&
            headers.TryGetValue(forwardAuthOptions.EmailHeader, out var emailVal) &&
            !string.IsNullOrWhiteSpace(emailVal))
        {
            claims.Add(new Claim(ClaimTypes.Email, emailVal.ToString().Trim()));
        }
        else if (headers.TryGetValue("X-Forwarded-Email", out var fwdEmail) && !string.IsNullOrWhiteSpace(fwdEmail))
        {
            claims.Add(new Claim(ClaimTypes.Email, fwdEmail.ToString().Trim()));
        }

        // Extract Groups (comma or semicolon separated)
        string? groupsRaw = null;
        if (!string.IsNullOrWhiteSpace(forwardAuthOptions.GroupsHeader) &&
            headers.TryGetValue(forwardAuthOptions.GroupsHeader, out var customGroupsVal) &&
            !string.IsNullOrWhiteSpace(customGroupsVal))
        {
            groupsRaw = customGroupsVal.ToString();
        }
        else if (headers.TryGetValue("X-Forwarded-Groups", out var fwdGroupsVal) && !string.IsNullOrWhiteSpace(fwdGroupsVal))
        {
            groupsRaw = fwdGroupsVal.ToString();
        }
        else if (headers.TryGetValue("X-Auth-Request-Groups", out var authGroupsVal) && !string.IsNullOrWhiteSpace(authGroupsVal))
        {
            groupsRaw = authGroupsVal.ToString();
        }

        if (!string.IsNullOrWhiteSpace(groupsRaw))
        {
            var groups = groupsRaw.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var g in groups)
            {
                claims.Add(new Claim(ClaimTypes.GroupSid, g));
            }
        }

        // Extract Roles (comma or semicolon separated)
        string? rolesRaw = null;
        if (!string.IsNullOrWhiteSpace(forwardAuthOptions.RolesHeader) &&
            headers.TryGetValue(forwardAuthOptions.RolesHeader, out var customRolesVal) &&
            !string.IsNullOrWhiteSpace(customRolesVal))
        {
            rolesRaw = customRolesVal.ToString();
        }
        else if (headers.TryGetValue("X-Forwarded-Roles", out var fwdRolesVal) && !string.IsNullOrWhiteSpace(fwdRolesVal))
        {
            rolesRaw = fwdRolesVal.ToString();
        }
        else if (headers.TryGetValue("X-Auth-Request-Roles", out var authRolesVal) && !string.IsNullOrWhiteSpace(authRolesVal))
        {
            rolesRaw = authRolesVal.ToString();
        }

        if (!string.IsNullOrWhiteSpace(rolesRaw))
        {
            var roles = rolesRaw.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var r in roles)
            {
                claims.Add(new Claim(ClaimTypes.Role, r));
            }
        }

        // Extract Tenant
        string? tenant = null;
        if (!string.IsNullOrWhiteSpace(forwardAuthOptions.TenantHeader) &&
            headers.TryGetValue(forwardAuthOptions.TenantHeader, out var customTenantVal) &&
            !string.IsNullOrWhiteSpace(customTenantVal))
        {
            tenant = customTenantVal.ToString().Trim();
        }
        else if (headers.TryGetValue("X-Forwarded-Tenant", out var fwdTenantVal) && !string.IsNullOrWhiteSpace(fwdTenantVal))
        {
            tenant = fwdTenantVal.ToString().Trim();
        }
        else if (headers.TryGetValue("X-Auth-Request-Tenant", out var authTenantVal) && !string.IsNullOrWhiteSpace(authTenantVal))
        {
            tenant = authTenantVal.ToString().Trim();
        }
        else if (headers.TryGetValue("X-Forwarded-Tenant-Id", out var fwdTenantIdVal) && !string.IsNullOrWhiteSpace(fwdTenantIdVal))
        {
            tenant = fwdTenantIdVal.ToString().Trim();
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
