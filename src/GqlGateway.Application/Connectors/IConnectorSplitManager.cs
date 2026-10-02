using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GqlGateway.Domain.Connectors;
using GqlGateway.Domain.Model;

namespace GqlGateway.Application.Connectors;

public interface IConnectorSplitManager
{
    Task<IReadOnlyList<ConnectorSplit>> GetSplitsAsync(
        TableMetadata table,
        ConnectorSessionContext session,
        CancellationToken ct = default);
}
