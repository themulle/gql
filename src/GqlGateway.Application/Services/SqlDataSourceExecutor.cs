using System.Data;
using System.Data.Common;
using System.Security;
using System.Text;
using GqlGateway.Application.Interfaces;
using GqlGateway.Domain.Common;
using GqlGateway.Domain.Model;
using GqlGateway.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GqlGateway.Application.Services;

public sealed class SqlDataSourceExecutor : IDataSourceExecutor
{
    private readonly ISqlConnectionFactory? _connectionFactory;
    private readonly IOptions<GatewayOptions>? _options;
    private readonly ILogger<SqlDataSourceExecutor>? _logger;

    public DataSourceType SupportedType => DataSourceType.Sql;

    public SqlDataSourceExecutor(
        ISqlConnectionFactory? connectionFactory = null,
        IOptions<GatewayOptions>? options = null,
        ILogger<SqlDataSourceExecutor>? logger = null)
    {
        _connectionFactory = connectionFactory;
        _options = options;
        _logger = logger;
    }

    public async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ExecuteAsync(
        DataSourceExecutionContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        // SEC-01: Side-channel inference protection: verify column filters target only Clear columns
        foreach (var (argKey, argVal) in context.Arguments)
        {
            if (string.Equals(argKey, "limit", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(argKey, "offset", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var matchingCol = context.Metadata.Columns.FirstOrDefault(c => string.Equals(c.ColumnName, argKey, StringComparison.OrdinalIgnoreCase));
            if (matchingCol != null && argVal != null)
            {
                var access = context.AccessDecision.GetColumnAccess(matchingCol.ColumnName);
                if (access != ColumnAccessLevel.Clear)
                {
                    throw new SecurityException($"Zero-Trust-Verletzung: Filtern auf Spalte '{matchingCol.ColumnName}' in Tabelle '{context.Metadata.Identifier}' ist nicht gestattet (Zugriffsebene: {access}).");
                }
            }
        }

        // Check if a real SQL connection is configured for this data source
        var configuredConnections = _options?.Value?.DataSources?.Connections;
        DataSourceConnectionOptions? connOptions = null;

        if (configuredConnections != null && !string.IsNullOrWhiteSpace(context.SourceName))
        {
            configuredConnections.TryGetValue(context.SourceName, out connOptions);
        }

        // If no real connection is configured, or connection factory is missing, execute synthetic demo data generator (fallback for dev & unit tests)
        if (connOptions == null || string.IsNullOrWhiteSpace(connOptions.ConnectionString) || _connectionFactory == null)
        {
            return GenerateSyntheticRows(context);
        }

        return await ExecuteRealSqlQueryAsync(context, connOptions, ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ExecuteRealSqlQueryAsync(
        DataSourceExecutionContext context,
        DataSourceConnectionOptions connOptions,
        CancellationToken ct)
    {
        var metadata = context.Metadata;
        var dialect = metadata.Dialect;

        ArgumentNullException.ThrowIfNull(_connectionFactory);
        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(connOptions, ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Math.Max(1, connOptions.CommandTimeoutSeconds);

        // 1. Column Projections (Zero-Trust: Only authorized requested fields)
        var columnsToSelect = (context.RequestedFields != null && context.RequestedFields.Count > 0)
            ? context.RequestedFields
            : metadata.Columns.Select(c => c.ColumnName).ToList();

        var selectClause = string.Join(", ", columnsToSelect.Select(c => dialect.QuoteIdentifier(c)));
        var fromTable = dialect.FormatTableIdentifier(metadata.Identifier);

        // 2. WHERE Clause: Push down RLS predicate + Tenant isolation + any applicable equality arguments
        var whereParts = new List<string>();
        var paramIndex = 0;

        // Stufe 1: Applikationsseitiger erzwungener Tenant-Filter (Defense in Depth)
        var tenantVal = context.Tenant?.Value ?? context.Principal.FindFirst("tenant")?.Value ?? TenantId.LegacySingleTenant.Value;
        var hasTenantCol = metadata.Columns.Any(c => string.Equals(c.ColumnName, "tenant_id", StringComparison.OrdinalIgnoreCase));
        if (hasTenantCol)
        {
            var pTenant = $"@p_tenant_{paramIndex++}";
            whereParts.Add($"{dialect.QuoteIdentifier("tenant_id")} = {pTenant}");
            var tp = command.CreateParameter();
            tp.ParameterName = pTenant;
            tp.Value = tenantVal;
            command.Parameters.Add(tp);
        }

        if (!string.IsNullOrWhiteSpace(context.AccessDecision.CombinedRowFilterSql))
        {
            whereParts.Add($"({context.AccessDecision.CombinedRowFilterSql})");
        }

        foreach (var (argKey, argVal) in context.Arguments)
        {
            if (string.Equals(argKey, "limit", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(argKey, "offset", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var matchingCol = metadata.Columns.FirstOrDefault(c => string.Equals(c.ColumnName, argKey, StringComparison.OrdinalIgnoreCase));
            if (matchingCol != null && argVal != null)
            {
                // SEC-01: Zero-Trust rule: Filtering on columns without explicit Clear access (or with Mask/Deny) is strictly forbidden to prevent side-channel inference
                var access = context.AccessDecision.GetColumnAccess(matchingCol.ColumnName);
                if (access != ColumnAccessLevel.Clear)
                {
                    throw new SecurityException($"Zero-Trust-Verletzung: Filtern auf Spalte '{matchingCol.ColumnName}' in Tabelle '{metadata.Identifier}' ist nicht gestattet (Zugriffsebene: {access}).");
                }

                var paramName = $"@p{paramIndex++}";
                whereParts.Add($"{dialect.QuoteIdentifier(matchingCol.ColumnName)} = {paramName}");

                var p = command.CreateParameter();
                p.ParameterName = paramName;
                p.Value = argVal;
                command.Parameters.Add(p);
            }
        }

        var sqlBuilder = new StringBuilder();
        sqlBuilder.Append($"SELECT {selectClause} FROM {fromTable}");

        if (whereParts.Count > 0)
        {
            sqlBuilder.Append(" WHERE ");
            sqlBuilder.Append(string.Join(" AND ", whereParts));
        }

        // 3. Pagination Pushdown (Dialect-specific)
        var limit = Math.Max(1, context.Limit);
        var offset = Math.Max(0, context.Offset);

        var limitParam = command.CreateParameter();
        limitParam.ParameterName = "@gql_limit";
        limitParam.Value = limit;
        command.Parameters.Add(limitParam);

        var offsetParam = command.CreateParameter();
        offsetParam.ParameterName = "@gql_offset";
        offsetParam.Value = offset;
        command.Parameters.Add(offsetParam);

        switch (dialect)
        {
            case DatabaseDialect.SqlServer:
                // SQL Server requires an ORDER BY clause for OFFSET-FETCH.
                var orderCol = metadata.Columns.FirstOrDefault(c => string.Equals(c.ColumnName, "id", StringComparison.OrdinalIgnoreCase))?.ColumnName
                               ?? (metadata.Columns.Count > 0 ? metadata.Columns[0].ColumnName : null);
                var orderClause = orderCol != null ? dialect.QuoteIdentifier(orderCol) : "(SELECT 1)";
                sqlBuilder.Append($" ORDER BY {orderClause} OFFSET @gql_offset ROWS FETCH NEXT @gql_limit ROWS ONLY");
                break;

            case DatabaseDialect.Oracle:
                sqlBuilder.Append(" OFFSET @gql_offset ROWS FETCH NEXT @gql_limit ROWS ONLY");
                break;

            case DatabaseDialect.Sqlite:
            case DatabaseDialect.PostgreSql:
            case DatabaseDialect.Databricks:
            default:
                sqlBuilder.Append(" LIMIT @gql_limit OFFSET @gql_offset");
                break;
        }

        command.CommandText = sqlBuilder.ToString();
        _logger?.LogDebug("Executing SQL Backend query: {Sql}", command.CommandText);

        // Stufe 2: Native PostgreSQL Transaktions-Scoped Session RLS (SET LOCAL app.tenant_id = @p)
        DbTransaction? tx = null;
        try
        {
            if (dialect == DatabaseDialect.PostgreSql ||
                string.Equals(connOptions.Provider, "PostgreSql", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(connOptions.Provider, "postgres", StringComparison.OrdinalIgnoreCase))
            {
                tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
                command.Transaction = tx;

                await using var setCmd = connection.CreateCommand();
                setCmd.Transaction = tx;
                setCmd.CommandText = "SELECT set_config('app.tenant_id', @p_tenant, true);";
                var pTenant = setCmd.CreateParameter();
                pTenant.ParameterName = "@p_tenant";
                pTenant.Value = tenantVal;
                setCmd.Parameters.Add(pTenant);
                await setCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            var results = new List<IReadOnlyDictionary<string, object?>>(Math.Min(Math.Max(context.Limit, 16), 1024));

            await using (var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess | CommandBehavior.SingleResult, ct).ConfigureAwait(false))
            {
                int fieldCount = reader.FieldCount;
                var columnNames = new string[fieldCount];
                for (int i = 0; i < fieldCount; i++)
                {
                    columnNames[i] = reader.GetName(i);
                }

                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    var row = new Dictionary<string, object?>(fieldCount, StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < fieldCount; i++)
                    {
                        var value = reader.IsDBNull(i) ? null : reader.GetValue(i);
                        row[columnNames[i]] = value;
                    }
                    results.Add(row);
                }
            }

            if (tx != null)
            {
                await tx.CommitAsync(ct).ConfigureAwait(false);
            }

            return results;
        }
        catch
        {
            if (tx != null)
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
            }
            throw;
        }
        finally
        {
            if (tx != null)
            {
                await tx.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static IReadOnlyList<IReadOnlyDictionary<string, object?>> GenerateSyntheticRows(DataSourceExecutionContext context)
    {
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        var metadata = context.Metadata;
        var count = Math.Max(1, context.Limit);
        var offset = Math.Max(0, context.Offset);

        for (int i = 1; i <= count; i++)
        {
            var rowNum = offset + i;
            var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

            foreach (var col in metadata.Columns)
            {
                object? rawVal = col.ColumnName.ToLowerInvariant() switch
                {
                    "id" => rowNum,
                    "name" => $"Sample {metadata.Identifier.TableName} Record #{rowNum}",
                    "amount" => 100.50m * rowNum,
                    "email" => $"user{rowNum}@corp.local",
                    "created_at" => DateTimeOffset.UtcNow.AddDays(-rowNum),
                    _ => $"Value_{rowNum}"
                };

                dict[col.ColumnName] = rawVal;
            }

            rows.Add(dict);
        }

        return rows;
    }
}
