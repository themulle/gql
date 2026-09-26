using System.Data.Common;
using GqlGateway.Domain.Options;

namespace GqlGateway.Application.Interfaces;

public interface ISqlConnectionFactory
{
    Task<DbConnection> CreateOpenConnectionAsync(DataSourceConnectionOptions options, CancellationToken ct = default);
}
