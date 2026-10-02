using System.Data.Common;
using GqlGateway.Application.Interfaces;
using GqlGateway.Domain.Options;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;

namespace GqlGateway.Infrastructure.Persistence;

public sealed class SqlConnectionFactory : ISqlConnectionFactory
{
    public async Task<DbConnection> CreateOpenConnectionAsync(DataSourceConnectionOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            throw new ArgumentException("Connection string cannot be empty for SQL data source.", nameof(options));
        }

        var provider = options.Provider?.Trim().ToLowerInvariant() ?? "sqlite";
        DbConnection connection = provider switch
        {
            "sqlite" or "sqlite3" => new SqliteConnection(options.ConnectionString),
            "sqlserver" or "mssql" or "microsoft sql server" => new SqlConnection(options.ConnectionString),
            "postgres" or "postgresql" or "npgsql" => new Npgsql.NpgsqlConnection(options.ConnectionString),
            _ => throw new NotSupportedException($"SQL provider '{options.Provider}' is not supported. Supported providers are: 'Sqlite', 'SqlServer', 'PostgreSql'.")
        };

        try
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);

            if (connection is SqliteConnection sqliteConn)
            {
                sqliteConn.CreateFunction("gateway_hmac_sha256", (string? val, string? salt) =>
                {
                    if (val == null) return null;
                    byte[] key = System.Text.Encoding.UTF8.GetBytes(salt ?? string.Empty);
                    byte[] data = System.Text.Encoding.UTF8.GetBytes(val);
                    byte[] hash = System.Security.Cryptography.HMACSHA256.HashData(key, data);
                    return "hmac_" + Convert.ToHexStringLower(hash);
                }, isDeterministic: true);
            }

            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
