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
    public async Task<TableMetadata?> GetTableMetadataAsync(TableIdentifier table, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            Guid? tableId = null;
            Table? tableEntity = null;

            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = @"SELECT id, source_type, source_name, schema_name, table_name, display_name, sensitivity, requires_four_eyes, is_active
                                    FROM TABLES
                                    WHERE source_name = @domain COLLATE NOCASE AND schema_name = @schema COLLATE NOCASE AND table_name = @table COLLATE NOCASE";
                cmd.Parameters.AddWithValue("@domain", table.Domain);
                cmd.Parameters.AddWithValue("@schema", table.Schema);
                cmd.Parameters.AddWithValue("@table", table.TableName);

                using var reader = await cmd.ExecuteReaderAsync(ct);
                if (await reader.ReadAsync(ct))
                {
                    tableId = Guid.Parse(reader.GetString(0));
                    tableEntity = new Table
                    {
                        Id = tableId.Value,
                        SourceType = reader.GetString(1),
                        SourceName = reader.GetString(2),
                        SchemaName = reader.GetString(3),
                        TableName = reader.GetString(4),
                        DisplayName = reader.GetString(5),
                        Sensitivity = reader.GetString(6),
                        RequiresFourEyes = reader.GetInt32(7) == 1,
                        IsActive = reader.GetInt32(8) == 1
                    };
                }
            }

            if (tableEntity == null || !tableId.HasValue) return null;

            var columns = new List<TableColumn>();
            var maskingRules = new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase);

            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = @"SELECT c.id, c.column_name, c.data_type, c.is_sensitive,
                                           m.id, m.rule_type, m.pattern_or_format, m.replacement, m.hmac_key_id
                                    FROM TABLE_COLUMNS c
                                    LEFT JOIN COLUMN_MASKING_RULES m ON c.id = m.table_column_id
                                    WHERE c.table_id = @tid";
                cmd.Parameters.AddWithValue("@tid", tableId.Value.ToString());

                using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var colId = Guid.Parse(reader.GetString(0));
                    var colName = reader.GetString(1);
                    var col = new TableColumn
                    {
                        Id = colId,
                        TableId = tableId.Value,
                        ColumnName = colName,
                        DataType = reader.GetString(2),
                        IsSensitive = reader.GetInt32(3) == 1
                    };
                    columns.Add(col);

                    if (!reader.IsDBNull(4))
                    {
                        var maskRule = new MaskingRule
                        {
                            Id = Guid.Parse(reader.GetString(4)),
                            TableColumnId = colId,
                            RuleType = reader.GetString(5),
                            PatternOrFormat = reader.IsDBNull(6) ? null : reader.GetString(6),
                            Replacement = reader.IsDBNull(7) ? null : reader.GetString(7),
                            HmacKeyId = reader.IsDBNull(8) ? null : reader.GetString(8)
                        };
                        maskingRules[colName] = maskRule;
                    }
                }
            }

            return new TableMetadata
            {
                Table = tableEntity,
                Identifier = table,
                Columns = columns,
                ColumnMaskingRules = maskingRules
            };
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<TableMetadata>> GetAllTablesAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var results = new List<TableMetadata>();
            var tableRows = new List<(Guid id, string schema, string name, Table table)>();

            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = @"SELECT t.id, t.source_type, t.source_name, t.schema_name, t.table_name,
                                           t.display_name, t.sensitivity, t.requires_four_eyes, t.is_active,
                                           COALESCE(t.source_name, p.domain, 'default') as domain
                                    FROM TABLES t
                                    LEFT JOIN POLICY_EPOCHS p ON t.id = p.table_id
                                    WHERE t.is_active = 1";

                using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var id = Guid.Parse(reader.GetString(0));
                    var domain = reader.GetString(9);
                    var schema = reader.GetString(3);
                    var name = reader.GetString(4);

                    var table = new Table
                    {
                        Id = id,
                        SourceType = reader.GetString(1),
                        SourceName = reader.GetString(2),
                        SchemaName = schema,
                        TableName = name,
                        DisplayName = reader.GetString(5),
                        Sensitivity = reader.GetString(6),
                        RequiresFourEyes = reader.GetInt32(7) == 1,
                        IsActive = reader.GetInt32(8) == 1
                    };
                    tableRows.Add((id, domain, name, table));
                }
            }

            var columnsByTable = new Dictionary<Guid, List<TableColumn>>();
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = @"SELECT id, table_id, column_name, data_type, is_sensitive FROM TABLE_COLUMNS";
                using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var tid = Guid.Parse(reader.GetString(1));
                    if (!columnsByTable.TryGetValue(tid, out var list))
                    {
                        list = new List<TableColumn>();
                        columnsByTable[tid] = list;
                    }
                    list.Add(new TableColumn
                    {
                        Id = Guid.Parse(reader.GetString(0)),
                        TableId = tid,
                        ColumnName = reader.GetString(2),
                        DataType = reader.GetString(3),
                        IsSensitive = reader.GetInt32(4) == 1
                    });
                }
            }

            foreach (var (id, domain, name, table) in tableRows)
            {
                var identifier = new TableIdentifier(domain, table.SchemaName, name);
                var columns = columnsByTable.TryGetValue(id, out var cols) ? cols : new List<TableColumn>();

                results.Add(new TableMetadata
                {
                    Table = table,
                    Identifier = identifier,
                    Columns = columns,
                    ColumnMaskingRules = new Dictionary<string, MaskingRule>()
                });
            }

            return results;
        }
        finally
        {
            _lock.Release();
        }
    }


    public async Task<long> GetTableEpochAsync(TableIdentifier table, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"SELECT epoch FROM POLICY_EPOCHS
                                WHERE domain = @domain COLLATE NOCASE AND schema_name = @schema COLLATE NOCASE AND table_name = @table COLLATE NOCASE";
            cmd.Parameters.AddWithValue("@domain", table.Domain);
            cmd.Parameters.AddWithValue("@schema", table.Schema);
            cmd.Parameters.AddWithValue("@table", table.TableName);

            var result = await cmd.ExecuteScalarAsync(ct);
            return result is long l ? l : (result is int i ? i : 1L);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<long> IncrementTableEpochAsync(TableIdentifier table, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            long newEpoch;
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = @"UPDATE POLICY_EPOCHS
                                    SET epoch = epoch + 1, updated_at = @now
                                    WHERE domain = @domain COLLATE NOCASE AND schema_name = @schema COLLATE NOCASE AND table_name = @table COLLATE NOCASE
                                    RETURNING epoch;";
                cmd.Parameters.AddWithValue("@domain", table.Domain);
                cmd.Parameters.AddWithValue("@schema", table.Schema);
                cmd.Parameters.AddWithValue("@table", table.TableName);
                cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));

                var result = await cmd.ExecuteScalarAsync(ct);
                newEpoch = result is long l ? l : (result is int i ? i : 2L);
            }

            await _epochValidationService.InvalidateEpochAsync(table, ct);
            return newEpoch;
        }
        finally
        {
            _lock.Release();
        }
    }


    private Task IncrementTableEpochInternalAsync(TableIdentifier table, CancellationToken ct) =>
        IncrementTableEpochInternalAsync(table, null, ct);

    private async Task IncrementTableEpochInternalAsync(TableIdentifier table, SqliteTransaction? transaction, CancellationToken ct)
    {
        using (var cmd = _connection.CreateCommand())
        {
            if (transaction != null)
            {
                cmd.Transaction = transaction;
            }
            cmd.CommandText = @"UPDATE POLICY_EPOCHS
                                SET epoch = epoch + 1, updated_at = @now
                                WHERE domain = @domain COLLATE NOCASE AND schema_name = @schema COLLATE NOCASE AND table_name = @table COLLATE NOCASE";
            cmd.Parameters.AddWithValue("@domain", table.Domain);
            cmd.Parameters.AddWithValue("@schema", table.Schema);
            cmd.Parameters.AddWithValue("@table", table.TableName);
            cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));
            int rowsAffected = await cmd.ExecuteNonQueryAsync(ct);
            if (rowsAffected == 0)
            {
                using var insertCmd = _connection.CreateCommand();
                if (transaction != null)
                {
                    insertCmd.Transaction = transaction;
                }
                insertCmd.CommandText = @"INSERT INTO POLICY_EPOCHS (table_id, domain, schema_name, table_name, epoch, updated_at)
                                          SELECT id, @domain, schema_name, table_name, 2, @now
                                          FROM TABLES
                                          WHERE source_name = @domain COLLATE NOCASE AND schema_name = @schema COLLATE NOCASE AND table_name = @table COLLATE NOCASE";
                insertCmd.Parameters.AddWithValue("@domain", table.Domain);
                insertCmd.Parameters.AddWithValue("@schema", table.Schema);
                insertCmd.Parameters.AddWithValue("@table", table.TableName);
                insertCmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));
                await insertCmd.ExecuteNonQueryAsync(ct);
            }
        }

        if (transaction == null)
        {
            await _epochValidationService.InvalidateEpochAsync(table, ct);
        }
    }

    public async Task DeletePolicyEpochForTableAsync(TableIdentifier table, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"DELETE FROM POLICY_EPOCHS WHERE domain = @domain COLLATE NOCASE AND schema_name = @schema COLLATE NOCASE AND table_name = @table COLLATE NOCASE";
            cmd.Parameters.AddWithValue("@domain", table.Domain);
            cmd.Parameters.AddWithValue("@schema", table.Schema);
            cmd.Parameters.AddWithValue("@table", table.TableName);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<DataOwnerDelegation> DelegateDataOwnershipAsync(DataOwnerDelegation delegation, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"INSERT INTO DATA_OWNER_DELEGATIONS (id, data_owner_id, delegate_sid, valid_from, valid_to, reason)
                                VALUES (@id, @ownerId, @delSid, @from, @to, @reason)";
            cmd.Parameters.AddWithValue("@id", delegation.Id.ToString());
            cmd.Parameters.AddWithValue("@ownerId", delegation.DataOwnerId.ToString());
            cmd.Parameters.AddWithValue("@delSid", delegation.DelegateSid.Value);
            cmd.Parameters.AddWithValue("@from", delegation.ValidFrom.ToString("O"));
            cmd.Parameters.AddWithValue("@to", delegation.ValidTo.ToString("O"));
            cmd.Parameters.AddWithValue("@reason", delegation.Reason);

            await cmd.ExecuteNonQueryAsync(ct);
            return delegation;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<DataOwner>> GetDataOwnersForTableAsync(TableIdentifier table, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var owners = new List<DataOwner>();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"SELECT o.id, o.ad_sid, o.ad_account, o.display_name, o.email, o.is_active
                                FROM DATA_OWNERS o
                                JOIN TABLE_OWNERS tow ON o.id = tow.data_owner_id
                                JOIN TABLES t ON tow.table_id = t.id
                                WHERE t.source_name = @domain COLLATE NOCASE AND t.schema_name = @schema COLLATE NOCASE AND t.table_name = @table COLLATE NOCASE";
            cmd.Parameters.AddWithValue("@domain", table.Domain);
            cmd.Parameters.AddWithValue("@schema", table.Schema);
            cmd.Parameters.AddWithValue("@table", table.TableName);

            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                owners.Add(new DataOwner
                {
                    Id = Guid.Parse(reader.GetString(0)),
                    AdSid = new Sid(reader.GetString(1)),
                    AdAccount = reader.GetString(2),
                    DisplayName = reader.GetString(3),
                    Email = reader.GetString(4),
                    IsActive = reader.GetInt32(5) == 1
                });
            }
            return owners;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> IsAuthorizedApproverForTableAsync(TableIdentifier table, Sid approverSid, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            return await IsAuthorizedApproverForTableInternalAsync(table, approverSid, ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<bool> IsAuthorizedApproverForTableInternalAsync(TableIdentifier table, Sid approverSid, CancellationToken ct)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            SELECT 1
            FROM TABLE_OWNERS tow
            JOIN DATA_OWNERS o ON tow.data_owner_id = o.id
            JOIN TABLES t ON tow.table_id = t.id
            WHERE t.source_name = @domain COLLATE NOCASE AND t.schema_name = @schema COLLATE NOCASE AND t.table_name = @table COLLATE NOCASE
              AND o.ad_sid = @apprSid AND o.is_active = 1
            UNION
            SELECT 1
            FROM DATA_OWNER_DELEGATIONS del
            JOIN TABLE_OWNERS tow ON del.data_owner_id = tow.data_owner_id
            JOIN TABLES t ON tow.table_id = t.id
            WHERE t.source_name = @domain COLLATE NOCASE AND t.schema_name = @schema COLLATE NOCASE AND t.table_name = @table COLLATE NOCASE
              AND del.delegate_sid = @apprSid AND del.valid_from <= @now AND @now < del.valid_to
            UNION
            SELECT 1
            FROM ROLE_MEMBERS rm
            JOIN ROLES r ON rm.role_id = r.id
            WHERE rm.member_sid = @apprSid AND r.role_name IN ('GovernanceAdmin', 'ClusterAdmin')
            LIMIT 1;";
        cmd.Parameters.AddWithValue("@domain", table.Domain);
        cmd.Parameters.AddWithValue("@schema", table.Schema);
        cmd.Parameters.AddWithValue("@table", table.TableName);
        cmd.Parameters.AddWithValue("@apprSid", approverSid.Value);
        cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));

        var result = await cmd.ExecuteScalarAsync(ct);
        return result != null && result != DBNull.Value;
    }

    public async Task<IReadOnlySet<string>> GetTransitiveRolesAsync(Sid subjectSid, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var roles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"SELECT r.role_name
                                FROM ROLES r
                                JOIN ROLE_MEMBERS m ON r.id = m.role_id
                                WHERE m.member_sid = @sid";
            cmd.Parameters.AddWithValue("@sid", subjectSid.Value);

            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                roles.Add(reader.GetString(0));
            }
            return roles;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<TableRelation>> GetRelationsForTableAsync(TableIdentifier parentTable, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var relations = new List<TableRelation>();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"SELECT r.id, r.parent_table_id, COALESCE(tp.source_name, 'default'), tp.schema_name, tp.table_name,
                                       r.child_table_id, COALESCE(tc.source_name, 'default'), tc.schema_name, tc.table_name,
                                       r.relation_name, r.join_key_parent, r.join_key_child, r.cardinality
                                FROM TABLE_RELATIONS r
                                JOIN TABLES tp ON r.parent_table_id = tp.id
                                JOIN TABLES tc ON r.child_table_id = tc.id
                                WHERE tp.source_name = @pDomain COLLATE NOCASE AND tp.schema_name = @pSchema COLLATE NOCASE AND tp.table_name = @pTable COLLATE NOCASE";
            cmd.Parameters.AddWithValue("@pDomain", parentTable.Domain);
            cmd.Parameters.AddWithValue("@pSchema", parentTable.Schema);
            cmd.Parameters.AddWithValue("@pTable", parentTable.TableName);

            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var relId = Guid.Parse(reader.GetString(0));
                var parentId = Guid.Parse(reader.GetString(1));
                var pId = new TableIdentifier(reader.GetString(2), reader.GetString(3), reader.GetString(4));
                var childId = Guid.Parse(reader.GetString(5));
                var cId = new TableIdentifier(reader.GetString(6), reader.GetString(7), reader.GetString(8));
                var relName = reader.GetString(9);
                var jkParent = reader.GetString(10);
                var jkChild = reader.GetString(11);
                var card = Enum.TryParse<RelationCardinality>(reader.GetString(12), true, out var c) ? c : RelationCardinality.OneToMany;

                relations.Add(new TableRelation
                {
                    Id = relId,
                    ParentTableId = parentId,
                    ParentTableIdentifier = pId,
                    ChildTableId = childId,
                    ChildTableIdentifier = cId,
                    RelationName = relName,
                    JoinKeyParent = jkParent,
                    JoinKeyChild = jkChild,
                    Cardinality = card
                });
            }
            return relations;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task CreateRelationAsync(TableRelation relation, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"INSERT OR REPLACE INTO TABLE_RELATIONS 
                                (id, parent_table_id, child_table_id, relation_name, join_key_parent, join_key_child, cardinality)
                                VALUES (@id, @pId, @cId, @relName, @jkP, @jkC, @card)";
            cmd.Parameters.AddWithValue("@id", relation.Id.ToString());
            cmd.Parameters.AddWithValue("@pId", relation.ParentTableId.ToString());
            cmd.Parameters.AddWithValue("@cId", relation.ChildTableId.ToString());
            cmd.Parameters.AddWithValue("@relName", relation.RelationName);
            cmd.Parameters.AddWithValue("@jkP", relation.JoinKeyParent);
            cmd.Parameters.AddWithValue("@jkC", relation.JoinKeyChild);
            cmd.Parameters.AddWithValue("@card", relation.Cardinality.ToString());
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _lock.Release();
        }
    }

}
