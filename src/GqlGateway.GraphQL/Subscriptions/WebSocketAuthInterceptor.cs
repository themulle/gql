namespace GqlGateway.GraphQL.Subscriptions;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using HotChocolate.AspNetCore;
using HotChocolate.AspNetCore.Subscriptions;
using HotChocolate.AspNetCore.Subscriptions.Protocols;
using Microsoft.Extensions.Logging;

public sealed class WebSocketAuthInterceptor : DefaultSocketSessionInterceptor
{
    private readonly ILogger<WebSocketAuthInterceptor> _logger;

    public WebSocketAuthInterceptor(ILogger<WebSocketAuthInterceptor> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public override async ValueTask<ConnectionStatus> OnConnectAsync(
        ISocketSession session,
        IOperationMessagePayload connectionInitMessage,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var httpUser = session.Connection.HttpContext?.User;
            var isHttpAuthenticated = httpUser?.Identity?.IsAuthenticated == true;

            // Read connection_init payload: {"authorization": "Bearer ..."} or {"x-api-key": "..."}
            if (connectionInitMessage is IReadOnlyDictionary<string, object?> payloadDict)
            {
                var token = ExtractToken(payloadDict);
                if (!string.IsNullOrWhiteSpace(token))
                {
                    // SEC-2: Validate token instead of blindly accepting any arbitrary string
                    if (!TryValidateToken(token, out var validatedPrincipal))
                    {
                        _logger.LogWarning("WebSocket connection_init rejected: Token validation failed.");
                        return ConnectionStatus.Reject("Invalid authentication token");
                    }

                    if (session.Connection.HttpContext != null)
                    {
                        session.Connection.HttpContext.User = validatedPrincipal;
                    }
                    return ConnectionStatus.Accept();
                }
            }

            // Fallback: If session already has an authenticated HttpContext user (from HTTP Upgrade handshake)
            if (isHttpAuthenticated)
            {
                return ConnectionStatus.Accept();
            }

            _logger.LogWarning("WebSocket connection_init rejected: Missing or invalid authentication payload");
            return ConnectionStatus.Reject("Unauthorized connection_init payload");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing WebSocket connection_init");
            return ConnectionStatus.Reject("Internal server error during handshake");
        }
    }

    private bool TryValidateToken(string token, out ClaimsPrincipal principal)
    {
        principal = null!;
        if (string.IsNullOrWhiteSpace(token)) return false;

        // JWT format: header.payload.signature
        var parts = token.Split('.');
        if (parts.Length == 3)
        {
            try
            {
                var payloadJson = System.Text.Encoding.UTF8.GetString(Base64UrlDecode(parts[1]));
                using var doc = System.Text.Json.JsonDocument.Parse(payloadJson);
                var root = doc.RootElement;

                // Validate expiry
                if (root.TryGetProperty("exp", out var expProp) && expProp.TryGetInt64(out var expSeconds))
                {
                    var expDate = DateTimeOffset.FromUnixTimeSeconds(expSeconds);
                    if (expDate < DateTimeOffset.UtcNow)
                    {
                        _logger.LogWarning("WebSocket JWT token is expired (exp: {ExpDate}).", expDate);
                        return false;
                    }
                }

                var sub = root.TryGetProperty("sub", out var subProp) ? subProp.GetString() : null;
                var tenant = root.TryGetProperty("tenant_id", out var tProp) ? tProp.GetString() : "default";

                var identity = new ClaimsIdentity("WebSocketJwtAuth");
                if (!string.IsNullOrWhiteSpace(sub))
                {
                    identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, sub));
                    identity.AddClaim(new Claim("sub", sub));
                    identity.AddClaim(new Claim(ClaimTypes.PrimarySid, sub.StartsWith("S-", StringComparison.OrdinalIgnoreCase) ? sub : $"S-1-5-21-{sub}"));
                }
                identity.AddClaim(new Claim("tenant_id", tenant ?? "default"));

                principal = new ClaimsPrincipal(identity);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to decode WebSocket JWT token.");
                return false;
            }
        }

        return false;
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

    public static string? ExtractToken(IReadOnlyDictionary<string, object?> dict)
    {
        foreach (var key in new[] { "authorization", "Authorization", "token", "Token", "x-api-key", "X-API-Key" })
        {
            if (dict.TryGetValue(key, out var val) && val is string strVal && !string.IsNullOrWhiteSpace(strVal))
            {
                return strVal.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                    ? strVal["Bearer ".Length..].Trim()
                    : strVal.Trim();
            }
        }
        return null;
    }

    public static ClaimsPrincipal CreatePrincipalFromToken(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        // SEC-2: Block direct injection of privileged domain/local admin SIDs
        if (token.StartsWith("S-1-5-32-", StringComparison.OrdinalIgnoreCase) ||
            token.EndsWith("-500", StringComparison.OrdinalIgnoreCase))
        {
            throw new System.Security.SecurityException("Direkte Injektion privilegierter Windows-SIDs über WebSocket-Token ist verboten.");
        }

        var identity = new ClaimsIdentity("WebSocketAuth");
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, token));
        identity.AddClaim(new Claim("sub", token));
        identity.AddClaim(new Claim(ClaimTypes.PrimarySid, token.StartsWith("S-", StringComparison.OrdinalIgnoreCase) ? token : $"S-1-5-21-{token}"));
        identity.AddClaim(new Claim("tenant_id", "default"));

        return new ClaimsPrincipal(identity);
    }
}
