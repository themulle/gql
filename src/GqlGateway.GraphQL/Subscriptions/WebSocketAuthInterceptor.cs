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
            // Read connection_init payload: {"authorization": "Bearer ..."} or {"x-api-key": "..."}
            if (connectionInitMessage is IReadOnlyDictionary<string, object?> payloadDict)
            {
                var token = ExtractToken(payloadDict);
                if (!string.IsNullOrWhiteSpace(token))
                {
                    return ConnectionStatus.Accept();
                }
            }

            // Fallback: If session already has an authenticated HttpContext user
            if (session.Connection.HttpContext?.User.Identity?.IsAuthenticated == true)
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
        var identity = new ClaimsIdentity("WebSocketAuth");
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, token));
        identity.AddClaim(new Claim("sub", token));
        identity.AddClaim(new Claim(ClaimTypes.PrimarySid, token.StartsWith("S-", StringComparison.OrdinalIgnoreCase) ? token : $"S-1-5-21-{token}"));
        identity.AddClaim(new Claim("tenant_id", "default"));

        return new ClaimsPrincipal(identity);
    }
}
