using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using GqlGateway.Application.Connectors;
using GqlGateway.Domain.Common;
using GqlGateway.Domain.Model;

namespace GqlGateway.Infrastructure.Connectors;

public sealed class InMemoryConnectorRegistry : IGqlGatewayConnectorRegistry
{
    private readonly ConcurrentDictionary<string, IGqlGatewayConnector> _connectors = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, IGqlGatewayConnectorFactory> _factories = new(StringComparer.OrdinalIgnoreCase);

    private static readonly System.Text.RegularExpressions.Regex CatalogNameRegex =
        new(@"^[a-zA-Z0-9_\-\.]+$", System.Text.RegularExpressions.RegexOptions.Compiled, TimeSpan.FromMilliseconds(200));

    private static void ValidateCatalogName(string catalogName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogName);
        if (!CatalogNameRegex.IsMatch(catalogName))
        {
            throw new ArgumentException($"Ungültiger Katalogname '{catalogName}'. Nur alphanumerische Zeichen, Bindestriche, Punkte und Unterstriche sind erlaubt.", nameof(catalogName));
        }
    }

    public void RegisterConnector(string catalogName, IGqlGatewayConnector connector)
    {
        ValidateCatalogName(catalogName);
        ArgumentNullException.ThrowIfNull(connector);
        _connectors[catalogName] = connector;
    }

    public void RegisterFactory(IGqlGatewayConnectorFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factories[factory.ConnectorType] = factory;
    }

    public IGqlGatewayConnector? GetConnector(string catalogName)
    {
        if (string.IsNullOrWhiteSpace(catalogName)) return null;
        return _connectors.TryGetValue(catalogName, out var connector) ? connector : null;
    }

    public IReadOnlyCollection<IGqlGatewayConnector> GetAllConnectors() => _connectors.Values.ToList();

    public bool TryGetConnectorForTable(TableIdentifier table, out IGqlGatewayConnector? connector)
    {
        // 1. Direct domain match (e.g. Domain = "finance" -> catalog "finance")
        if (!string.IsNullOrWhiteSpace(table.Domain) && _connectors.TryGetValue(table.Domain, out connector))
        {
            return true;
        }

        // 2. Direct connector ID match
        var matchingById = _connectors.Values.FirstOrDefault(c => string.Equals(c.ConnectorId, table.Domain, StringComparison.OrdinalIgnoreCase));
        if (matchingById != null)
        {
            connector = matchingById;
            return true;
        }

        // 3. Fallback to default connector by type if registered (e.g. "default-sql", "sql")
        if (_connectors.TryGetValue("default-sql", out connector) || _connectors.TryGetValue("sql", out connector))
        {
            return true;
        }

        connector = null;
        return false;
    }
}
