using System;
using Microsoft.Extensions.Configuration;

namespace GqlGateway.Application.Connectors;

public interface IGqlGatewayConnectorFactory
{
    string ConnectorType { get; }
    IGqlGatewayConnector CreateConnector(string catalogName, IConfiguration configuration, IServiceProvider serviceProvider);
}
