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
        var payload = $"{entry.Id}|{entry.PrevHash}|{entry.OccurredAt:O}|{entry.EventType}|{entry.ActorSid.Value}|{entry.TargetTable}|{entry.TargetColumn ?? ""}|{entry.Decision}|{entry.TraceId}|{entry.DetailsJson}";
        Span<byte> hashBytes = stackalloc byte[32];
        HMACSHA256.HashData(_auditHmacKey, Encoding.UTF8.GetBytes(payload), hashBytes);
        entry.EntryHash = Convert.ToHexString(hashBytes);

        using (var cmd = _connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = @"INSERT INTO AUDIT_LOG_ENTRIES (id, occurred_at, event_type, actor_sid, target_table, target_column, decision, trace_id, details_json, prev_hash, entry_hash)
                                VALUES (@id, @occ, @event, @actor, @target, @col, @dec, @trace, @det, @prev, @hash)";
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

            await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        _lastAuditHash = entry.EntryHash;
    }

    public async Task<IReadOnlyList<AuditLogEntry>> GetAuditLogEntriesAsync(int limit = 100, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var list = new List<AuditLogEntry>();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"SELECT id, occurred_at, event_type, actor_sid, target_table, target_column,
                                       decision, trace_id, details_json, prev_hash, entry_hash
                                FROM AUDIT_LOG_ENTRIES
                                ORDER BY rowid ASC
                                LIMIT @lim";
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
                    EntryHash = reader.GetString(10)
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
                                       decision, trace_id, details_json, prev_hash, entry_hash
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

                if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(prevHash), Encoding.UTF8.GetBytes(expectedPrevHash)))
                {
                    return false; // Broken chain!
                }

                var parsedOccurredAt = DateTimeOffset.Parse(occurredAt);
                var payload = $"{id}|{prevHash}|{parsedOccurredAt:O}|{eventType}|{actorSid}|{targetTable}|{targetColumn}|{decision}|{traceId}|{detailsJson}";
                var computedBytes = HMACSHA256.HashData(_auditHmacKey, Encoding.UTF8.GetBytes(payload));
                var computedHash = Convert.ToHexString(computedBytes);

                if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(entryHash), Encoding.UTF8.GetBytes(computedHash)))
                {
                    return false; // Tampered payload!
                }

                expectedPrevHash = entryHash;
            }

            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

}
