using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using GqlGateway.Application.Interfaces;
using GqlGateway.Domain.Common;
using GqlGateway.Domain.Interfaces;
using GqlGateway.Domain.Model;
using GqlGateway.GraphQL.Types;
using HotChocolate;

namespace GqlGateway.GraphQL.Services;

public sealed partial class GatewayExecutionService
{
    [GeneratedRegex(@"(?:\[[a-zA-Z0-9_]+\]|[a-zA-Z_][a-zA-Z0-9_]*)\.(\[?[a-zA-Z_][a-zA-Z0-9_]*\]?)", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex TablePrefixRegex();

    [GeneratedRegex(@"""([a-zA-Z0-9_]+)""", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex DoubleQuotedIdentifierRegex();

    [GeneratedRegex(@"\(\s*([a-zA-Z0-9_, \[\]]+)\s*\)\s+IN\s*\(\s*(\(.*?\))\s*\)", RegexOptions.IgnoreCase | RegexOptions.Singleline, matchTimeoutMilliseconds: 1000)]
    private static partial Regex TupleInRegex();

    private readonly ITableMetadataRepository _metadataRepository;
    private readonly IConsentRepository _consentRepository;
    private readonly IAuditLogRepository _auditLogRepository;
    private readonly IConsentResolutionService _resolutionService;
    private readonly IConsentCacheService _cacheService;
    private readonly IColumnMaskingProvider _maskingProvider;
    private readonly IChunkedQueryExecutor _chunkedQueryExecutor;
    private readonly GatewayOptions? _options;
    private readonly ITrafficDrainController? _drainController;

    public int LastDispatchedChildQueryCount { get; private set; }

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public GatewayExecutionService(
        ITableMetadataRepository metadataRepository,
        IConsentRepository consentRepository,
        IAuditLogRepository auditLogRepository,
        IConsentResolutionService resolutionService,
        IConsentCacheService cacheService,
        IColumnMaskingProvider maskingProvider,
        IChunkedQueryExecutor? chunkedQueryExecutor = null,
        Microsoft.Extensions.Options.IOptions<GatewayOptions>? options = null,
        ITrafficDrainController? drainController = null)
    {
        _metadataRepository = metadataRepository;
        _consentRepository = consentRepository;
        _auditLogRepository = auditLogRepository;
        _resolutionService = resolutionService;
        _cacheService = cacheService;
        _maskingProvider = maskingProvider;
        _chunkedQueryExecutor = chunkedQueryExecutor ?? new GqlGateway.Application.Services.ChunkedQueryExecutor(500);
        _options = options?.Value;
        _drainController = drainController;
    }

    public GatewayExecutionService(
        IGovernanceRepository repository,
        IConsentResolutionService resolutionService,
        IConsentCacheService cacheService,
        IColumnMaskingProvider maskingProvider,
        IChunkedQueryExecutor? chunkedQueryExecutor = null,
        Microsoft.Extensions.Options.IOptions<GatewayOptions>? options = null,
        ITrafficDrainController? drainController = null)
        : this(repository, repository, repository, resolutionService, cacheService, maskingProvider, chunkedQueryExecutor, options, drainController)
    {
    }

    public async Task<(IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows, TableAccessDecision Decision)> ExecuteTableQueryAsync(
        ClaimsPrincipal? principal,
        TableIdentifier table,
        int first = 50,
        int after = 0,
        CancellationToken ct = default)
    {
        using var _ = _drainController?.TrackQuery();
        if (principal == null || principal.Identity?.IsAuthenticated != true)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("UNAUTHORIZED")
                .SetMessage("Authentication is required to query tables.")
                .Build());
        }

        var userSidNullable = principal.GetUserSid();
        if (userSidNullable == null)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("UNAUTHORIZED")
                .SetMessage("Keine gültige Benutzer-SID im Authentifizierungstoken vorhanden.")
                .Build());
        }
        var userSid = userSidNullable.Value;

        var groupSids = principal.FindAll(ClaimTypes.GroupSid)
            .Select(c => new Sid(c.Value))
            .ToHashSet();

        var roles = principal.FindAll(ClaimTypes.Role)
            .Select(c => c.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Verify table existence in metadata catalog
        var metadata = await _metadataRepository.GetTableMetadataAsync(table, ct);
        if (metadata == null)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("NOT_FOUND")
                .SetMessage($"Tabelle '{table}' existiert nicht im Metadatenkatalog.")
                .Build());
        }

        // Check Consent Cache (L1/L2 with Epoch Validation & Group/Role Context Hash)
        var contextHash = IConsentCacheService.ComputeSubjectContextHash(groupSids, roles);
        var decision = await _cacheService.GetCachedDecisionAsync(userSid, table, contextHash, ct);
        if (decision == null)
        {
            // Cache Miss -> Load from Governance DB
            var allSubjects = groupSids.Append(userSid).ToList();
            var activeConsents = await _consentRepository.GetActiveConsentsForSubjectsAsync(allSubjects, table, DateTimeOffset.UtcNow, ct);

            decision = _resolutionService.ResolveAccess(userSid, groupSids, roles, table, activeConsents, metadata.Dialect);

            // Cache decision
            var ttl = metadata.Table.IsHighlySensitive
                ? TimeSpan.FromSeconds(60)
                : TimeSpan.FromMinutes(10);
            await _cacheService.SetCachedDecisionAsync(userSid, table, decision, ttl, contextHash, ct);
        }

        // Audit evaluation
        var traceId = Guid.NewGuid().ToString("N");
        await _auditLogRepository.RecordAuditEventAsync(new AuditLogEntry
        {
            EventType = "TABLE_QUERY",
            ActorSid = userSid,
            TargetTable = table.ToString(),
            Decision = decision.IsAllowed ? "ALLOW" : "DENY",
            TraceId = traceId,
            DetailsJson = JsonSerializer.Serialize(new { is_allowed = decision.IsAllowed, reasons = decision.DeniedReasons })
        }, ct);

        // Enforce Access
        if (!decision.IsAllowed)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("FORBIDDEN")
                .SetMessage($"Zugriff auf Tabelle '{table}' verweigert: {string.Join("; ", decision.DeniedReasons)}")
                .Build());
        }

        // Generate synthetic query result (enforcing configured MaxResponseRows)
        var maxRows = _options?.GraphQL?.MaxResponseRows > 0 ? _options.GraphQL.MaxResponseRows : 1000;
        var rowLimit = Math.Clamp(first, 1, maxRows);
        var mockRows = GenerateMockRows(metadata, decision, rowLimit, after);

        // Enforce configured MaxResponseBytes
        var maxBytes = _options?.GraphQL?.MaxResponseBytes > 0 ? _options.GraphQL.MaxResponseBytes : 10 * 1024 * 1024;
        long estimatedBytes = 0;
        foreach (var row in mockRows)
        {
            foreach (var kvp in row)
            {
                estimatedBytes += kvp.Key.Length * 2;
                if (kvp.Value is string s)
                {
                    estimatedBytes += s.Length * 2;
                }
                else if (kvp.Value is byte[] b)
                {
                    estimatedBytes += b.Length;
                }
                else
                {
                    estimatedBytes += 16;
                }
            }
        }

        if (estimatedBytes > maxBytes)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("RESPONSE_TOO_LARGE")
                .SetMessage($"Antwortgröße ({estimatedBytes} Bytes) überschreitet das konfigurierte Limit von {maxBytes} Bytes.")
                .Build());
        }

        return (mockRows, decision);
    }

    private List<IReadOnlyDictionary<string, object?>> GenerateMockRows(
        TableMetadata metadata,
        TableAccessDecision decision,
        int count,
        int offset)
    {
        var rows = new List<IReadOnlyDictionary<string, object?>>();

        for (int i = 1; i <= count; i++)
        {
            var rowNum = offset + i;
            var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

            foreach (var col in metadata.Columns)
            {
                var access = decision.GetColumnAccess(col.ColumnName);

                if (access == ColumnAccessLevel.Deny)
                {
                    continue; // Strip denied column
                }

                object? rawVal = col.ColumnName.ToLowerInvariant() switch
                {
                    "id" => rowNum,
                    "name" => $"Sample {metadata.Identifier.TableName} Record #{rowNum}",
                    "amount" => 100.50m * rowNum,
                    "email" => $"user{rowNum}@corp.local",
                    "created_at" => DateTimeOffset.UtcNow.AddDays(-rowNum),
                    _ => $"Value_{rowNum}"
                };

                if (access == ColumnAccessLevel.Mask)
                {
                    var rule = metadata.ColumnMaskingRules.TryGetValue(col.ColumnName, out var r) ? r : new MaskingRule { RuleType = "REDACT" };
                    rawVal = _maskingProvider.MaskValue(col.ColumnName, rawVal, rule);
                }

                dict[col.ColumnName] = rawVal;
            }

            rows.Add(dict);
        }

        if (!string.IsNullOrWhiteSpace(decision.CombinedRowFilterSql))
        {
            rows = FilterRows(rows, decision.CombinedRowFilterSql, metadata);
        }

        return rows;
    }

    private static List<IReadOnlyDictionary<string, object?>> FilterRows(
        List<IReadOnlyDictionary<string, object?>> rows,
        string rowFilterSql,
        TableMetadata metadata)
    {
        if (rows.Count == 0 || string.IsNullOrWhiteSpace(rowFilterSql))
        {
            return rows;
        }

        var normalizedSql = NormalizeRowFilterForInMemoryEvaluation(rowFilterSql);
        if (string.IsNullOrWhiteSpace(normalizedSql))
        {
            // Zero Trust: When a row filter is defined but cannot be safely evaluated in-memory, fail closed
            return new List<IReadOnlyDictionary<string, object?>>();
        }

        try
        {
            using var dt = new System.Data.DataTable();
            foreach (var col in metadata.Columns)
            {
                Type colType = col.DataType.ToLowerInvariant() switch
                {
                    var d when d.Contains("bigint") || d.Contains("long") => typeof(long),
                    var d when d.Contains("int") => typeof(int),
                    var d when d.Contains("decimal") || d.Contains("numeric") || d.Contains("money") => typeof(decimal),
                    var d when d.Contains("bool") => typeof(bool),
                    var d when d.Contains("date") || d.Contains("time") => typeof(DateTime),
                    _ => typeof(string)
                };
                dt.Columns.Add(col.ColumnName, colType);
            }

            var rowMap = new Dictionary<System.Data.DataRow, IReadOnlyDictionary<string, object?>>();
            foreach (var r in rows)
            {
                var dr = dt.NewRow();
                foreach (var col in metadata.Columns)
                {
                    if (r.TryGetValue(col.ColumnName, out var v) && v != null)
                    {
                        if (v is DateTimeOffset dto)
                        {
                            dr[col.ColumnName] = dto.UtcDateTime;
                        }
                        else
                        {
                            dr[col.ColumnName] = v;
                        }
                    }
                    else
                    {
                        dr[col.ColumnName] = DBNull.Value;
                    }
                }
                dt.Rows.Add(dr);
                rowMap[dr] = r;
            }

            var matchedDataRows = dt.Select(normalizedSql);
            return matchedDataRows.Select(dr => rowMap[dr]).ToList();
        }
        catch
        {
            // Strict Fail-Closed if expression cannot be evaluated
            return new List<IReadOnlyDictionary<string, object?>>();
        }
    }

    private static string? NormalizeRowFilterForInMemoryEvaluation(string rowFilterSql)
    {
        if (string.IsNullOrWhiteSpace(rowFilterSql))
        {
            return null;
        }

        var trimmed = rowFilterSql.Trim();

        // 1. Subqueries like EXISTS (...) cannot be evaluated against mock in-memory DataTables
        if (trimmed.StartsWith("EXISTS", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("NOT EXISTS", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var normalized = trimmed;

        // 2. Strip table prefixes: [alias].[col] -> [col] or alias.col -> col
        normalized = TablePrefixRegex().Replace(normalized, "$1");

        // 3. Normalize double-quoted identifiers: "col" -> [col]
        normalized = DoubleQuotedIdentifierRegex().Replace(normalized, "[$1]");

        // 4. Expand Tuple-IN clauses: (col1, col2) IN ((v1, v2), (v3, v4)) -> ((([col1] = v1) AND ([col2] = v2)) OR ...)
        var tupleInRegex = TupleInRegex();

        while (true)
        {
            var match = tupleInRegex.Match(normalized);
            if (!match.Success)
            {
                break;
            }

            var colNames = match.Groups[1].Value
                .Split(',')
                .Select(c => c.Trim().Trim('[', ']'))
                .ToArray();

            var tuples = ParseTuples(match.Groups[2].Value);
            string replacement;

            if (tuples.Count == 0)
            {
                replacement = "(1 = 0)";
            }
            else
            {
                var orClauses = new List<string>();
                foreach (var tuple in tuples)
                {
                    var andClauses = new List<string>();
                    for (int i = 0; i < colNames.Length && i < tuple.Count; i++)
                    {
                        andClauses.Add($"([{colNames[i]}] = {tuple[i]})");
                    }
                    orClauses.Add($"({string.Join(" AND ", andClauses)})");
                }
                replacement = $"({string.Join(" OR ", orClauses)})";
            }

            normalized = normalized.Remove(match.Index, match.Length).Insert(match.Index, replacement);
        }

        return normalized;
    }

    private static List<List<string>> ParseTuples(string tupleString)
    {
        var tuples = new List<List<string>>();
        var currentTuple = new List<string>();
        var currentVal = new System.Text.StringBuilder();
        bool inQuote = false;
        bool inTuple = false;

        for (int i = 0; i < tupleString.Length; i++)
        {
            char c = tupleString[i];
            if (c == '\'')
            {
                currentVal.Append(c);
                if (inQuote && i + 1 < tupleString.Length && tupleString[i + 1] == '\'')
                {
                    currentVal.Append('\'');
                    i++;
                }
                else
                {
                    inQuote = !inQuote;
                }
            }
            else if (inQuote)
            {
                currentVal.Append(c);
            }
            else if (c == '(')
            {
                inTuple = true;
                currentTuple = new List<string>();
                currentVal.Clear();
            }
            else if (c == ')')
            {
                if (inTuple)
                {
                    currentTuple.Add(currentVal.ToString().Trim());
                    currentVal.Clear();
                    tuples.Add(currentTuple);
                    inTuple = false;
                }
            }
            else if (c == ',' && inTuple)
            {
                currentTuple.Add(currentVal.ToString().Trim());
                currentVal.Clear();
            }
            else if (inTuple)
            {
                currentVal.Append(c);
            }
        }

        return tuples;
    }

    public async Task<TableAccessDecision> CheckTableAccessAsync(
        ClaimsPrincipal? principal,
        TableIdentifier table,
        CancellationToken ct = default)
    {
        if (principal == null || principal.Identity?.IsAuthenticated != true)
        {
            return TableAccessDecision.Denied(table, "Authentication required");
        }

        var userSidNullable = principal.GetUserSid();
        if (userSidNullable == null)
        {
            return TableAccessDecision.Denied(table, "Keine gültige Benutzer-SID im Authentifizierungstoken vorhanden.");
        }
        var userSid = userSidNullable.Value;

        var groupSids = principal.FindAll(ClaimTypes.GroupSid)
            .Select(c => new Sid(c.Value))
            .ToHashSet();

        var roles = principal.FindAll(ClaimTypes.Role)
            .Select(c => c.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var metadata = await _metadataRepository.GetTableMetadataAsync(table, ct);
        if (metadata == null)
        {
            return TableAccessDecision.Denied(table, $"Table '{table}' not found in catalog");
        }

        var contextHash = IConsentCacheService.ComputeSubjectContextHash(groupSids, roles);
        var decision = await _cacheService.GetCachedDecisionAsync(userSid, table, contextHash, ct);
        if (decision == null)
        {
            var allSubjects = groupSids.Append(userSid).ToList();
            var activeConsents = await _consentRepository.GetActiveConsentsForSubjectsAsync(allSubjects, table, DateTimeOffset.UtcNow, ct);
            decision = _resolutionService.ResolveAccess(userSid, groupSids, roles, table, activeConsents, metadata.Dialect);

            var ttl = metadata.Table.IsHighlySensitive
                ? TimeSpan.FromSeconds(60)
                : TimeSpan.FromMinutes(10);
            await _cacheService.SetCachedDecisionAsync(userSid, table, decision, ttl, contextHash, ct);
        }

        var traceId = Guid.NewGuid().ToString("N");
        await _auditLogRepository.RecordAuditEventAsync(new AuditLogEntry
        {
            EventType = "CHILD_RELATION_CHECK",
            ActorSid = userSid,
            TargetTable = table.ToString(),
            Decision = decision.IsAllowed ? "ALLOW" : "DENY",
            TraceId = traceId,
            DetailsJson = JsonSerializer.Serialize(new { is_allowed = decision.IsAllowed, reasons = decision.DeniedReasons })
        }, ct);

        return decision;
    }

    public async Task<IReadOnlyList<InvoiceRecord>> GetInvoicesWithItemsAsync(
        ClaimsPrincipal? principal,
        int first = 10,
        CancellationToken ct = default)
    {
        var parentTableId = new TableIdentifier("finance", "dbo", "finance_table_1");
        var (rows, _) = await ExecuteTableQueryAsync(principal, parentTableId, first, 0, ct);

        var invoices = new List<InvoiceRecord>();
        foreach (var r in rows)
        {
            invoices.Add(new InvoiceRecord
            {
                Id = r.TryGetValue("id", out var id) && id != null ? id.ToString()! : Guid.NewGuid().ToString(),
                Amount = r.TryGetValue("amount", out var amt) && amt is decimal d ? d : 1500.00m,
                Vendor = r.TryGetValue("name", out var n) && n != null ? n.ToString()! : "Vendor Alpha",
                Email = r.TryGetValue("email", out var em) ? em?.ToString() : null
            });
        }
        return invoices;
    }

    public async Task<IReadOnlyDictionary<string, List<InvoiceItemRecord>>> LoadInvoiceItemsBatchAsync(
        ClaimsPrincipal? principal,
        IReadOnlyList<string> invoiceIds,
        CancellationToken ct = default)
    {
        using var _ = _drainController?.TrackQuery();
        var childTableId = new TableIdentifier("finance", "dbo", "finance_items");
        var decision = await CheckTableAccessAsync(principal, childTableId, ct);
        if (!decision.IsAllowed)
        {
            LastDispatchedChildQueryCount = 0;
            return new Dictionary<string, List<InvoiceItemRecord>>();
        }

        var metadata = await _metadataRepository.GetTableMetadataAsync(childTableId, ct);

        // Sonderfall: Wenn IN-Liste aufgrund vieler IDs zu lang wird (RDBMS Parameter-/Puffer-Limit),
        // teilen wir die Abfrage in mehrere parametrisierte Teilabfragen auf und aggregieren die Ergebnisse.
        var (result, dispatchedQueries) = await _chunkedQueryExecutor.ExecuteGroupedWithMetricsAsync(
            invoiceIds,
            (chunkKeys, _) =>
            {
                var chunkResult = new Dictionary<string, List<InvoiceItemRecord>>(chunkKeys.Count);

                foreach (var invId in chunkKeys)
                {
                    var items = new List<InvoiceItemRecord>();
                    for (int i = 1; i <= 2; i++)
                    {
                        var rawNote = $"Confidential spec for item {i} of invoice {invId}";
                        object? maskedNote = rawNote;

                        var noteAccess = decision.GetColumnAccess("sensitive_note");

                        if (noteAccess == ColumnAccessLevel.Deny)
                        {
                            maskedNote = null;
                        }
                        else if (noteAccess == ColumnAccessLevel.Mask)
                        {
                            var rule = metadata != null && metadata.ColumnMaskingRules.TryGetValue("sensitive_note", out var r)
                                ? r
                                : new MaskingRule { RuleType = "REDACT" };
                            maskedNote = _maskingProvider.MaskValue("sensitive_note", rawNote, rule);
                        }

                        var prodAccess = decision.GetColumnAccess("product_name");
                        string? prodName = prodAccess switch
                        {
                            ColumnAccessLevel.Clear => $"Enterprise License Pack {i}",
                            ColumnAccessLevel.Mask => "***",
                            _ => null
                        };

                        var priceAccess = decision.GetColumnAccess("price");
                        decimal price = priceAccess switch
                        {
                            ColumnAccessLevel.Clear => 1250.00m * i,
                            _ => 0m
                        };

                        items.Add(new InvoiceItemRecord
                        {
                            Id = $"{invId}-ITEM-{i}",
                            InvoiceId = invId,
                            ProductName = prodName,
                            Price = price,
                            SensitiveNote = maskedNote?.ToString()
                        });
                    }

                    if (!string.IsNullOrWhiteSpace(decision.CombinedRowFilterSql))
                    {
                        var rowDicts = items.Select(item => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["id"] = item.Id,
                            ["invoice_id"] = item.InvoiceId,
                            ["product_name"] = item.ProductName,
                            ["price"] = item.Price,
                            ["sensitive_note"] = item.SensitiveNote
                        }).ToList();

                        var filteredDicts = FilterRows(rowDicts, decision.CombinedRowFilterSql, metadata ?? new TableMetadata { Identifier = childTableId });
                        var filteredIds = filteredDicts.Select(d => d.TryGetValue("id", out var v) ? v?.ToString() : null).ToHashSet();
                        items = items.Where(item => filteredIds.Contains(item.Id)).ToList();
                    }

                    chunkResult[invId] = items;
                }

                return Task.FromResult<IReadOnlyDictionary<string, List<InvoiceItemRecord>>>(chunkResult);
            },
            chunkSize: _options?.GraphQL?.MaxInClauseBatchSize,
            ct: ct);

        LastDispatchedChildQueryCount = dispatchedQueries;
        return result;
    }
}
