using System.Collections.Concurrent;
using GqlGateway.Application.Caching.Interfaces;
using GqlGateway.Application.Interfaces;
using GqlGateway.Domain.Common;
using GqlGateway.Domain.Interfaces;
using GqlGateway.Domain.Model;
using GqlGateway.Domain.Options;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace GqlGateway.Infrastructure.Cache;

public sealed class ConsentCacheService : IConsentCacheService, IDisposable
{
    private readonly IMemoryCache _memoryCache;
    private readonly IEpochValidationService _epochValidationService;
    private readonly IBinaryCacheSerializer? _serializer;
    private readonly IConnectionMultiplexer? _multiplexer;
    private readonly IDatabase? _redisDb;
    private readonly string _prefix;
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _tableCacheKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly IDisposable? _subscription;

    private readonly object _syncLock = new();

    private sealed record CacheEntryEnvelope(TableAccessDecision Decision, long Epoch);

    public ConsentCacheService(
        IMemoryCache memoryCache,
        IEpochValidationService epochValidationService,
        IEventBus eventBus,
        IOptions<GatewayOptions>? options = null,
        IBinaryCacheSerializer? serializer = null,
        IConnectionMultiplexer? multiplexer = null)
    {
        _memoryCache = memoryCache;
        _epochValidationService = epochValidationService;
        _serializer = serializer;
        _multiplexer = multiplexer;
        _redisDb = multiplexer?.GetDatabase();
        _prefix = options?.Value?.Caching?.Redis?.InstanceName ?? "GqlGateway:";
        if (!_prefix.EndsWith(':'))
        {
            _prefix += ":";
        }

        var channel = options?.Value?.Caching?.Redis?.InvalidationChannel ?? "consent:invalidations";
        _subscription = eventBus.Subscribe<string>(channel, async tableString =>
        {
            await Task.Yield();
            var normalized = (tableString ?? string.Empty).ToLowerInvariant();
            lock (_syncLock)
            {
                if (_tableCacheKeys.TryRemove(normalized, out var keys))
                {
                    foreach (var key in keys.Keys)
                    {
                        _memoryCache.Remove(key);
                    }
                }
            }
        });
    }

    private static readonly Prometheus.Counter CacheHits = Prometheus.Metrics.CreateCounter(
        "gqlgateway_consent_cache_hits_total", "Number of ConsentCache hits");
    private static readonly Prometheus.Counter CacheMisses = Prometheus.Metrics.CreateCounter(
        "gqlgateway_consent_cache_misses_total", "Number of ConsentCache misses");

    public Task<TableAccessDecision?> GetCachedDecisionAsync(
        Sid userSid,
        TableIdentifier table,
        string? contextHash = null,
        CancellationToken ct = default)
        => GetCachedDecisionAsync(TenantId.LegacySingleTenant, userSid, table, contextHash, ct);

    public async Task<TableAccessDecision?> GetCachedDecisionAsync(
        TenantId tenant,
        Sid userSid,
        TableIdentifier table,
        string? contextHash = null,
        CancellationToken ct = default)
    {
        var cacheKey = BuildCacheKey(tenant, userSid, table, contextHash);
        if (_memoryCache.TryGetValue(cacheKey, out CacheEntryEnvelope? envelope) && envelope != null)
        {
            // Validate epoch
            var isValid = await _epochValidationService.IsEpochValidAsync(table, envelope.Epoch, ct).ConfigureAwait(false);
            if (!isValid)
            {
                CacheMisses.Inc();
                _memoryCache.Remove(cacheKey);
                RemoveKeyFromTableIndex(table, cacheKey);
                return null;
            }

            CacheHits.Inc();
            return envelope.Decision;
        }

        // L1 Miss: Check L2 Distributed Cache (Redis / Garnet via MemoryPack)
        if (_redisDb != null && _serializer != null)
        {
            try
            {
                var l2Key = (RedisKey)$"{_prefix}consent:l2:{cacheKey}";
                var rawBytes = await _redisDb.StringGetAsync(l2Key).ConfigureAwait(false);
                if (!rawBytes.IsNullOrEmpty && _serializer.TryDeserialize<CachedConsentEnvelope>(rawBytes, out var l2Env) && l2Env != null)
                {
                    var isL2EpochValid = await _epochValidationService.IsEpochValidAsync(table, l2Env.Epoch, ct).ConfigureAwait(false);
                    if (isL2EpochValid)
                    {
                        var decision = l2Env.ToDecision();
                        // Populate L1 cache for subsequent fast-path hits
                        SetL1Internal(table, cacheKey, new CacheEntryEnvelope(decision, l2Env.Epoch), TimeSpan.FromMinutes(5));
                        CacheHits.Inc();
                        return decision;
                    }
                    else
                    {
                        await _redisDb.KeyDeleteAsync(l2Key).ConfigureAwait(false);
                    }
                }
            }
            catch
            {
                // Fallback gracefully on L2 error
            }
        }

        CacheMisses.Inc();
        RemoveKeyFromTableIndex(table, cacheKey);
        return null;
    }

    public Task SetCachedDecisionAsync(
        Sid userSid,
        TableIdentifier table,
        TableAccessDecision decision,
        TimeSpan ttl,
        string? contextHash = null,
        CancellationToken ct = default)
        => SetCachedDecisionAsync(TenantId.LegacySingleTenant, userSid, table, decision, ttl, contextHash, ct);

    public async Task SetCachedDecisionAsync(
        TenantId tenant,
        Sid userSid,
        TableIdentifier table,
        TableAccessDecision decision,
        TimeSpan ttl,
        string? contextHash = null,
        CancellationToken ct = default)
    {
        var cacheKey = BuildCacheKey(tenant, userSid, table, contextHash);
        var currentEpoch = await _epochValidationService.GetCurrentEpochAsync(table, ct).ConfigureAwait(false);
        var envelope = new CacheEntryEnvelope(decision, currentEpoch);

        SetL1Internal(table, cacheKey, envelope, ttl);

        // Store in L2 Distributed Cache (Redis / Garnet via MemoryPack)
        if (_redisDb != null && _serializer != null)
        {
            try
            {
                var l2Key = (RedisKey)$"{_prefix}consent:l2:{cacheKey}";
                var l2Dto = CachedConsentEnvelope.FromDecision(decision, currentEpoch);
                var binaryPayload = _serializer.Serialize(l2Dto);
                await _redisDb.StringSetAsync(l2Key, binaryPayload, ttl).ConfigureAwait(false);
            }
            catch
            {
                // L1 remains active even if L2 fails
            }
        }
    }

    private void SetL1Internal(TableIdentifier table, string cacheKey, CacheEntryEnvelope envelope, TimeSpan ttl)
    {
        var decision = envelope.Decision;

        // Approximate byte size for L1 MemoryCache size budgeting (NF-PERF-02)
        var estimatedBytes = 512
            + (decision.CombinedRowFilterSql?.Length ?? 0) * 2
            + decision.ColumnAccess.Count * 64
            + decision.DeniedReasons.Sum(r => r.Length * 2);

        var tableKey = NormalizeTableKey(table);

        var cacheEntryOptions = new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = ttl,
            Size = Math.Max(256, estimatedBytes)
        };

        cacheEntryOptions.RegisterPostEvictionCallback((evictedKey, _, _, _) =>
        {
            lock (_syncLock)
            {
                if (_tableCacheKeys.TryGetValue(tableKey, out var currentKeys))
                {
                    currentKeys.TryRemove((string)evictedKey, out _);
                    if (currentKeys.IsEmpty)
                    {
                        _tableCacheKeys.TryRemove(tableKey, out _);
                    }
                }
            }
        });

        var keys = _tableCacheKeys.GetOrAdd(tableKey, _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal));
        keys.TryAdd(cacheKey, 0);
        _memoryCache.Set(cacheKey, envelope, cacheEntryOptions);
    }

    public Task EvictTableDecisionsAsync(TableIdentifier table, CancellationToken ct = default)
    {
        var tableKey = NormalizeTableKey(table);
        if (_tableCacheKeys.TryRemove(tableKey, out var keys))
        {
            foreach (var key in keys.Keys)
            {
                _memoryCache.Remove(key);
            }
        }
        return Task.CompletedTask;
    }

    public Task ClearL1CacheAsync(CancellationToken ct = default)
    {
        lock (_syncLock)
        {
            if (_memoryCache is MemoryCache mc)
            {
                mc.Clear();
            }
            _tableCacheKeys.Clear();
        }
        return Task.CompletedTask;
    }

    public int GetTrackedKeyCount(TableIdentifier table)
    {
        var tableKey = NormalizeTableKey(table);
        if (_tableCacheKeys.TryGetValue(tableKey, out var keys))
        {
            return keys.Count;
        }
        return 0;
    }

    private void RemoveKeyFromTableIndex(TableIdentifier table, string cacheKey)
    {
        var tableKey = NormalizeTableKey(table);
        if (_tableCacheKeys.TryGetValue(tableKey, out var keys))
        {
            keys.TryRemove(cacheKey, out _);
            if (keys.IsEmpty)
            {
                _tableCacheKeys.TryRemove(tableKey, out _);
            }
        }
    }

    private static string NormalizeTableKey(TableIdentifier table) => table.ToString().ToLowerInvariant();

    public static string ComputeSubjectContextHash(IReadOnlySet<Sid>? groupSids, IReadOnlySet<string>? roles) =>
        IConsentCacheService.ComputeSubjectContextHash(groupSids, roles);

    private static string BuildCacheKey(TenantId tenant, Sid userSid, TableIdentifier table, string? contextHash = null)
    {
        var safeSid = Uri.EscapeDataString(userSid.Value.ToUpperInvariant());
        var safeContext = Uri.EscapeDataString(string.IsNullOrWhiteSpace(contextHash) ? "default" : contextHash);
        return $"{tenant.Value}:consent:{safeSid}:{safeContext}:{table.Domain.ToLowerInvariant()}:{table.Schema.ToLowerInvariant()}:{table.TableName.ToLowerInvariant()}";
    }

    public void Dispose()
    {
        _subscription?.Dispose();
    }
}
