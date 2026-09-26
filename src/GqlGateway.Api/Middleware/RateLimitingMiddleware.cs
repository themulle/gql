using System.Security.Claims;
using System.Text.Json;
using GqlGateway.Application.Interfaces;
using GqlGateway.Domain.Common;
using GqlGateway.Domain.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace GqlGateway.Api.Middleware;

public sealed class PreAuthIpRateLimitingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly PreAuthIpRateLimitOptions _options;
    private readonly IRateLimiterService _rateLimiter;

    public PreAuthIpRateLimitingMiddleware(
        RequestDelegate next,
        IOptions<GatewayOptions> options,
        IRateLimiterService rateLimiter)
    {
        _next = next;
        _options = options.Value.RateLimiting.PreAuthIpRateLimit;
        _rateLimiter = rateLimiter;
    }

    internal static readonly Prometheus.Counter RateLimitExceededCounter = Prometheus.Metrics.CreateCounter(
        "gqlgateway_ratelimit_rejected_total", "Rate limit rejections count", new Prometheus.CounterConfiguration
        {
            LabelNames = new[] { "type" }
        });

    public async Task InvokeAsync(HttpContext context)
    {
        // Skip health and metrics endpoints
        if (context.Request.Path.StartsWithSegments("/health") || context.Request.Path.StartsWithSegments("/metrics"))
        {
            await _next(context);
            return;
        }

        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "127.0.0.1";
        var result = await _rateLimiter.CheckPreAuthIpAsync(ip, _options, context.RequestAborted);

        if (!result.Allowed)
        {
            RateLimitExceededCounter.WithLabels("pre_auth_ip").Inc();
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.Response.Headers.RetryAfter = result.RetryAfterSeconds.ToString();
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(JsonSerializer.Serialize(new
            {
                errors = new[]
                {
                    new
                    {
                        message = "Pre-Auth IP Rate Limit überschritten. Bitte warten.",
                        extensions = new { code = "RATE_LIMIT_EXCEEDED" }
                    }
                }
            }));
            return;
        }

        await _next(context);
    }
}

public sealed class PostAuthSidRateLimitingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly PostAuthSidRateLimitOptions _options;
    private readonly IRateLimiterService _rateLimiter;

    public PostAuthSidRateLimitingMiddleware(
        RequestDelegate next,
        IOptions<GatewayOptions> options,
        IRateLimiterService rateLimiter)
    {
        _next = next;
        _options = options.Value.RateLimiting.PostAuthSidRateLimit;
        _rateLimiter = rateLimiter;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Skip health and metrics endpoints
        if (context.Request.Path.StartsWithSegments("/health") || context.Request.Path.StartsWithSegments("/metrics"))
        {
            await _next(context);
            return;
        }

        if (context.User.Identity?.IsAuthenticated != true)
        {
            // Anonymous requests proceed to endpoint for authorization / authentication challenge
            await _next(context);
            return;
        }

        var sid = context.User.GetUserSid()?.Value;
        if (string.IsNullOrEmpty(sid))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(JsonSerializer.Serialize(new
            {
                errors = new[]
                {
                    new
                    {
                        message = "Zero-Trust Error: Authenticated user lacks a valid SID claim (PrimarySid, objectSid, or NameIdentifier).",
                        extensions = new { code = "UNAUTHORIZED_NO_SID" }
                    }
                }
            }));
            return;
        }

        var result = await _rateLimiter.CheckPostAuthSidAsync(sid, _options, context.RequestAborted);
        if (!result.Allowed)
        {
            PreAuthIpRateLimitingMiddleware.RateLimitExceededCounter.WithLabels("post_auth_sid").Inc();
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.Response.Headers.RetryAfter = result.RetryAfterSeconds.ToString();
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(JsonSerializer.Serialize(new
            {
                errors = new[]
                {
                    new
                    {
                        message = "Benutzerbezogenes SID-Rate-Limit überschritten.",
                        extensions = new { code = "RATE_LIMIT_EXCEEDED" }
                    }
                }
            }));
            return;
        }

        await _next(context);
    }
}
