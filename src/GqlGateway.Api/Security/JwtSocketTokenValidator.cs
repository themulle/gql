namespace GqlGateway.Api.Security;

using System;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using GqlGateway.Application.Interfaces;
using GqlGateway.Domain.Options;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

public sealed class JwtSocketTokenValidator : ISocketTokenValidator
{
    private readonly IOptionsMonitor<JwtBearerOptions> _jwtOptionsMonitor;
    private readonly IOptions<GatewayOptions> _gatewayOptions;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<JwtSocketTokenValidator> _logger;
    private readonly JsonWebTokenHandler _tokenHandler = new();

    public JwtSocketTokenValidator(
        IOptionsMonitor<JwtBearerOptions> jwtOptionsMonitor,
        IOptions<GatewayOptions> gatewayOptions,
        IHostEnvironment environment,
        ILogger<JwtSocketTokenValidator> logger)
    {
        _jwtOptionsMonitor = jwtOptionsMonitor ?? throw new ArgumentNullException(nameof(jwtOptionsMonitor));
        _gatewayOptions = gatewayOptions ?? throw new ArgumentNullException(nameof(gatewayOptions));
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<(bool IsValid, ClaimsPrincipal? Principal)> ValidateTokenAsync(string token, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return (false, null);
        }

        try
        {
            var jwtOptions = _jwtOptionsMonitor.Get(GatewayAuthSchemes.JwtBearer);
            var validationParameters = jwtOptions.TokenValidationParameters?.Clone();

            if (validationParameters != null &&
                (validationParameters.IssuerSigningKey != null || (validationParameters.IssuerSigningKeys != null && validationParameters.IssuerSigningKeys.Any())))
            {
                var validationResult = await _tokenHandler.ValidateTokenAsync(token, validationParameters);
                if (!validationResult.IsValid || validationResult.ClaimsIdentity == null)
                {
                    _logger.LogWarning(validationResult.Exception, "WebSocket JWT cryptographic validation failed: {Reason}", validationResult.Exception?.Message);
                    return (false, null);
                }

                var identity = validationResult.ClaimsIdentity;
                var sub = identity.FindFirst("sub")?.Value ?? identity.FindFirst(ClaimTypes.NameIdentifier)?.Value;

                if (!string.IsNullOrWhiteSpace(sub) &&
                    (sub.StartsWith("S-1-5-32-", StringComparison.OrdinalIgnoreCase) || sub.EndsWith("-500", StringComparison.OrdinalIgnoreCase)))
                {
                    _logger.LogWarning("WebSocket connection rejected: Attempted use of privileged SID '{Sub}'.", sub);
                    return (false, null);
                }

                return (true, new ClaimsPrincipal(identity));
            }

            // In non-development environment: reject if no signing keys are configured (Fail-Closed)
            if (!_environment.IsDevelopment())
            {
                _logger.LogWarning("WebSocket token rejected: No cryptographic IssuerSigningKey configured in non-development environment.");
                return (false, null);
            }

            // In Development ONLY: allow test token if EnableTestAuthHandler is explicitly active
            if (_gatewayOptions.Value.Authentication.EnableTestAuthHandler)
            {
                var parts = token.Split('.');
                if (parts.Length == 3)
                {
                    var payloadJson = System.Text.Encoding.UTF8.GetString(Base64UrlDecode(parts[1]));
                    using var doc = System.Text.Json.JsonDocument.Parse(payloadJson);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("exp", out var expProp) && expProp.TryGetInt64(out var expSeconds))
                    {
                        var expDate = DateTimeOffset.FromUnixTimeSeconds(expSeconds);
                        if (expDate < DateTimeOffset.UtcNow)
                        {
                            _logger.LogWarning("WebSocket JWT dev token is expired (exp: {ExpDate}).", expDate);
                            return (false, null);
                        }
                    }

                    var sub = root.TryGetProperty("sub", out var subProp) ? subProp.GetString() : null;
                    var tenant = root.TryGetProperty("tenant_id", out var tProp) ? tProp.GetString() : "default";

                    var identity = new ClaimsIdentity("WebSocketDevAuth");
                    if (!string.IsNullOrWhiteSpace(sub))
                    {
                        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, sub));
                        identity.AddClaim(new Claim("sub", sub));
                        identity.AddClaim(new Claim(ClaimTypes.PrimarySid, sub.StartsWith("S-", StringComparison.OrdinalIgnoreCase) ? sub : $"S-1-5-21-{sub}"));
                    }
                    identity.AddClaim(new Claim("tenant_id", tenant ?? "default"));

                    return (true, new ClaimsPrincipal(identity));
                }
            }

            return (false, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unexpected error during WebSocket token validation.");
            return (false, null);
        }
    }

    private static byte[] Base64UrlDecode(string input)
    {
        var output = input.Replace('-', '+').Replace('_', '/');
        switch (output.Length % 4)
        {
            case 2: output += "=="; break;
            case 3: output += "="; break;
        }
        return Convert.FromBase64String(output);
    }
}
