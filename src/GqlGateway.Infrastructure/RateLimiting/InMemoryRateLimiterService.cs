using System.Collections.Concurrent;
using System.Diagnostics;
using GqlGateway.Application.Interfaces;
using GqlGateway.Domain.Options;
using Microsoft.Extensions.Hosting;

namespace GqlGateway.Infrastructure.RateLimiting;

public sealed class InMemoryRateLimiterService : IRateLimiterService
{
    private sealed class IpCounter
    {
        public int Count;
        public long WindowStartTimestamp = Stopwatch.GetTimestamp();
        public readonly object Lock = new();
    }

    private sealed class TokenBucket
    {
        public double Tokens;
        public long LastRefillTimestamp = Stopwatch.GetTimestamp();
        public readonly object Lock = new();
    }

    private readonly ConcurrentDictionary<string, IpCounter> _ipCounters = new();
    private readonly ConcurrentDictionary<string, TokenBucket> _buckets = new(StringComparer.OrdinalIgnoreCase);

    private int _ipCount;
    private int _bucketCount;

    private long _lastIpCleanup = Stopwatch.GetTimestamp();
    private int _isCleaningUpIp;

    private long _lastBucketCleanup = Stopwatch.GetTimestamp();
    private int _isCleaningUpBuckets;

    private readonly CancellationToken _stoppingToken;

    public InMemoryRateLimiterService(IHostApplicationLifetime? lifetime = null)
    {
        _stoppingToken = lifetime?.ApplicationStopping ?? CancellationToken.None;
    }

    public Task<RateLimitResult> CheckPreAuthIpAsync(string ip, PreAuthIpRateLimitOptions options, CancellationToken ct = default)
    {
        var currentTimestamp = Stopwatch.GetTimestamp();
        var windowDuration = TimeSpan.FromSeconds(options.WindowSeconds);

        if (Volatile.Read(ref _ipCount) > 5000 &&
            Stopwatch.GetElapsedTime(_lastIpCleanup, currentTimestamp) > TimeSpan.FromSeconds(60) &&
            Interlocked.CompareExchange(ref _isCleaningUpIp, 1, 0) == 0)
        {
            _lastIpCleanup = currentTimestamp;
            _ = Task.Run(() =>
            {
                try
                {
                    var staleThreshold = windowDuration * 2;
                    foreach (var kvp in _ipCounters)
                    {
                        if (_stoppingToken.IsCancellationRequested) break;
                        long ws;
                        lock (kvp.Value.Lock)
                        {
                            ws = kvp.Value.WindowStartTimestamp;
                        }
                        if (Stopwatch.GetElapsedTime(ws, Stopwatch.GetTimestamp()) > staleThreshold)
                        {
                            if (_ipCounters.TryRemove(kvp.Key, out _))
                            {
                                Interlocked.Decrement(ref _ipCount);
                            }
                        }
                    }
                }
                finally
                {
                    Interlocked.Exchange(ref _isCleaningUpIp, 0);
                }
            }, _stoppingToken);
        }

        if (!_ipCounters.TryGetValue(ip, out var counter))
        {
            if (Volatile.Read(ref _ipCount) >= 25000)
            {
                return Task.FromResult(new RateLimitResult(false, 60));
            }

            var newCounter = new IpCounter();
            if (_ipCounters.TryAdd(ip, newCounter))
            {
                Interlocked.Increment(ref _ipCount);
                counter = newCounter;
            }
            else
            {
                counter = _ipCounters[ip];
            }
        }
        bool rateLimitExceeded = false;
        int retryAfterSeconds = options.WindowSeconds;

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
                if (counter.Count > options.PermitLimit)
                {
                    rateLimitExceeded = true;
                    var remaining = windowDuration - elapsed;
                    retryAfterSeconds = Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds));
                }
            }
        }

        return Task.FromResult(new RateLimitResult(!rateLimitExceeded, retryAfterSeconds));
    }

    public Task<RateLimitResult> CheckPostAuthSidAsync(string sid, PostAuthSidRateLimitOptions options, CancellationToken ct = default)
    {
        var currentTimestamp = Stopwatch.GetTimestamp();

        if (Volatile.Read(ref _bucketCount) > 5000 &&
            Stopwatch.GetElapsedTime(_lastBucketCleanup, currentTimestamp) > TimeSpan.FromSeconds(60) &&
            Interlocked.CompareExchange(ref _isCleaningUpBuckets, 1, 0) == 0)
        {
            _lastBucketCleanup = currentTimestamp;
            _ = Task.Run(() =>
            {
                try
                {
                    var staleThreshold = TimeSpan.FromMinutes(10);
                    foreach (var kvp in _buckets)
                    {
                        if (_stoppingToken.IsCancellationRequested) break;
                        long lastRefill;
                        lock (kvp.Value.Lock)
                        {
                            lastRefill = kvp.Value.LastRefillTimestamp;
                        }
                        if (Stopwatch.GetElapsedTime(lastRefill, Stopwatch.GetTimestamp()) > staleThreshold)
                        {
                            if (_buckets.TryRemove(kvp.Key, out _))
                            {
                                Interlocked.Decrement(ref _bucketCount);
                            }
                        }
                    }
                }
                finally
                {
                    Interlocked.Exchange(ref _isCleaningUpBuckets, 0);
                }
            }, _stoppingToken);
        }

        if (!_buckets.TryGetValue(sid, out var bucket))
        {
            if (Volatile.Read(ref _bucketCount) >= 25000)
            {
                return Task.FromResult(new RateLimitResult(false, 60));
            }

            var newBucket = new TokenBucket
            {
                Tokens = options.TokenBucketCapacity,
                LastRefillTimestamp = currentTimestamp
            };

            if (_buckets.TryAdd(sid, newBucket))
            {
                Interlocked.Increment(ref _bucketCount);
                bucket = newBucket;
            }
            else
            {
                bucket = _buckets[sid];
            }
        }

        bool limitExceeded = false;
        int waitSeconds = 1;

        lock (bucket.Lock)
        {
            var elapsedSeconds = Stopwatch.GetElapsedTime(bucket.LastRefillTimestamp, currentTimestamp).TotalSeconds;
            bucket.Tokens = Math.Min(options.TokenBucketCapacity, bucket.Tokens + (elapsedSeconds * options.TokensPerSecond));
            bucket.LastRefillTimestamp = currentTimestamp;

            if (bucket.Tokens >= 1.0)
            {
                bucket.Tokens -= 1.0;
            }
            else
            {
                limitExceeded = true;
                var missingTokens = 1.0 - bucket.Tokens;
                var refillRate = options.TokensPerSecond > 0 ? options.TokensPerSecond : 1;
                waitSeconds = Math.Max(1, (int)Math.Ceiling(missingTokens / refillRate));
            }
        }

        return Task.FromResult(new RateLimitResult(!limitExceeded, waitSeconds));
    }
}
