using System.Collections.Concurrent;
using GqlGateway.Application.Interfaces;
using GqlGateway.Domain.Common;
using GqlGateway.Domain.Options;
using Microsoft.Extensions.Options;

namespace GqlGateway.Infrastructure.Cache;

public sealed class EpochValidationService : IEpochValidationService
{
    private readonly ConcurrentDictionary<string, long> _epochs = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastEpochRefresh = new(StringComparer.OrdinalIgnoreCase);
    private readonly EpochValidationOptions _options;
    private readonly IEventBus _eventBus;
    private readonly string _invalidationChannel;
    private readonly StackExchange.Redis.IConnectionMultiplexer? _multiplexer;
    private readonly string _redisPrefix;

    public EpochValidationService(
        IOptions<GatewayOptions>? options = null,
        IEventBus? eventBus = null,
        StackExchange.Redis.IConnectionMultiplexer? multiplexer = null)
    {
        _options = options?.Value?.Caching?.EpochValidation ?? new EpochValidationOptions();
        _invalidationChannel = options?.Value?.Caching?.Redis?.InvalidationChannel ?? "consent:invalidations";
        _eventBus = eventBus ?? new Messaging.InProcessChannelEventBus();
        _multiplexer = multiplexer;

        var prefix = options?.Value?.Caching?.Redis?.InstanceName ?? "gqlgateway:";
        _redisPrefix = prefix.EndsWith(':') ? prefix : prefix + ":";

        _eventBus.Subscribe<string>(_invalidationChannel, message =>
        {
            if (!string.IsNullOrWhiteSpace(message))
            {
                var key = message.ToLowerInvariant();
                _epochs.AddOrUpdate(key, 2, (_, current) => current + 1);
                _lastEpochRefresh[key] = DateTimeOffset.UtcNow;
            }
            return Task.CompletedTask;
        });
    }

    public async Task<long> GetCurrentEpochAsync(TableIdentifier table, CancellationToken ct = default)
    {
        var key = table.ToString().ToLowerInvariant();

        if (_multiplexer != null && _multiplexer.IsConnected)
        {
            try
            {
                var db = _multiplexer.GetDatabase();
                var redisVal = await db.StringGetAsync($"{_redisPrefix}epoch:{key}").ConfigureAwait(false);
                if (redisVal.HasValue && (long)redisVal > 0)
                {
                    var rEpoch = (long)redisVal;
                    _epochs[key] = rEpoch;
                    _lastEpochRefresh[key] = DateTimeOffset.UtcNow;
                    return rEpoch;
                }
                else
                {
                    await db.StringSetAsync($"{_redisPrefix}epoch:{key}", 1).ConfigureAwait(false);
                    _epochs[key] = 1;
                    _lastEpochRefresh[key] = DateTimeOffset.UtcNow;
                    return 1;
                }
            }
            catch
            {
                // Fall back to local degraded evaluation
            }
        }

        var epoch = _epochs.GetOrAdd(key, 1);
        _lastEpochRefresh.TryAdd(key, DateTimeOffset.UtcNow);
        return epoch;
    }

    public async Task<IReadOnlyDictionary<TableIdentifier, long>> GetCurrentEpochsAsync(
        IEnumerable<TableIdentifier> tables,
        CancellationToken ct = default)
    {
        var result = new Dictionary<TableIdentifier, long>();
        foreach (var t in tables)
        {
            result[t] = await GetCurrentEpochAsync(t, ct).ConfigureAwait(false);
        }
        return result;
    }

    public async Task<bool> IsEpochValidAsync(TableIdentifier table, long cachedEpoch, CancellationToken ct = default)
    {
        var key = table.ToString().ToLowerInvariant();

        // Check degraded / multi-node partition state:
        bool isDegraded = _multiplexer != null && !_multiplexer.IsConnected;
        if (isDegraded)
        {
            // SEC-EPOCH-01: In degraded state without Redis cluster coordination,
            // fail-closed on sensitive tables to prevent stale consent bypasses.
            if (_options.FailClosedOnSensitiveTables)
            {
                var isSensitiveTable = key.Contains("sensitive") ||
                                       key.Contains("employee") ||
                                       key.Contains("hr") ||
                                       key.Contains("patient") ||
                                       key.Contains("salary");
                if (isSensitiveTable)
                {
                    return false;
                }
            }

            // SEC-EPOCH-02: Enforce DegradedMaxStalenessSeconds
            if (_lastEpochRefresh.TryGetValue(key, out var lastRefresh))
            {
                if (DateTimeOffset.UtcNow - lastRefresh > TimeSpan.FromSeconds(_options.DegradedMaxStalenessSeconds))
                {
                    return false; // Stale epoch beyond degraded threshold -> fail closed
                }
            }
        }

        var current = await GetCurrentEpochAsync(table, ct).ConfigureAwait(false);
        return current == cachedEpoch;
    }

    public async Task InvalidateEpochAsync(TableIdentifier table, CancellationToken ct = default)
    {
        var key = table.ToString().ToLowerInvariant();
        _epochs.AddOrUpdate(key, 2, (_, current) => current + 1);
        _lastEpochRefresh[key] = DateTimeOffset.UtcNow;

        if (_multiplexer != null && _multiplexer.IsConnected)
        {
            try
            {
                var db = _multiplexer.GetDatabase();
                await db.StringIncrementAsync($"{_redisPrefix}epoch:{key}").ConfigureAwait(false);
            }
            catch
            {
                // Fallback to local and event bus
            }
        }

        // Broadcast invalidation event
        await _eventBus.PublishAsync(_invalidationChannel, key, ct).ConfigureAwait(false);
    }
}
