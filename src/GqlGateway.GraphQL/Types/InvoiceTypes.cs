using System.Security.Claims;
using GqlGateway.Domain.Common;
using GqlGateway.GraphQL.Loaders;
using GqlGateway.GraphQL.Services;
using HotChocolate;
using Microsoft.AspNetCore.Http;

namespace GqlGateway.GraphQL.Types;

public sealed class InvoiceRecord
{
    public string Id { get; init; } = string.Empty;
    public decimal Amount { get; init; }
    public string Vendor { get; init; } = string.Empty;
    public string? Email { get; init; }

    public async Task<IReadOnlyList<InvoiceItemRecord>?> GetItemsAsync(
        [Parent] InvoiceRecord invoice,
        InvoiceItemDataLoader dataLoader,
        CancellationToken ct)
    {
        // N+1 & Zero-Trust: Batch DataLoader batches queries across all parent records in 1 roundtrip,
        // and LoadInvoiceItemsBatchAsync enforces consent and audits the child table once.
        return await dataLoader.LoadAsync(invoice.Id, ct);
    }
}

public sealed class InvoiceItemRecord
{
    public string Id { get; init; } = string.Empty;
    public string InvoiceId { get; init; } = string.Empty;
    public string? ProductName { get; init; }
    public decimal Price { get; init; }
    public string? SensitiveNote { get; init; }
}
