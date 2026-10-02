using System.Security.Claims;
using GqlGateway.Domain.Common;
using GqlGateway.Domain.Interfaces;
using GqlGateway.Domain.Model;

namespace GqlGateway.Application.Interfaces;

public interface IGatewayExecutionService
{
    int LastDispatchedChildQueryCount { get; }

    Task<(IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows, TableAccessDecision Decision)> ExecuteTableQueryAsync(
        ClaimsPrincipal? principal,
        TableIdentifier table,
        int? first = null,
        int? after = null,
        CancellationToken ct = default);

    Task<(IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows, TableAccessDecision Decision)> ExecuteTableQueryAsync(
        ClaimsPrincipal? principal,
        TableIdentifier table,
        int? first,
        int? after,
        IReadOnlyDictionary<string, object?>? queryArguments,
        IReadOnlyList<string>? requestedFields = null,
        IReadOnlyDictionary<string, string[]>? requestHeaders = null,
        CancellationToken ct = default);

    Task<TableAccessDecision> CheckTableAccessAsync(
        ClaimsPrincipal? principal,
        TableIdentifier table,
        CancellationToken ct = default);

    Task<IReadOnlyDictionary<string, List<InvoiceItemRecord>>> LoadInvoiceItemsBatchAsync(
        ClaimsPrincipal? principal,
        IReadOnlyList<string> invoiceIds,
        CancellationToken ct = default);
}
