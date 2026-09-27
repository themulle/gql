namespace GqlGateway.GraphQL.Subscriptions;

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GqlGateway.Application.Streaming.Interfaces;
using GqlGateway.Domain.Model;
using HotChocolate;
using HotChocolate.Types;

public sealed class Subscription
{
    [Subscribe(With = nameof(SubscribeToTableEventsAsync))]
    public StreamCdcEvent OnTableChanged(
        [EventMessage] StreamCdcEvent message) => message;

    public async IAsyncEnumerable<StreamCdcEvent> SubscribeToTableEventsAsync(
        string table,
        string? tenantId,
        [Service] ICdcEventChannel eventChannel,
        [Service] IStreamRlsPolicyEnforcer enforcer,
        ClaimsPrincipal? principal,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var cleanTable = table.Trim().ToLowerInvariant();
        var topic = cleanTable.StartsWith("cdc_", StringComparison.OrdinalIgnoreCase)
            ? cleanTable
            : $"cdc_{cleanTable}";

        var sourceStream = eventChannel.SubscribeAsync(topic, ct);
        var subscriber = principal ?? new ClaimsPrincipal(new ClaimsIdentity());

        await foreach (var cdcEvent in sourceStream.WithCancellation(ct))
        {
            var decision = await enforcer.EvaluateAndMaskAsync(cdcEvent, subscriber, ct);
            if (!decision.IsAllowed || decision.MaskedPayload == null)
            {
                continue; // Zero leakage: unauthorized events dropped
            }

            yield return new StreamCdcEvent(
                EventId: cdcEvent.EventId,
                Table: cdcEvent.Table.ToQualifiedName(),
                Operation: cdcEvent.Operation.ToString(),
                TenantId: cdcEvent.TenantId,
                PayloadJson: JsonSerializer.Serialize(decision.MaskedPayload),
                Timestamp: cdcEvent.Timestamp
            );
        }
    }
}
