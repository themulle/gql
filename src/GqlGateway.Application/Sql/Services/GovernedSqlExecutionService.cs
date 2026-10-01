namespace GqlGateway.Application.Sql.Services;

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Linq;
using System.Security;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GqlGateway.Application.Interfaces;
using GqlGateway.Application.Sql.Interfaces;
using GqlGateway.Domain.Common;
using GqlGateway.Domain.Interfaces;
using GqlGateway.Domain.Model;
using GqlGateway.Domain.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TrinoSqlEngine;
using TrinoSqlEngine.Analysis;

public sealed class GovernedSqlExecutionService : IGovernedSqlExecutionService
{
    private readonly FastSqlEngine _sqlEngine = new();
    private readonly IOptions<GatewayOptions> _options;
    private readonly IPolicyEnforcementService? _policyEnforcement;
    private readonly IConsentResolutionService? _consentResolution;
    private readonly ITableMetadataRepository? _tableRepository;
    private readonly IAuditLogRepository? _auditLogRepository;
    private readonly ISqlConnectionFactory? _connectionFactory;
    private readonly IClientIpResolver? _clientIpResolver;
    private readonly IHostEnvironment? _environment;
    private readonly ILogger<GovernedSqlExecutionService>? _logger;

    public GovernedSqlExecutionService(
        IOptions<GatewayOptions> options,
        IPolicyEnforcementService? policyEnforcement = null,
        IConsentResolutionService? consentResolution = null,
        ITableMetadataRepository? tableRepository = null,
        IAuditLogRepository? auditLogRepository = null,
        ISqlConnectionFactory? connectionFactory = null,
        IClientIpResolver? clientIpResolver = null,
        IHostEnvironment? environment = null,
        ILogger<GovernedSqlExecutionService>? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _policyEnforcement = policyEnforcement;
        _consentResolution = consentResolution;
        _tableRepository = tableRepository;
        _auditLogRepository = auditLogRepository;
        _connectionFactory = connectionFactory;
        _clientIpResolver = clientIpResolver;
        _environment = environment;
        _logger = logger;
    }

    public async Task<string> RewriteSqlAsync(
        string rawSql,
        ClaimsPrincipal user,
        TenantId tenantId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rawSql))
        {
            throw new ArgumentException("SQL query cannot be empty or whitespace.", nameof(rawSql));
        }

        var webSqlOptions = _options.Value.WebSql;
        if (!webSqlOptions.Enabled)
        {
            throw new SecurityException("WebSQL execution is disabled by gateway configuration.");
        }

        if (rawSql.Length > webSqlOptions.MaxQueryLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(rawSql),
                $"SQL query length ({rawSql.Length}) exceeds the maximum allowed limit of {webSqlOptions.MaxQueryLength} characters.");
        }

        // 1. AST Analysis
        var metadata = _sqlEngine.Analyze(rawSql.AsMemory());

        // 2. Validate Statement Type (Reject DDL, allow DML only if explicitly enabled)
        if (metadata.StatementType == SqlStatementType.Ddl)
        {
            throw new SecurityException("DDL statements (CREATE, DROP, ALTER, TRUNCATE, GRANT, REVOKE) are strictly forbidden in WebSQL.");
        }

        if (metadata.StatementType != SqlStatementType.Select && !_options.Value.IsWebSqlDmlAllowed)
        {
            throw new SecurityException(
                $"WebSQL only permits read-only SELECT statements by default. Received '{metadata.StatementType}' operation. DML must be explicitly enabled via configuration.");
        }

        // 3. Danger Bypass: If WebSQL governance is dangerously bypassed in dev/test, return raw SQL
        if (_options.Value.IsWebSqlGovernanceBypassed)
        {
            _logger?.LogWarning("[DANGER] WebSQL governance bypass is active! Query will be executed without RLS or AST masking.");
            return rawSql;
        }

        // 4. Resolve RLS filters and Column Masking for all referenced physical tables
        var tableRlsFilters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var tableMaskingExpressions = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var tableColumnsMap = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

        var userSid = ResolveUserSid(user);
        var groupSids = ResolveGroupSids(user);
        var clientIp = ResolveClientIp(user);
        var purpose = user.FindFirst("purpose")?.Value ?? user.FindFirst("purpose_id")?.Value;

        var attributes = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var claim in user.Claims)
        {
            attributes[claim.Type] = claim.Value;
        }

        foreach (var target in metadata.ReferencedTables)
        {
            // Resolve table metadata from repository
            TableIdentifier tableId = ResolveTableIdentifier(target);
            TableMetadata? tableMeta = null;
            if (_tableRepository != null)
            {
                tableMeta = await _tableRepository.GetTableMetadataAsync(tableId, ct).ConfigureAwait(false);
                if (tableMeta == null && !string.Equals(tableId.Domain, "default", StringComparison.OrdinalIgnoreCase))
                {
                    tableMeta = await _tableRepository.GetTableMetadataAsync(new TableIdentifier("default", tableId.Schema, tableId.TableName), ct).ConfigureAwait(false);
                }
            }

            if (tableMeta != null && tableMeta.Columns.Count > 0)
            {
                var colList = tableMeta.Columns.Select(c => c.ColumnName).ToList();
                tableColumnsMap[target.TableName] = colList;
                tableColumnsMap[target.FullName] = colList;

                // SEC-JOIN-01: Zero-Trust Guardrail: Check if any statically redacted column is used as a JOIN predicate
                if (metadata.JoinConditionColumns != null && metadata.JoinConditionColumns.Count > 0)
                {
                    foreach (var col in tableMeta.Columns)
                    {
                        if (metadata.JoinConditionColumns.Contains(col.ColumnName))
                        {
                            if (tableMeta.ColumnMaskingRules.TryGetValue(col.ColumnName, out var rule))
                            {
                                var rType = rule.RuleType?.ToUpperInvariant() ?? "REDACT";
                                if (rType is "REDACT" or "NULLIFY" or "REGEX")
                                {
                                    throw new SecurityException(
                                        $"Security Policy Violation: Column '{col.ColumnName}' in table '{target.FullName}' is protected by static redaction ('{rule.RuleType}') and cannot be used in a relational JOIN predicate. Joining on static constants produces false Cartesian cross-products and enables side-channel join inference attacks. Configure deterministic HMAC pseudonymization (RuleType = 'HMAC') or join on surrogate foreign keys (e.g. ID).");
                                }
                            }
                        }
                    }
                }
            }

            // ABAC Policy Evaluation
            string? rlsFilter = null;
            var columnMasks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            TableAccessDecision? policyDecision = null;

            if (_policyEnforcement != null && _policyEnforcement.HasPolicies(tenantId))
            {
                var secContext = new SecurityEvaluationContext(
                    UserSid: userSid,
                    GroupSids: groupSids,
                    Tenant: tenantId,
                    TargetTable: tableId,
                    RequestedColumns: tableMeta?.Columns.Select(c => c.ColumnName).ToList() ?? new List<string>(),
                    ClientIp: clientIp,
                    Timestamp: DateTimeOffset.UtcNow,
                    PurposeId: purpose,
                    Attributes: attributes);

                policyDecision = await _policyEnforcement.EvaluatePolicyAsync(secContext, ct).ConfigureAwait(false);
                if (!policyDecision.IsAllowed)
                {
                    var reasons = policyDecision.DeniedReasons.Count > 0
                        ? string.Join("; ", policyDecision.DeniedReasons)
                        : "Table access forbidden by policy.";
                    throw new SecurityException($"Access to table '{target.FullName}' denied by ABAC policy: {reasons}");
                }

                if (!string.IsNullOrWhiteSpace(policyDecision.CombinedRowFilterSql))
                {
                    SqlSecurityValidator.ValidatePredicateSql(policyDecision.CombinedRowFilterSql, "CombinedRowFilterSql");
                    rlsFilter = policyDecision.CombinedRowFilterSql;
                }
            }

            // Resolve column masking rules (from policy or table metadata catalog)
            if (tableMeta != null)
            {
                string hmacSalt = _options.Value.DataMasking.HmacSecretKeyVaultRef ?? _options.Value.DataMasking.HmacKeyId ?? "gateway_salt";
                foreach (var col in tableMeta.Columns)
                {
                    ColumnAccessLevel lvl = ColumnAccessLevel.Clear;
                    if (policyDecision != null)
                    {
                        lvl = policyDecision.GetColumnAccess(col.ColumnName);
                    }
                    else if (tableMeta.ColumnMaskingRules.ContainsKey(col.ColumnName) || col.IsSensitive)
                    {
                        lvl = ColumnAccessLevel.Mask;
                    }

                    if (lvl == ColumnAccessLevel.Deny)
                    {
                        columnMasks[col.ColumnName] = "NULL";
                    }
                    else if (lvl == ColumnAccessLevel.Mask)
                    {
                        columnMasks[col.ColumnName] = GetMaskExpressionForRule(col.ColumnName, tableMeta, hmacSalt);
                    }
                }
            }

            // Fallback tenant filter if no custom filter was resolved
            if (string.IsNullOrWhiteSpace(rlsFilter))
            {
                rlsFilter = $"tenant_id = '{tenantId.Value.Replace("'", "''")}'";
            }

            tableRlsFilters[target.TableName] = rlsFilter;
            tableRlsFilters[target.FullName] = rlsFilter;

            if (columnMasks.Count > 0)
            {
                tableMaskingExpressions[target.TableName] = columnMasks;
                tableMaskingExpressions[target.FullName] = columnMasks;
            }
        }

        // 5. Construct RlsOptions
        long maxRows = webSqlOptions.DefaultMaxRows > 0 ? webSqlOptions.DefaultMaxRows : 1000;
        if (webSqlOptions.MaxAllowedRows > 0 && maxRows > webSqlOptions.MaxAllowedRows)
        {
            maxRows = webSqlOptions.MaxAllowedRows;
        }

        var rlsOptions = new RlsOptions
        {
            AppendTableAlias = true,
            EnforcedMaxRows = maxRows,
            EnforceReadOnlyQueries = !_options.Value.IsWebSqlDmlAllowed,
            PolicyProvider = new DefaultRlsPolicyProvider(
                defaultFilter: $"tenant_id = '{tenantId.Value.Replace("'", "''")}'",
                predicate: tbl => tableRlsFilters.ContainsKey(tbl),
                filterFunc: tbl => tableRlsFilters.TryGetValue(tbl, out var f) ? f : $"tenant_id = '{tenantId.Value.Replace("'", "''")}'"),
            TableColumnsProvider = tbl => tableColumnsMap.TryGetValue(tbl, out var cols) ? cols : null,
            ColumnMaskingProvider = new DefaultColumnMaskingPolicyProvider(
                hasMaskPredicate: (tbl, col) => tableMaskingExpressions.TryGetValue(tbl, out var dict) && dict.ContainsKey(col),
                maskExpressionProvider: (tbl, col) => tableMaskingExpressions.TryGetValue(tbl, out var dict) && dict.TryGetValue(col, out var expr) ? expr : "'***'")
        };

        // 6. Rewrite SQL AST
        return _sqlEngine.RewriteRls(rawSql.AsMemory(), rlsOptions);
    }

    public async Task ExecuteGovernedQueryAsync(
        GovernedSqlQueryRequest request,
        ClaimsPrincipal user,
        TenantId tenantId,
        Func<DbDataReader, CancellationToken, Task> rowWriter,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(rowWriter);

        string securedSql = await RewriteSqlAsync(request.Sql, user, tenantId, ct).ConfigureAwait(false);

        // Audit Log Entry
        if (_auditLogRepository != null)
        {
            var userSid = ResolveUserSid(user);
            await _auditLogRepository.RecordAuditEventAsync(new AuditLogEntry
            {
                TenantId = tenantId,
                EventType = "WEBSQL_QUERY",
                ActorSid = userSid,
                TargetTable = request.DataSourceName ?? _options.Value.WebSql.DefaultDataSourceName,
                Decision = "ALLOW",
                TraceId = Guid.NewGuid().ToString("N"),
                DetailsJson = JsonSerializer.Serialize(new
                {
                    originalSql = request.Sql,
                    securedSql,
                    dataSource = request.DataSourceName ?? _options.Value.WebSql.DefaultDataSourceName
                })
            }, ct).ConfigureAwait(false);
        }

        // Connection resolution
        string dsName = request.DataSourceName ?? _options.Value.WebSql.DefaultDataSourceName;
        var connections = _options.Value.DataSources?.Connections;
        DataSourceConnectionOptions? connOptions = null;
        connections?.TryGetValue(dsName, out connOptions);

        if (connOptions == null || string.IsNullOrWhiteSpace(connOptions.ConnectionString) || _connectionFactory == null)
        {
            bool isDevOrTest = _environment == null ||
                               string.Equals(_environment.EnvironmentName, "Development", StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(_environment.EnvironmentName, "Testing", StringComparison.OrdinalIgnoreCase);
            bool isExplicitlyAllowed = _options.Value.AreExternalSystemsMockedIfUnreachable;

            if (!isDevOrTest && !isExplicitlyAllowed)
            {
                throw new InvalidOperationException($"No active database connection configured for data source '{dsName}'. Synthetic fallback is disabled in production.");
            }

            // Synthetic demo reader for testing/dev environments without a backing DB
            using var syntheticReader = new SyntheticDataTableReader(securedSql);
            await rowWriter(syntheticReader, ct).ConfigureAwait(false);
            return;
        }

        // Real Database Execution
        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(connOptions, ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = securedSql;
        command.CommandTimeout = Math.Max(1, _options.Value.WebSql.ExecutionTimeoutSeconds);

        if (request.Parameters != null)
        {
            foreach (var (paramName, paramVal) in request.Parameters)
            {
                var p = command.CreateParameter();
                p.ParameterName = paramName.StartsWith('@') ? paramName : "@" + paramName;
                p.Value = paramVal ?? DBNull.Value;
                command.Parameters.Add(p);
            }
        }

        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct).ConfigureAwait(false);
        await rowWriter(reader, ct).ConfigureAwait(false);
    }

    public async Task<GovernedSqlResult> ExecuteQueryBufferedAsync(
        GovernedSqlQueryRequest request,
        ClaimsPrincipal user,
        TenantId tenantId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        string securedSql = await RewriteSqlAsync(request.Sql, user, tenantId, ct).ConfigureAwait(false);
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        var columns = new List<string>();
        var sw = Stopwatch.StartNew();

        await ExecuteGovernedQueryAsync(
            request,
            user,
            tenantId,
            async (reader, token) =>
            {
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    columns.Add(reader.GetName(i));
                }

                while (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    var row = new Dictionary<string, object?>(reader.FieldCount, StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < reader.FieldCount; i++)
                    {
                        row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                    }
                    rows.Add(row);
                }
            },
            ct).ConfigureAwait(false);

        sw.Stop();

        return new GovernedSqlResult(
            OriginalSql: request.Sql,
            RewrittenSql: securedSql,
            Columns: columns.AsReadOnly(),
            Rows: rows.AsReadOnly(),
            RowCount: rows.Count,
            ElapsedMilliseconds: sw.ElapsedMilliseconds);
    }

    private static Sid ResolveUserSid(ClaimsPrincipal user)
    {
        return user.GetUserSid() ?? new Sid("anonymous");
    }

    private static IReadOnlyList<Sid> ResolveGroupSids(ClaimsPrincipal user)
    {
        return user.GetGroupSids().ToList();
    }

    private System.Net.IPAddress ResolveClientIp(ClaimsPrincipal user)
    {
        return _clientIpResolver?.ResolveClientIp() ??
            (user.FindFirst("ip")?.Value is { Length: > 0 } ipStr && System.Net.IPAddress.TryParse(ipStr, out var parsedIp)
                ? parsedIp
                : System.Net.IPAddress.Loopback);
    }

    private static TableIdentifier ResolveTableIdentifier(TableAccessTarget target)
    {
        if (TableIdentifier.TryParse(target.FullName, out var parsed))
        {
            return parsed;
        }

        string domain = !string.IsNullOrWhiteSpace(target.Catalog) ? target.Catalog : "default";
        string schema = !string.IsNullOrWhiteSpace(target.Schema) ? target.Schema : "public";
        return new TableIdentifier(domain, schema, target.TableName);
    }

    private static string GetMaskExpressionForRule(string columnName, TableMetadata tableMeta, string hmacSalt)
    {
        if (tableMeta.ColumnMaskingRules.TryGetValue(columnName, out var rule))
        {
            var ruleType = rule.RuleType?.ToUpperInvariant() ?? "REDACT";
            if (ruleType == "NULLIFY")
            {
                return "NULL";
            }
            if (ruleType is "HMAC" or "HMAC_SHA256" or "HASH")
            {
                return BuildDeterministicHashExpression(columnName, tableMeta.Dialect, hmacSalt);
            }
            if (!string.IsNullOrWhiteSpace(rule.Replacement))
            {
                return $"'{rule.Replacement.Replace("'", "''")}'";
            }
        }
        return "'***'";
    }

    private static string BuildDeterministicHashExpression(string columnName, DatabaseDialect dialect, string salt)
    {
        string safeSalt = salt.Replace("'", "''");
        return dialect switch
        {
            DatabaseDialect.PostgreSql => $"ENCODE(DIGEST(CAST({columnName} AS TEXT) || '{safeSalt}', 'sha256'), 'hex')",
            DatabaseDialect.SqlServer => $"CONVERT(VARCHAR(64), HASHBYTES('SHA2_256', CAST({columnName} AS VARCHAR(MAX)) + '{safeSalt}'), 2)",
            DatabaseDialect.Sqlite => $"'hmac_' || HEX(SUBSTR({columnName} || '{safeSalt}', 1, 16))",
            _ => $"'hmac_' || HEX(SUBSTR({columnName} || '{safeSalt}', 1, 16))"
        };
    }

    /// <summary>
    /// Lightweight synthetic reader for developer / unit test environments where no physical DB is attached.
    /// </summary>
    private sealed class SyntheticDataTableReader : DbDataReader
    {
        private readonly string[] _columns = { "status", "query", "governed" };
        private readonly object?[] _values;
        private bool _readDone;

        public SyntheticDataTableReader(string rewrittenSql)
        {
            _values = new object?[] { "ok", rewrittenSql, true };
        }

        public override int FieldCount => _columns.Length;
        public override bool HasRows => true;
        public override bool IsClosed => false;
        public override int RecordsAffected => 0;
        public override int Depth => 0;

        public override bool Read()
        {
            if (!_readDone)
            {
                _readDone = true;
                return true;
            }
            return false;
        }

        public override Task<bool> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(Read());
        public override string GetName(int ordinal) => _columns[ordinal];
        public override int GetOrdinal(string name) => Array.FindIndex(_columns, c => string.Equals(c, name, StringComparison.OrdinalIgnoreCase));
        public override object GetValue(int ordinal) => _values[ordinal] ?? DBNull.Value;
        public override int GetValues(object[] values)
        {
            int count = Math.Min(values.Length, _values.Length);
            for (int i = 0; i < count; i++) values[i] = _values[i] ?? DBNull.Value;
            return count;
        }
        public override bool IsDBNull(int ordinal) => _values[ordinal] == null;
        public override Type GetFieldType(int ordinal) => typeof(string);
        public override string GetDataTypeName(int ordinal) => "varchar";
        public override System.Collections.IEnumerator GetEnumerator() => throw new NotSupportedException();
        public override bool NextResult() => false;
        public override Task<bool> NextResultAsync(CancellationToken cancellationToken) => Task.FromResult(false);
        public override bool GetBoolean(int ordinal) => Convert.ToBoolean(_values[ordinal]);
        public override byte GetByte(int ordinal) => Convert.ToByte(_values[ordinal]);
        public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => 0;
        public override char GetChar(int ordinal) => Convert.ToChar(_values[ordinal]!);
        public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => 0;
        public override DateTime GetDateTime(int ordinal) => Convert.ToDateTime(_values[ordinal]);
        public override decimal GetDecimal(int ordinal) => Convert.ToDecimal(_values[ordinal]);
        public override double GetDouble(int ordinal) => Convert.ToDouble(_values[ordinal]);
        public override float GetFloat(int ordinal) => Convert.ToSingle(_values[ordinal]);
        public override Guid GetGuid(int ordinal) => Guid.Parse(_values[ordinal]!.ToString()!);
        public override short GetInt16(int ordinal) => Convert.ToInt16(_values[ordinal]);
        public override int GetInt32(int ordinal) => Convert.ToInt32(_values[ordinal]);
        public override long GetInt64(int ordinal) => Convert.ToInt64(_values[ordinal]);
        public override string GetString(int ordinal) => _values[ordinal]?.ToString() ?? string.Empty;
        public override object this[int ordinal] => GetValue(ordinal);
        public override object this[string name] => GetValue(GetOrdinal(name));
    }
}
