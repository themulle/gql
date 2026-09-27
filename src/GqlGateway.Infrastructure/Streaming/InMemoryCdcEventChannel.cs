namespace GqlGateway.Infrastructure.Streaming;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using GqlGateway.Application.Streaming.Interfaces;
using GqlGateway.Domain.Model;
using Microsoft.Extensions.Logging;

public sealed class InMemoryCdcEventChannel : ICdcEventChannel
{
    private readonly ConcurrentDictionary<string, Channel<CdcEvent>> _channels = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<InMemoryCdcEventChannel> _logger;

    public InMemoryCdcEventChannel(ILogger<InMemoryCdcEventChannel> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    private Channel<CdcEvent> GetOrCreateChannel(string topic)
    {
        return _channels.GetOrAdd(topic, _ =>
            Channel.CreateBounded<CdcEvent>(new BoundedChannelOptions(10_000)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = false,
                SingleWriter = false
            }));
    }

    public async ValueTask PublishAsync(CdcEvent cdcEvent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(cdcEvent);

        var tableTopic = $"cdc_{cdcEvent.Table.ToQualifiedName().ToLowerInvariant()}";
        var simpleTableTopic = $"cdc_{cdcEvent.Table.TableName.ToLowerInvariant()}";
        var globalTopic = "cdc_all";

        var channel1 = GetOrCreateChannel(tableTopic);
        await channel1.Writer.WriteAsync(cdcEvent, ct);

        if (!string.Equals(tableTopic, simpleTableTopic, StringComparison.OrdinalIgnoreCase))
        {
            var channel2 = GetOrCreateChannel(simpleTableTopic);
            await channel2.Writer.WriteAsync(cdcEvent, ct);
        }

        var globalChannel = GetOrCreateChannel(globalTopic);
        await globalChannel.Writer.WriteAsync(cdcEvent, ct);

        _logger.LogDebug(
            "Published event '{EventId}' to topics '{Topic1}', '{Topic2}'",
            cdcEvent.EventId, tableTopic, globalTopic);
    }

    public async IAsyncEnumerable<CdcEvent> SubscribeAsync(
        string topic,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var channel = GetOrCreateChannel(topic);
        var reader = channel.Reader;

        while (await reader.WaitToReadAsync(ct))
        {
            while (reader.TryRead(out var cdcEvent))
            {
                yield return cdcEvent;
            }
        }
    }
}
