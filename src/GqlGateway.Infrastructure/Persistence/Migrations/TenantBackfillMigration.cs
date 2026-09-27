namespace GqlGateway.Infrastructure.Persistence.Migrations;

using System;
using System.Threading;
using System.Threading.Tasks;
using GqlGateway.Domain.Common;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

public static class TenantBackfillMigration
{
    public static async Task<int> RunBackfillAsync(SqliteConnection connection, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            UPDATE CONSENT_REQUESTS 
            SET tenant_id = @defaultTenant 
            WHERE tenant_id IS NULL OR TRIM(tenant_id) = '';
        ";
        cmd.Parameters.AddWithValue("@defaultTenant", TenantId.LegacySingleTenant.Value);

        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public static async Task VerifyTenantBackfillAsync(SqliteConnection connection, bool isMultiTenantEnabled, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        if (!isMultiTenantEnabled) return;

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM CONSENT_REQUESTS WHERE tenant_id IS NULL OR TRIM(tenant_id) = '';";
        var countObj = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        var unmigratedCount = Convert.ToInt64(countObj);

        if (unmigratedCount > 0)
        {
            throw new InvalidOperationException(
                $"FATAL MIGRATION ERROR: Es wurden {unmigratedCount} unmigrierte Datensätze ohne TenantId in CONSENT_REQUESTS gefunden. " +
                $"Multi-Tenancy Start verweigert bis TenantBackfillMigration ausgeführt wurde.");
        }
    }
}
