using GqlGateway.Application.Interfaces;
using GqlGateway.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace GqlGateway.Infrastructure.RateLimiting;

public sealed class RedisRateLimiterService : IRateLimiterService
{
    private readonly IConnectionMultiplexer _multiplexer;
    private readonly IDatabase _db;
    private readonly ILogger<RedisRateLimiterService> _logger;
    private readonly string _prefix;

    private const string PreAuthIpScript = @"
        local current = redis.call('INCR', KEYS[1])
        if current == 1 then
            redis.call('EXPIRE', KEYS[1], ARGV[1])
        end
        local ttl = redis.call('TTL', KEYS[1])
        return { current, ttl }
    ";

    private const string PostAuthSidScript = @"
        local key = KEYS[1]
        local capacity = tonumber(ARGV[1])
        local refill_rate = tonumber(ARGV[2])
        local now_ms = tonumber(ARGV[3])
        local ttl_seconds = tonumber(ARGV[4])

        local data = redis.call('HMGET', key, 'tokens', 'last_refill')
        local tokens = tonumber(data[1])
        local last_refill = tonumber(data[2])

        if not tokens then
            tokens = capacity
            last_refill = now_ms
        else
            local elapsed_sec = math.max(0, (now_ms - last_refill) / 1000.0)
            tokens = math.min(capacity, tokens + (elapsed_sec * refill_rate))
            last_refill = now_ms
        end

        local allowed = 0
        local wait_seconds = 1
        if tokens >= 1.0 then
            tokens = tokens - 1.0
            allowed = 1
        else
            local missing = 1.0 - tokens
            local rate = refill_rate > 0 and refill_rate or 1
            wait_seconds = math.max(1, math.ceil(missing / rate))
        end

        redis.call('HSET', key, 'tokens', tostring(tokens), 'last_refill', tostring(last_refill))
        redis.call('EXPIRE', key, ttl_seconds)
        return { allowed, wait_seconds }
    ";

    private static readonly LuaScript PreparedPreAuthIpScript = LuaScript.Prepare(PreAuthIpScript);
    private static readonly LuaScript PreparedPostAuthSidScript = LuaScript.Prepare(PostAuthSidScript);

    public RedisRateLimiterService(
        IConnectionMultiplexer multiplexer,
        IOptions<GatewayOptions> options,
        ILogger<RedisRateLimiterService> logger)
    {
        _multiplexer = multiplexer;
        _db = multiplexer.GetDatabase();
        _logger = logger;
        _prefix = options.Value.Caching.Redis.InstanceName;
        if (!_prefix.EndsWith(':'))
        {
            _prefix += ":";
        }
    }

    public async Task<RateLimitResult> CheckPreAuthIpAsync(string ip, PreAuthIpRateLimitOptions options, CancellationToken ct = default)
    {
        try
        {
            var sanitizedIp = ip.Replace("{", "_").Replace("}", "_");
            var key = (RedisKey)$"{_prefix}ratelimit:ip:{sanitizedIp}";
            var res = (RedisResult[]?)await PreparedPreAuthIpScript.EvaluateAsync(
                _db,
                new { KEYS = new RedisKey[] { key }, ARGV = new RedisValue[] { (RedisValue)options.WindowSeconds } }
            ).ConfigureAwait(false);

            if (res != null && res.Length >= 2)
            {
                var count = (long)res[0];
                var ttl = (long)res[1];
                var retryAfter = Math.Max(1, (int)ttl);

                return new RateLimitResult(count <= options.PermitLimit, retryAfter);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Redis rate limiting failed for IP {Ip}. Degrading gracefully (fail-open for rate limiter only).", ip);
        }

        return new RateLimitResult(true, 0);
    }

    public async Task<RateLimitResult> CheckPostAuthSidAsync(string sid, PostAuthSidRateLimitOptions options, CancellationToken ct = default)
    {
        try
        {
            var sanitizedSid = sid.Replace("{", "_").Replace("}", "_");
            var key = (RedisKey)$"{_prefix}ratelimit:sid:{sanitizedSid}";
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var ttlSeconds = 600; // 10 minutes idle expiry

            var res = (RedisResult[]?)await PreparedPostAuthSidScript.EvaluateAsync(
                _db,
                new
                {
                    KEYS = new RedisKey[] { key },
                    ARGV = new RedisValue[]
                    {
                        (RedisValue)options.TokenBucketCapacity,
                        (RedisValue)options.TokensPerSecond,
                        (RedisValue)nowMs,
                        (RedisValue)ttlSeconds
                    }
                }
            ).ConfigureAwait(false);

            if (res != null && res.Length >= 2)
            {
                var allowed = (long)res[0] == 1;
                var waitSeconds = Math.Max(1, (int)(long)res[1]);

                return new RateLimitResult(allowed, waitSeconds);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Redis post-auth rate limiting failed for SID {Sid}. Degrading gracefully.", sid);
        }

        return new RateLimitResult(true, 0);
    }
}
