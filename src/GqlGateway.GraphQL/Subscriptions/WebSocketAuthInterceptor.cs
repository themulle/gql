namespace GqlGateway.GraphQL.Subscriptions;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GqlGateway.Application.Interfaces;
using HotChocolate.AspNetCore;
using HotChocolate.AspNetCore.Subscriptions;
using HotChocolate.AspNetCore.Subscriptions.Protocols;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

public sealed class WebSocketAuthInterceptor : DefaultSocketSessionInterceptor
{
    /// <summary>
    /// SEC M-14: Obergrenze für die Lebensdauer einer WebSocket-Session, wenn kein Token-Ablauf bekannt ist
    /// (z.B. Kerberos/Negotiate). Clients müssen sich danach neu verbinden und neu authentifizieren.
    /// </summary>
    public static readonly TimeSpan DefaultMaxSessionLifetime = TimeSpan.FromHours(8);

    internal const string SessionExpiresAtItemKey = "GqlGateway.WebSocket.SessionExpiresAt";

    private static readonly string[] TokenKeys = ["authorization", "Authorization", "token", "Token", "x-api-key", "X-API-Key"];

    private readonly ILogger<WebSocketAuthInterceptor> _logger;
    private readonly ISocketTokenValidator? _tokenValidator;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _maxSessionLifetime;

    public WebSocketAuthInterceptor(
        ILogger<WebSocketAuthInterceptor> logger,
        ISocketTokenValidator? tokenValidator = null,
        TimeProvider? timeProvider = null,
        TimeSpan? maxSessionLifetime = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _tokenValidator = tokenValidator;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _maxSessionLifetime = maxSessionLifetime is { } lifetime && lifetime > TimeSpan.Zero ? lifetime : DefaultMaxSessionLifetime;
    }

    public override async ValueTask<ConnectionStatus> OnConnectAsync(
        ISocketSession session,
        IOperationMessagePayload connectionInitMessage,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var httpContext = session.Connection.HttpContext;
            var httpUser = httpContext?.User;
            var isHttpAuthenticated = httpUser?.Identity?.IsAuthenticated == true;

            // Read connection_init payload: {"authorization": "Bearer ..."} or {"x-api-key": "..."}
            // SEC M-14: Payload über IOperationMessagePayload.As<T>() lesen (frühere Typprüfung auf IReadOnlyDictionary griff nie).
            var token = ExtractTokenFromPayload(connectionInitMessage);
            if (!string.IsNullOrWhiteSpace(token))
            {
                var validator = _tokenValidator ?? httpContext?.RequestServices.GetService<ISocketTokenValidator>();
                if (validator == null)
                {
                    _logger.LogWarning("WebSocket connection_init rejected: No ISocketTokenValidator registered.");
                    return ConnectionStatus.Reject("Authentication validator unavailable");
                }

                var (isValid, validatedPrincipal) = await validator.ValidateTokenAsync(token, cancellationToken).ConfigureAwait(false);
                if (!isValid || validatedPrincipal == null)
                {
                    _logger.LogWarning("WebSocket connection_init rejected: Cryptographic token validation failed.");
                    return ConnectionStatus.Reject("Invalid authentication token");
                }

                var tokenExpiry = ResolveSessionExpiry(validatedPrincipal, null, _timeProvider.GetUtcNow(), _maxSessionLifetime);
                if (tokenExpiry <= _timeProvider.GetUtcNow())
                {
                    _logger.LogWarning("WebSocket connection_init rejected: Token already expired.");
                    return ConnectionStatus.Reject("Authentication token expired");
                }

                if (httpContext != null)
                {
                    httpContext.User = validatedPrincipal;
                    ScheduleSessionExpiry(httpContext, tokenExpiry);
                }
                return ConnectionStatus.Accept();
            }

            // Fallback: If session already has an authenticated HttpContext user (from HTTP Upgrade handshake)
            if (isHttpAuthenticated && httpContext != null)
            {
                var authExpiresUtc = httpContext.Features.Get<IAuthenticateResultFeature>()?.AuthenticateResult?.Properties?.ExpiresUtc;
                var expiry = ResolveSessionExpiry(httpUser, authExpiresUtc, _timeProvider.GetUtcNow(), _maxSessionLifetime);
                if (expiry <= _timeProvider.GetUtcNow())
                {
                    _logger.LogWarning("WebSocket connection_init rejected: HTTP authentication already expired.");
                    return ConnectionStatus.Reject("Authentication token expired");
                }

                ScheduleSessionExpiry(httpContext, expiry);
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

    /// <summary>
    /// SEC M-14: Ermittelt den Ablaufzeitpunkt der Session als Minimum aus <c>exp</c>-Claim,
    /// <see cref="AuthenticationProperties.ExpiresUtc"/> und <paramref name="now"/> + <paramref name="maxLifetime"/>.
    /// </summary>
    public static DateTimeOffset ResolveSessionExpiry(
        ClaimsPrincipal? principal,
        DateTimeOffset? authPropertiesExpiresUtc,
        DateTimeOffset now,
        TimeSpan maxLifetime)
    {
        var expiry = now + maxLifetime;

        var expClaim = principal?.FindFirst("exp")?.Value;
        if (!string.IsNullOrWhiteSpace(expClaim) &&
            long.TryParse(expClaim, NumberStyles.Integer, CultureInfo.InvariantCulture, out var expSeconds) &&
            expSeconds >= DateTimeOffset.MinValue.ToUnixTimeSeconds() &&
            expSeconds <= DateTimeOffset.MaxValue.ToUnixTimeSeconds())
        {
            var exp = DateTimeOffset.FromUnixTimeSeconds(expSeconds);
            if (exp < expiry)
            {
                expiry = exp;
            }
        }

        if (authPropertiesExpiresUtc is { } propertiesExpiry && propertiesExpiry < expiry)
        {
            expiry = propertiesExpiry;
        }

        return expiry;
    }

    private void ScheduleSessionExpiry(HttpContext httpContext, DateTimeOffset expiresAt)
    {
        httpContext.Items[SessionExpiresAtItemKey] = expiresAt;

        var delay = expiresAt - _timeProvider.GetUtcNow();
        if (delay <= TimeSpan.Zero)
        {
            httpContext.Abort();
            return;
        }

        // SEC M-14: Bei Token-Ablauf wird die zugrunde liegende Verbindung (inkl. aller Subscriptions) beendet.
        var expiryCts = new CancellationTokenSource(delay, _timeProvider);
        var registration = expiryCts.Token.Register(static state =>
        {
            if (state is HttpContext ctx)
            {
                ctx.Abort();
            }
        }, httpContext);

        httpContext.Response.RegisterForDispose(registration);
        httpContext.Response.RegisterForDispose(expiryCts);
    }

    private static string? ExtractTokenFromPayload(IOperationMessagePayload connectionInitMessage)
    {
        if (connectionInitMessage is IReadOnlyDictionary<string, object?> payloadDict)
        {
            return ExtractToken(payloadDict);
        }

        if (connectionInitMessage.Payload is { } element && element.ValueKind == JsonValueKind.Object)
        {
            foreach (var key in TokenKeys)
            {
                if (element.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.String)
                {
                    var strVal = prop.GetString();
                    if (!string.IsNullOrWhiteSpace(strVal))
                    {
                        return NormalizeToken(strVal);
                    }
                }
            }
        }

        return null;
    }

    public static string? ExtractToken(IReadOnlyDictionary<string, object?> dict)
    {
        foreach (var key in TokenKeys)
        {
            if (dict.TryGetValue(key, out var val) && val is string strVal && !string.IsNullOrWhiteSpace(strVal))
            {
                return NormalizeToken(strVal);
            }
        }
        return null;
    }

    public static string? ExtractToken(IReadOnlyDictionary<string, JsonElement> dict)
    {
        foreach (var key in TokenKeys)
        {
            if (dict.TryGetValue(key, out var val) && val.ValueKind == JsonValueKind.String)
            {
                var strVal = val.GetString();
                if (!string.IsNullOrWhiteSpace(strVal))
                {
                    return NormalizeToken(strVal);
                }
            }
        }
        return null;
    }

    private static string NormalizeToken(string value) =>
        value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? value["Bearer ".Length..].Trim()
            : value.Trim();
}
