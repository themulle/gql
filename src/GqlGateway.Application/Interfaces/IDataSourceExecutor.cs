using System.Security.Claims;
using GqlGateway.Domain.Common;
using GqlGateway.Domain.Interfaces;
using GqlGateway.Domain.Model;
using Microsoft.AspNetCore.Http;

namespace GqlGateway.Application.Interfaces;

public interface IDataSourceExecutor
{
    DataSourceType SupportedType { get; }

    Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ExecuteAsync(
        DataSourceExecutionContext context,
        CancellationToken ct = default);
}

public sealed record DataSourceExecutionContext(
    string SourceName,
    TableMetadata Metadata,
    ClaimsPrincipal Principal,
    TableAccessDecision AccessDecision,
    IReadOnlyDictionary<string, object?> Arguments,
    IReadOnlyList<string> RequestedFields,
    HttpContext? HttpContext,
    int Limit = 1000,
    int Offset = 0
);
