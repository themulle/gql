using System.Collections.Generic;
using GqlGateway.Domain.Common;

namespace GqlGateway.Application.Connectors;

public interface IGqlGatewayConnectorRegistry
{
    void RegisterConnector(string catalogName, IGqlGatewayConnector connector);
    void RegisterFactory(IGqlGatewayConnectorFactory factory);
    IGqlGatewayConnector? GetConnector(string catalogName);
    IReadOnlyCollection<IGqlGatewayConnector> GetAllConnectors();
    bool TryGetConnectorForTable(TableIdentifier table, out IGqlGatewayConnector? connector);
}
