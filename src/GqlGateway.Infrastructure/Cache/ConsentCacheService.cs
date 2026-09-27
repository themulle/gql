using System.Collections.Concurrent;
using GqlGateway.Application.Interfaces;
using GqlGateway.Domain.Common;
using GqlGateway.Domain.Interfaces;
using GqlGateway.Domain.Options;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace GqlGateway.Infrastructure.Cache;

public sealed class ConsentCacheService : IConsentCacheService, IDisposable
{
    private readonly IMemoryCache _memoryCache;
    private readonly IEpochValidationService _epochValidationService;
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _tableCacheKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly IDisposable? _subscription;

    private readonly object _syncLock = new();

    private sealed record CacheEntryEnvelope(TableAccessDecision Decision, long Epoch);

    public ConsentCacheService(
        IMemoryCache memoryCache,
        IEpochValidationService epochValidationService,
        IEventBus eventBus,
        IOptions<GatewayOptions>? options = null)
    {
        _memoryCache = memoryCache;
        _epochValidationService = epochValidationService;

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
        if (!_memoryCache.TryGetValue(cacheKey, out CacheEntryEnvelope? envelope) || envelope == null)
        {
            CacheMisses.Inc();
            RemoveKeyFromTableIndex(table, cacheKey);
            return null;
        }

        // Validate epoch
        var isValid = await _epochValidationService.IsEpochValidAsync(table, envelope.Epoch, ct);
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
        var currentEpoch = await _epochValidationService.GetCurrentEpochAsync(table, ct);
        var envelope = new CacheEntryEnvelope(decision, currentEpoch);

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


    private static string BuildCacheKey(TenantId tenant, Sid userSid, TableIdentifier table, string? contextHash = null) =>
        $"{tenant.Value}:consent:{userSid.Value.ToUpperInvariant()}:{(string.IsNullOrWhiteSpace(contextHash) ? "default" : contextHash)}:{table.Domain.ToLowerInvariant()}:{table.Schema.ToLowerInvariant()}:{table.TableName.ToLowerInvariant()}";

    public void Dispose()
    {
        _subscription?.Dispose();
    }
}
