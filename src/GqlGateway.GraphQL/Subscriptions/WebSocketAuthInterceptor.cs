namespace GqlGateway.GraphQL.Subscriptions;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using GqlGateway.Application.Interfaces;
using HotChocolate.AspNetCore;
using HotChocolate.AspNetCore.Subscriptions;
using HotChocolate.AspNetCore.Subscriptions.Protocols;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

public sealed class WebSocketAuthInterceptor : DefaultSocketSessionInterceptor
{
    private readonly ILogger<WebSocketAuthInterceptor> _logger;
    private readonly ISocketTokenValidator? _tokenValidator;

    public WebSocketAuthInterceptor(
        ILogger<WebSocketAuthInterceptor> logger,
        ISocketTokenValidator? tokenValidator = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _tokenValidator = tokenValidator;
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
                    var validator = _tokenValidator ?? session.Connection.HttpContext?.RequestServices.GetService<ISocketTokenValidator>();
                    if (validator == null)
                    {
                        _logger.LogWarning("WebSocket connection_init rejected: No ISocketTokenValidator registered.");
                        return ConnectionStatus.Reject("Authentication validator unavailable");
                    }

                    var (isValid, validatedPrincipal) = await validator.ValidateTokenAsync(token, cancellationToken);
                    if (!isValid || validatedPrincipal == null)
                    {
                        _logger.LogWarning("WebSocket connection_init rejected: Cryptographic token validation failed.");
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
}
