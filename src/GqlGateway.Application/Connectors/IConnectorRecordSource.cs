using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GqlGateway.Domain.Connectors;

namespace GqlGateway.Application.Connectors;

public interface IConnectorRecordSource
{
    IAsyncEnumerable<IReadOnlyDictionary<string, object?>> ReadSplitAsync(
        ConnectorSplit split,
        ConnectorSessionContext session,
        CancellationToken ct = default);

    Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ReadBatchAsync(
        ConnectorSplit split,
        ConnectorSessionContext session,
        CancellationToken ct = default);
}
