using System;
using GqlGateway.Domain.Connectors;

namespace GqlGateway.Application.Connectors;

public interface IGqlGatewayConnector : IAsyncDisposable
{
    string ConnectorId { get; }
    string ConnectorType { get; }
    ConnectorCapabilities Capabilities { get; }
    IConnectorMetadata Metadata { get; }
    IConnectorSplitManager SplitManager { get; }
    IConnectorRecordSource RecordSource { get; }
}
