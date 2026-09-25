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
            if (_tableCacheKeys.TryRemove(normalized, out var keys))
            {
                foreach (var key in keys.Keys)
                {
                    _memoryCache.Remove(key);
                }
            }
        });
    }

    public async Task<TableAccessDecision?> GetCachedDecisionAsync(
        Sid userSid,
        TableIdentifier table,
        CancellationToken ct = default)
    {
        var cacheKey = BuildCacheKey(userSid, table);
        if (!_memoryCache.TryGetValue(cacheKey, out CacheEntryEnvelope? envelope) || envelope == null)
        {
            RemoveKeyFromTableIndex(table, cacheKey);
            return null;
        }

        // Validate epoch
        var isValid = await _epochValidationService.IsEpochValidAsync(table, envelope.Epoch, ct);
        if (!isValid)
        {
            _memoryCache.Remove(cacheKey);
            RemoveKeyFromTableIndex(table, cacheKey);
            return null;
        }

        return envelope.Decision;
    }

    public async Task SetCachedDecisionAsync(
        Sid userSid,
        TableIdentifier table,
        TableAccessDecision decision,
        TimeSpan ttl,
        CancellationToken ct = default)
    {
        var cacheKey = BuildCacheKey(userSid, table);
        var currentEpoch = await _epochValidationService.GetCurrentEpochAsync(table, ct);
        var envelope = new CacheEntryEnvelope(decision, currentEpoch);

        // Approximate byte size for L1 MemoryCache size budgeting (NF-PERF-02)
        var estimatedBytes = 512
            + (decision.CombinedRowFilterSql?.Length ?? 0) * 2
            + decision.ColumnAccess.Count * 64
            + decision.DeniedReasons.Sum(r => r.Length * 2);

        var tableKey = NormalizeTableKey(table);
        var keys = _tableCacheKeys.GetOrAdd(tableKey, _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal));
        keys.TryAdd(cacheKey, 0);

        var cacheEntryOptions = new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = ttl,
            Size = Math.Max(256, estimatedBytes)
        };

        cacheEntryOptions.RegisterPostEvictionCallback((evictedKey, _, _, _) =>
        {
            if (_tableCacheKeys.TryGetValue(tableKey, out var currentKeys))
            {
                currentKeys.TryRemove((string)evictedKey, out _);
                if (currentKeys.IsEmpty)
                {
                    _tableCacheKeys.TryRemove(tableKey, out _);
                }
            }
        });

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
        if (_memoryCache is MemoryCache mc)
        {
            mc.Clear();
        }
        _tableCacheKeys.Clear();
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

    private static string BuildCacheKey(Sid userSid, TableIdentifier table) =>
        $"consent:{userSid.Value.ToUpperInvariant()}:{table.Domain.ToLowerInvariant()}:{table.Schema.ToLowerInvariant()}:{table.TableName.ToLowerInvariant()}";

    public void Dispose()
    {
        _subscription?.Dispose();
    }
}
