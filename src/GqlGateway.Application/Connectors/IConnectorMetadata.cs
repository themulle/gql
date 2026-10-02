using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GqlGateway.Domain.Common;
using GqlGateway.Domain.Model;

namespace GqlGateway.Application.Connectors;

public interface IConnectorMetadata
{
    Task<IReadOnlyList<string>> ListSchemasAsync(CancellationToken ct = default);
    Task<IReadOnlyList<TableIdentifier>> ListTablesAsync(string? schema = null, CancellationToken ct = default);
    Task<TableMetadata?> GetTableMetadataAsync(TableIdentifier table, CancellationToken ct = default);
}
