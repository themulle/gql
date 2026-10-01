using System;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using GqlGateway.Application.Connectors;
using GqlGateway.Application.Connectors.Pushdown;
using GqlGateway.Application.Interfaces;
using GqlGateway.Domain.Common;
using GqlGateway.Domain.Connectors;
using GqlGateway.Domain.Interfaces;
using GqlGateway.Domain.Model;
using Microsoft.Extensions.Logging;

namespace GqlGateway.Application.Connectors.CrossDomain;

public sealed record CrossDomainJoinRequest(
    TableIdentifier PrimaryTable,
    TableIdentifier JoinedTable,
    string ForeignKeyColumn,
    string PrimaryKeyColumn,
    string TargetRelationPropertyName,
    ClaimsPrincipal Principal,
    TenantId? Tenant,
    IReadOnlyList<string>? PrimaryProjectedColumns = null,
    IReadOnlyList<string>? JoinedProjectedColumns = null,
    int Limit = 100,
    int Offset = 0,
    IReadOnlyDictionary<string, string[]>? RequestHeaders = null);

public sealed record CrossDomainJoinResult(
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows,
    bool JoinedTableAccessAllowed,
    int JoinedEntitiesMergedCount);

public interface ICrossDomainJoinEngine
{
    Task<CrossDomainJoinResult> ExecuteJoinAsync(
        CrossDomainJoinRequest request,
        CancellationToken ct = default);
}

public sealed class CrossDomainJoinEngine : ICrossDomainJoinEngine
{
    private readonly IGqlGatewayConnectorRegistry _connectorRegistry;
    private readonly ITableMetadataRepository _metadataRepository;
    private readonly ICrossDomainAccessResolver _accessResolver;
    private readonly IColumnMaskingProvider _maskingProvider;
    private readonly ILogger<CrossDomainJoinEngine>? _logger;

    public CrossDomainJoinEngine(
        IGqlGatewayConnectorRegistry connectorRegistry,
        ITableMetadataRepository metadataRepository,
        ICrossDomainAccessResolver accessResolver,
        IColumnMaskingProvider maskingProvider,
        ILogger<CrossDomainJoinEngine>? logger = null)
    {
        _connectorRegistry = connectorRegistry ?? throw new ArgumentNullException(nameof(connectorRegistry));
        _metadataRepository = metadataRepository ?? throw new ArgumentNullException(nameof(metadataRepository));
        _accessResolver = accessResolver ?? throw new ArgumentNullException(nameof(accessResolver));
        _maskingProvider = maskingProvider ?? throw new ArgumentNullException(nameof(maskingProvider));
        _logger = logger;
    }

    public async Task<CrossDomainJoinResult> ExecuteJoinAsync(
        CrossDomainJoinRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // 1. Resolve metadata for both tables
        var primaryMeta = await _metadataRepository.GetTableMetadataAsync(request.PrimaryTable, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Primärtabelle '{request.PrimaryTable}' wurde im Katalog nicht gefunden.");

        var joinedMeta = await _metadataRepository.GetTableMetadataAsync(request.JoinedTable, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Beigetretene Tabelle '{request.JoinedTable}' wurde im Katalog nicht gefunden.");

        // 2. Resolve connectors
        if (!_connectorRegistry.TryGetConnectorForTable(request.PrimaryTable, out var primaryConnector) || primaryConnector == null)
        {
            throw new InvalidOperationException($"Kein aktiver Connector für Primärdomäne '{request.PrimaryTable.Domain}' registriert.");
        }

        if (!_connectorRegistry.TryGetConnectorForTable(request.JoinedTable, out var joinedConnector) || joinedConnector == null)
        {
            throw new InvalidOperationException($"Kein aktiver Connector für Beigetretene Domäne '{request.JoinedTable.Domain}' registriert.");
        }

        // 3. SEC-CDJ-02: Independent Zero-Trust Consent & ABAC evaluation
        var primaryDecision = await _accessResolver.ResolveAccessAsync(
            request.Principal,
            request.PrimaryTable,
            primaryMeta,
            request.Tenant,
            ct).ConfigureAwait(false);

        if (!primaryDecision.IsAllowed)
        {
            throw new SecurityException($"Zero-Trust-Verletzung: Zugriff auf Primärtabelle '{request.PrimaryTable}' verweigert: {string.Join("; ", primaryDecision.DeniedReasons)}");
        }

        var joinedDecision = await _accessResolver.ResolveAccessAsync(
            request.Principal,
            request.JoinedTable,
            joinedMeta,
            request.Tenant,
            ct).ConfigureAwait(false);

        // 4. Fetch Primary Entities (Driving Table Scan)
        var primaryProjected = (request.PrimaryProjectedColumns != null && request.PrimaryProjectedColumns.Count > 0)
            ? request.PrimaryProjectedColumns
            : primaryMeta.Columns.Select(c => c.ColumnName).ToList();

        // Ensure Foreign Key is included in primary projection for join stitching
        if (!primaryProjected.Contains(request.ForeignKeyColumn, StringComparer.OrdinalIgnoreCase))
        {
            primaryProjected = [.. primaryProjected, request.ForeignKeyColumn];
        }

        var primarySession = new ConnectorSessionContext(
            Principal: request.Principal,
            Tenant: request.Tenant,
            AccessDecision: primaryDecision,
            ProjectedColumns: primaryProjected,
            Arguments: new Dictionary<string, object?>
            {
                ["limit"] = request.Limit,
                ["offset"] = request.Offset
            },
            PushdownFilterSql: primaryDecision.CombinedRowFilterSql,
            Limit: request.Limit,
            Offset: request.Offset,
            RequestHeaders: request.RequestHeaders);

        primarySession.Items["TableMetadata"] = primaryMeta;

        var primarySplits = await primaryConnector.SplitManager.GetSplitsAsync(primaryMeta, primarySession, ct).ConfigureAwait(false);
        var primaryRows = new List<IReadOnlyDictionary<string, object?>>();
        foreach (var split in primarySplits)
        {
            var batch = await primaryConnector.RecordSource.ReadBatchAsync(split, primarySession, ct).ConfigureAwait(false);
            primaryRows.AddRange(batch);
        }

        if (primaryRows.Count == 0 || !joinedDecision.IsAllowed)
        {
            // If primary has no rows or caller has no consent for joined table (SEC-CDJ-02), return primary rows with null relation
            var sanitizedPrimaryOnly = primaryRows.Select(r =>
            {
                var dict = new Dictionary<string, object?>(r, StringComparer.OrdinalIgnoreCase)
                {
                    [request.TargetRelationPropertyName] = null
                };
                return (IReadOnlyDictionary<string, object?>)dict;
            }).ToList();

            return new CrossDomainJoinResult(sanitizedPrimaryOnly, JoinedTableAccessAllowed: joinedDecision.IsAllowed, JoinedEntitiesMergedCount: 0);
        }

        // 5. Extract Distinct Foreign Key Values (Null-Pruning & Deduplication)
        var fkValues = primaryRows
            .Where(r => r.TryGetValue(request.ForeignKeyColumn, out var val) && val != null && !string.IsNullOrWhiteSpace(val.ToString()))
            .Select(r => r[request.ForeignKeyColumn]!)
            .Distinct()
            .ToList();

        if (fkValues.Count == 0)
        {
            var noFkRows = primaryRows.Select(r =>
            {
                var dict = new Dictionary<string, object?>(r, StringComparer.OrdinalIgnoreCase)
                {
                    [request.TargetRelationPropertyName] = null
                };
                return (IReadOnlyDictionary<string, object?>)dict;
            }).ToList();

            return new CrossDomainJoinResult(noFkRows, JoinedTableAccessAllowed: true, JoinedEntitiesMergedCount: 0);
        }

        // 6. Batched Dependent Fetch on Joined Connector (Parameterized, Zero-Injection SEC-CDJ-03)
        var joinedProjected = (request.JoinedProjectedColumns != null && request.JoinedProjectedColumns.Count > 0)
            ? request.JoinedProjectedColumns
            : joinedMeta.Columns.Select(c => c.ColumnName).ToList();

        if (!joinedProjected.Contains(request.PrimaryKeyColumn, StringComparer.OrdinalIgnoreCase))
        {
            joinedProjected = [.. joinedProjected, request.PrimaryKeyColumn];
        }

        var joinedArgs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["limit"] = Math.Max(fkValues.Count, 500)
        };

        var joinedSession = new ConnectorSessionContext(
            Principal: request.Principal,
            Tenant: request.Tenant,
            AccessDecision: joinedDecision,
            ProjectedColumns: joinedProjected,
            Arguments: joinedArgs,
            PushdownFilterSql: joinedDecision.CombinedRowFilterSql,
            Limit: Math.Max(fkValues.Count, 500),
            Offset: 0,
            RequestHeaders: request.RequestHeaders);

        joinedSession.Items["TableMetadata"] = joinedMeta;

        var joinedSplits = await joinedConnector.SplitManager.GetSplitsAsync(joinedMeta, joinedSession, ct).ConfigureAwait(false);
        var joinedRows = new List<IReadOnlyDictionary<string, object?>>();
        foreach (var split in joinedSplits)
        {
            var batch = await joinedConnector.RecordSource.ReadBatchAsync(split, joinedSession, ct).ConfigureAwait(false);
            joinedRows.AddRange(batch);
        }

        // 7. SEC-CDJ-01: Dual-Tenant Isolation & Hash Indexing
        var joinedIndex = new Dictionary<string, Dictionary<string, object?>>(StringComparer.OrdinalIgnoreCase);
        foreach (var jRow in joinedRows)
        {
            if (jRow.TryGetValue(request.PrimaryKeyColumn, out var pkVal) && pkVal != null)
            {
                // Verify tenant matches session tenant if present (SEC-CDJ-01)
                if (request.Tenant != null &&
                    jRow.TryGetValue("TenantId", out var rowTenantVal) &&
                    rowTenantVal != null &&
                    !string.Equals(rowTenantVal.ToString(), request.Tenant.Value, StringComparison.OrdinalIgnoreCase))
                {
                    // Drop cross-tenant row!
                    continue;
                }

                // Apply column masking to joined row
                var maskedJoined = ConnectorRowMasker.MaskRow(jRow, joinedMeta, joinedDecision, _maskingProvider);
                joinedIndex[pkVal.ToString()!] = maskedJoined;
            }
        }

        // 8. Hash-Join Stitching
        var mergedResult = new List<IReadOnlyDictionary<string, object?>>(primaryRows.Count);
        int mergedCount = 0;

        foreach (var pRow in primaryRows)
        {
            var dict = new Dictionary<string, object?>(pRow, StringComparer.OrdinalIgnoreCase);

            if (pRow.TryGetValue(request.ForeignKeyColumn, out var fkVal) &&
                fkVal != null &&
                joinedIndex.TryGetValue(fkVal.ToString()!, out var matchedJoinedRow))
            {
                dict[request.TargetRelationPropertyName] = matchedJoinedRow;
                mergedCount++;
            }
            else
            {
                dict[request.TargetRelationPropertyName] = null;
            }

            mergedResult.Add(dict);
        }

        return new CrossDomainJoinResult(mergedResult, JoinedTableAccessAllowed: true, JoinedEntitiesMergedCount: mergedCount);
    }
}
