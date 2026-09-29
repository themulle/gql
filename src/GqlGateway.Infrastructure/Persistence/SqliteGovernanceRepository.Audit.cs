using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GqlGateway.Application.Interfaces;
using GqlGateway.Domain.Common;
using GqlGateway.Domain.Interfaces;
using GqlGateway.Domain.Model;
using GqlGateway.Domain.Options;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace GqlGateway.Infrastructure.Persistence;

public partial class SqliteGovernanceRepository
{
    public async Task RecordAuditEventAsync(AuditLogEntry entry, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            await RecordAuditEventInternalAsync(entry, ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task RecordAuditEventInternalAsync(AuditLogEntry entry, CancellationToken ct)
    {
        using var tx = _connection.BeginTransaction();

        // Atomically query latest entry_hash from DB to prevent drift across instances or reconnections
        using (var prevCmd = _connection.CreateCommand())
        {
            prevCmd.Transaction = tx;
            prevCmd.CommandText = "SELECT entry_hash FROM AUDIT_LOG_ENTRIES ORDER BY rowid DESC LIMIT 1";
            var latestDbHash = await prevCmd.ExecuteScalarAsync(ct);
            if (latestDbHash != null && latestDbHash != DBNull.Value)
            {
                _lastAuditHash = (string)latestDbHash;
            }
        }

        // Compute cryptographic HMAC-SHA256 hash chain
        entry.PrevHash = _lastAuditHash;
        var payload = $"{entry.Id}|{entry.PrevHash}|{entry.OccurredAt:O}|{EscapeField(entry.EventType)}|{EscapeField(entry.ActorSid.Value)}|{EscapeField(entry.TargetTable)}|{EscapeField(entry.TargetColumn)}|{EscapeField(entry.Decision)}|{EscapeField(entry.TraceId)}|{EscapeField(entry.DetailsJson)}|{EscapeField(entry.TenantId.Value)}";
        Span<byte> hashBytes = stackalloc byte[32];
        HMACSHA256.HashData(_auditHmacKey, Encoding.UTF8.GetBytes(payload), hashBytes);
        entry.EntryHash = Convert.ToHexString(hashBytes);

        using (var cmd = _connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = @"INSERT INTO AUDIT_LOG_ENTRIES (id, occurred_at, event_type, actor_sid, target_table, target_column, decision, trace_id, details_json, prev_hash, entry_hash, tenant_id)
                                VALUES (@id, @occ, @event, @actor, @target, @col, @dec, @trace, @det, @prev, @hash, @tenantId)";
            cmd.Parameters.AddWithValue("@id", entry.Id.ToString());
            cmd.Parameters.AddWithValue("@occ", entry.OccurredAt.ToString("O"));
            cmd.Parameters.AddWithValue("@event", entry.EventType);
            cmd.Parameters.AddWithValue("@actor", entry.ActorSid.Value);
            cmd.Parameters.AddWithValue("@target", entry.TargetTable);
            cmd.Parameters.AddWithValue("@col", (object?)entry.TargetColumn ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@dec", entry.Decision);
            cmd.Parameters.AddWithValue("@trace", entry.TraceId);
            cmd.Parameters.AddWithValue("@det", entry.DetailsJson);
            cmd.Parameters.AddWithValue("@prev", entry.PrevHash);
            cmd.Parameters.AddWithValue("@hash", entry.EntryHash);
            cmd.Parameters.AddWithValue("@tenantId", entry.TenantId.Value);

            await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        _lastAuditHash = entry.EntryHash;
    }

    public async Task<IReadOnlyList<AuditLogEntry>> GetAuditLogEntriesAsync(int limit = 100, TenantId? tenantId = null, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var list = new List<AuditLogEntry>();
            using var cmd = _connection.CreateCommand();
            var sql = new StringBuilder(@"SELECT id, occurred_at, event_type, actor_sid, target_table, target_column,
                                       decision, trace_id, details_json, prev_hash, entry_hash, tenant_id
                                FROM AUDIT_LOG_ENTRIES");
            if (tenantId.HasValue)
            {
                sql.Append(" WHERE tenant_id = @tenantId");
                cmd.Parameters.AddWithValue("@tenantId", tenantId.Value.Value);
            }
            sql.Append(" ORDER BY rowid ASC LIMIT @lim");
            cmd.CommandText = sql.ToString();
            cmd.Parameters.AddWithValue("@lim", limit);

            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                list.Add(new AuditLogEntry
                {
                    Id = Guid.Parse(reader.GetString(0)),
                    OccurredAt = DateTimeOffset.Parse(reader.GetString(1)),
                    EventType = reader.GetString(2),
                    ActorSid = new Sid(reader.GetString(3)),
                    TargetTable = reader.GetString(4),
                    TargetColumn = reader.IsDBNull(5) ? null : reader.GetString(5),
                    Decision = reader.GetString(6),
                    TraceId = reader.GetString(7),
                    DetailsJson = reader.GetString(8),
                    PrevHash = reader.GetString(9),
                    EntryHash = reader.GetString(10),
                    TenantId = reader.IsDBNull(11) ? TenantId.LegacySingleTenant : (TenantId.TryParse(reader.GetString(11), out var tid) ? tid : TenantId.LegacySingleTenant)
                });
            }
            return list;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<AuditLogEntry>> QueryAuditLogsAsync(
        string? targetTable = null,
        Sid? actorSid = null,
        DateTimeOffset? since = null,
        int limit = 1000,
        TenantId? tenantId = null,
        CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var list = new List<AuditLogEntry>();
            using var cmd = _connection.CreateCommand();
            var sql = new StringBuilder(@"SELECT id, occurred_at, event_type, actor_sid, target_table, target_column,
                                               decision, trace_id, details_json, prev_hash, entry_hash, tenant_id
                                        FROM AUDIT_LOG_ENTRIES
                                        WHERE 1=1");

            if (!string.IsNullOrWhiteSpace(targetTable))
            {
                sql.Append(" AND target_table = @targetTable");
                cmd.Parameters.AddWithValue("@targetTable", targetTable);
            }
            if (actorSid.HasValue && !string.IsNullOrWhiteSpace(actorSid.Value.Value))
            {
                sql.Append(" AND actor_sid = @actorSid");
                cmd.Parameters.AddWithValue("@actorSid", actorSid.Value.Value);
            }
            if (since.HasValue)
            {
                sql.Append(" AND occurred_at >= @since");
                cmd.Parameters.AddWithValue("@since", since.Value.ToString("O"));
            }
            if (tenantId.HasValue)
            {
                sql.Append(" AND tenant_id = @tenantId");
                cmd.Parameters.AddWithValue("@tenantId", tenantId.Value.Value);
            }

            sql.Append(" ORDER BY occurred_at DESC LIMIT @lim");
            cmd.Parameters.AddWithValue("@lim", Math.Clamp(limit, 1, 5000));
            cmd.CommandText = sql.ToString();

            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                list.Add(new AuditLogEntry
                {
                    Id = Guid.Parse(reader.GetString(0)),
                    OccurredAt = DateTimeOffset.Parse(reader.GetString(1)),
                    EventType = reader.GetString(2),
                    ActorSid = new Sid(reader.GetString(3)),
                    TargetTable = reader.GetString(4),
                    TargetColumn = reader.IsDBNull(5) ? null : reader.GetString(5),
                    Decision = reader.GetString(6),
                    TraceId = reader.GetString(7),
                    DetailsJson = reader.GetString(8),
                    PrevHash = reader.GetString(9),
                    EntryHash = reader.GetString(10),
                    TenantId = reader.IsDBNull(11) ? TenantId.LegacySingleTenant : (TenantId.TryParse(reader.GetString(11), out var tid) ? tid : TenantId.LegacySingleTenant)
                });
            }
            return list;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> VerifyAuditHashChainAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"SELECT id, occurred_at, event_type, actor_sid, target_table, target_column,
                                       decision, trace_id, details_json, prev_hash, entry_hash, tenant_id
                                FROM AUDIT_LOG_ENTRIES
                                ORDER BY rowid ASC";

            using var reader = await cmd.ExecuteReaderAsync(ct);
            var expectedPrevHash = "GENESIS_0000000000000000000000000000000000000000000000000000000000000000";

            while (await reader.ReadAsync(ct))
            {
                var id = reader.GetString(0);
                var occurredAt = reader.GetString(1);
                var eventType = reader.GetString(2);
                var actorSid = reader.GetString(3);
                var targetTable = reader.GetString(4);
                var targetColumn = reader.IsDBNull(5) ? "" : reader.GetString(5);
                var decision = reader.GetString(6);
                var traceId = reader.GetString(7);
                var detailsJson = reader.GetString(8);
                var prevHash = reader.GetString(9);
                var entryHash = reader.GetString(10);
                var tenantId = reader.IsDBNull(11) ? TenantId.LegacySingleTenant.Value : reader.GetString(11);

                if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(prevHash), Encoding.UTF8.GetBytes(expectedPrevHash)))
                {
                    return false; // Broken chain!
                }

                var parsedOccurredAt = DateTimeOffset.Parse(occurredAt);
                var payload = $"{id}|{prevHash}|{parsedOccurredAt:O}|{EscapeField(eventType)}|{EscapeField(actorSid)}|{EscapeField(targetTable)}|{EscapeField(targetColumn)}|{EscapeField(decision)}|{EscapeField(traceId)}|{EscapeField(detailsJson)}|{EscapeField(tenantId)}";
                var computedBytes = HMACSHA256.HashData(_auditHmacKey, Encoding.UTF8.GetBytes(payload));
                var computedHash = Convert.ToHexString(computedBytes);

                if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(entryHash), Encoding.UTF8.GetBytes(computedHash)))
                {
                    return false; // Tampered payload!
                }

                expectedPrevHash = entryHash;
            }

            // Tail truncation detection (H-2): Ensure final entry hash matches expected last audit hash
            if (_lastAuditHash != "GENESIS_0000000000000000000000000000000000000000000000000000000000000000" &&
                !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expectedPrevHash), Encoding.UTF8.GetBytes(_lastAuditHash)))
            {
                return false; // Tail truncation detected!
            }

            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    private static string EscapeField(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace("\\", "\\\\").Replace("|", "\\|");
    }
}
