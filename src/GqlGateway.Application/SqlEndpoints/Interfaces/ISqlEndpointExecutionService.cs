namespace GqlGateway.Application.SqlEndpoints.Interfaces;

using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using GqlGateway.Application.Sql.Interfaces;
using GqlGateway.Domain.Common;

public interface ISqlEndpointExecutionService
{
    Task<GovernedSqlResult> ExecuteEndpointAsync(
        string endpointName,
        IReadOnlyDictionary<string, object?>? rawInputs,
        ClaimsPrincipal user,
        TenantId tenantId,
        CancellationToken ct = default);
}
