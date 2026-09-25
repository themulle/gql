using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using GqlGateway.Domain.Common;
using GqlGateway.Domain.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace GqlGateway.Api.Middleware;

public sealed class PreAuthIpRateLimitingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly PreAuthIpRateLimitOptions _options;
    private static readonly ConcurrentDictionary<string, IpCounter> IpCounters = new();

    private sealed class IpCounter
    {
        public int Count;
        public long WindowStartTimestamp = Stopwatch.GetTimestamp();
        public readonly object Lock = new();
    }

    private static long _lastCleanupTimestamp = Stopwatch.GetTimestamp();
    private static int _isCleaningUp;
    private readonly CancellationToken _stoppingToken;

    public PreAuthIpRateLimitingMiddleware(
        RequestDelegate next,
        IOptions<GatewayOptions> options,
        Microsoft.Extensions.Hosting.IHostApplicationLifetime? lifetime = null)
    {
        _next = next;
        _options = options.Value.RateLimiting.PreAuthIpRateLimit;
        _stoppingToken = lifetime?.ApplicationStopping ?? CancellationToken.None;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Skip health endpoints
        if (context.Request.Path.StartsWithSegments("/health"))
        {
            await _next(context);
            return;
        }

        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "127.0.0.1";
        var currentTimestamp = Stopwatch.GetTimestamp();
        var windowDuration = TimeSpan.FromSeconds(_options.WindowSeconds);

        if (IpCounters.Count > 5000 &&
            Stopwatch.GetElapsedTime(_lastCleanupTimestamp, currentTimestamp) > TimeSpan.FromSeconds(60) &&
            Interlocked.CompareExchange(ref _isCleaningUp, 1, 0) == 0)
        {
            _lastCleanupTimestamp = currentTimestamp;
            _ = Task.Run(() =>
            {
                try
                {
                    var staleThresholdDuration = windowDuration * 2;
                    foreach (var kvp in IpCounters)
                    {
                        if (_stoppingToken.IsCancellationRequested) break;
                        long ws;
                        lock (kvp.Value.Lock)
                        {
                            ws = kvp.Value.WindowStartTimestamp;
                        }
                        if (Stopwatch.GetElapsedTime(ws, Stopwatch.GetTimestamp()) > staleThresholdDuration)
                        {
                            IpCounters.TryRemove(kvp.Key, out _);
                        }
                    }
                }
                finally
                {
                    Interlocked.Exchange(ref _isCleaningUp, 0);
                }
            }, _stoppingToken);
        }

        // Bounded capacity check against memory exhaustion attacks
        if (IpCounters.Count >= 25000 && !IpCounters.ContainsKey(ip))
        {
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.Response.Headers.RetryAfter = "60";
            return;
        }

        var counter = IpCounters.GetOrAdd(ip, _ => new IpCounter());

        bool rateLimitExceeded = false;
        int retryAfterSeconds = _options.WindowSeconds;

        lock (counter.Lock)
        {
            var elapsed = Stopwatch.GetElapsedTime(counter.WindowStartTimestamp, currentTimestamp);
            if (elapsed > windowDuration)
            {
                counter.WindowStartTimestamp = currentTimestamp;
                counter.Count = 1;
            }
            else
            {
                counter.Count++;
                if (counter.Count > _options.PermitLimit)
                {
                    rateLimitExceeded = true;
                    var remaining = windowDuration - elapsed;
                    retryAfterSeconds = Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds));
                }
            }
        }

        if (rateLimitExceeded)
        {
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.Response.Headers.RetryAfter = retryAfterSeconds.ToString();
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
    private static readonly ConcurrentDictionary<string, TokenBucket> Buckets = new(StringComparer.OrdinalIgnoreCase);
    private static long _lastBucketsCleanupTimestamp = Stopwatch.GetTimestamp();
    private static int _isCleaningUpBuckets = 0;
    private readonly CancellationToken _stoppingToken;

    private sealed class TokenBucket
    {
        public double Tokens;
        public long LastRefillTimestamp = Stopwatch.GetTimestamp();
        public readonly object Lock = new();
    }

    public PostAuthSidRateLimitingMiddleware(
        RequestDelegate next,
        IOptions<GatewayOptions> options,
        Microsoft.Extensions.Hosting.IHostApplicationLifetime? lifetime = null)
    {
        _next = next;
        _options = options.Value.RateLimiting.PostAuthSidRateLimit;
        _stoppingToken = lifetime?.ApplicationStopping ?? CancellationToken.None;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Skip health endpoints
        if (context.Request.Path.StartsWithSegments("/health"))
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

        var currentTimestamp = Stopwatch.GetTimestamp();

        if (Buckets.Count > 5000 &&
            Stopwatch.GetElapsedTime(_lastBucketsCleanupTimestamp, currentTimestamp) > TimeSpan.FromSeconds(60) &&
            Interlocked.CompareExchange(ref _isCleaningUpBuckets, 1, 0) == 0)
        {
            _lastBucketsCleanupTimestamp = currentTimestamp;
            _ = Task.Run(() =>
            {
                try
                {
                    var staleThresholdDuration = TimeSpan.FromMinutes(10);
                    foreach (var kvp in Buckets)
                    {
                        if (_stoppingToken.IsCancellationRequested) break;
                        long lastRefill;
                        lock (kvp.Value.Lock)
                        {
                            lastRefill = kvp.Value.LastRefillTimestamp;
                        }
                        if (Stopwatch.GetElapsedTime(lastRefill, Stopwatch.GetTimestamp()) > staleThresholdDuration)
                        {
                            Buckets.TryRemove(kvp.Key, out _);
                        }
                    }
                }
                finally
                {
                    Interlocked.Exchange(ref _isCleaningUpBuckets, 0);
                }
            }, _stoppingToken);
        }

        // Bounded capacity check
        if (Buckets.Count >= 25000 && !Buckets.ContainsKey(sid))
        {
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.Response.Headers.RetryAfter = "60";
            return;
        }

        var bucket = Buckets.GetOrAdd(sid, _ => new TokenBucket
        {
            Tokens = _options.TokenBucketCapacity,
            LastRefillTimestamp = currentTimestamp
        });

        bool limitExceeded = false;
        int waitSeconds = 1;

        lock (bucket.Lock)
        {
            var elapsedSeconds = Stopwatch.GetElapsedTime(bucket.LastRefillTimestamp, currentTimestamp).TotalSeconds;
            bucket.Tokens = Math.Min(_options.TokenBucketCapacity, bucket.Tokens + (elapsedSeconds * _options.TokensPerSecond));
            bucket.LastRefillTimestamp = currentTimestamp;

            // Simple token cost = 1 per request
            if (bucket.Tokens >= 1.0)
            {
                bucket.Tokens -= 1.0;
            }
            else
            {
                limitExceeded = true;
                var missingTokens = 1.0 - bucket.Tokens;
                var refillRate = _options.TokensPerSecond > 0 ? _options.TokensPerSecond : 1;
                waitSeconds = Math.Max(1, (int)Math.Ceiling(missingTokens / refillRate));
            }
        }

        if (limitExceeded)
        {
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.Response.Headers.RetryAfter = waitSeconds.ToString();
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
