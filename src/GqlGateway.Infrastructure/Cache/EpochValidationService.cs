using System.Collections.Concurrent;
using GqlGateway.Application.Interfaces;
using GqlGateway.Domain.Common;
using GqlGateway.Domain.Options;
using Microsoft.Extensions.Options;

namespace GqlGateway.Infrastructure.Cache;

public sealed class EpochValidationService : IEpochValidationService
{
    private readonly ConcurrentDictionary<string, long> _epochs = new(StringComparer.OrdinalIgnoreCase);
    private readonly EpochValidationOptions _options;
    private readonly IEventBus _eventBus;
    private readonly string _invalidationChannel;

    public EpochValidationService(
        IOptions<GatewayOptions>? options = null,
        IEventBus? eventBus = null)
    {
        _options = options?.Value?.Caching?.EpochValidation ?? new EpochValidationOptions();
        _invalidationChannel = options?.Value?.Caching?.Redis?.InvalidationChannel ?? "consent:invalidations";
        _eventBus = eventBus ?? new Messaging.InProcessChannelEventBus();
    }

    public Task<long> GetCurrentEpochAsync(TableIdentifier table, CancellationToken ct = default)
    {
        var key = table.ToString().ToLowerInvariant();
        var epoch = _epochs.GetOrAdd(key, 1);
        return Task.FromResult(epoch);
    }

    public Task<IReadOnlyDictionary<TableIdentifier, long>> GetCurrentEpochsAsync(
        IEnumerable<TableIdentifier> tables,
        CancellationToken ct = default)
    {
        var result = new Dictionary<TableIdentifier, long>();
        foreach (var t in tables)
        {
            var key = t.ToString().ToLowerInvariant();
            result[t] = _epochs.GetOrAdd(key, 1);
        }
        return Task.FromResult<IReadOnlyDictionary<TableIdentifier, long>>(result);
    }

    public async Task<bool> IsEpochValidAsync(TableIdentifier table, long cachedEpoch, CancellationToken ct = default)
    {
        var current = await GetCurrentEpochAsync(table, ct);
        return current == cachedEpoch;
    }

    public async Task InvalidateEpochAsync(TableIdentifier table, CancellationToken ct = default)
    {
        var key = table.ToString().ToLowerInvariant();
        _epochs.AddOrUpdate(key, 2, (_, current) => current + 1);

        // Broadcast invalidation event
        await _eventBus.PublishAsync(_invalidationChannel, key, ct);
    }
}
