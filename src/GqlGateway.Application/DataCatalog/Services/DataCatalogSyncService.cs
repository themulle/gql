namespace GqlGateway.Application.DataCatalog.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GqlGateway.Application.DataCatalog.Interfaces;
using GqlGateway.Application.DataCatalog.Models;
using GqlGateway.Domain.Common;
using GqlGateway.Domain.Interfaces;
using GqlGateway.Domain.Model;
using GqlGateway.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public sealed class DataCatalogSyncService : IDataCatalogSyncService
{
    private readonly IDataCatalogClientFactory _clientFactory;
    private readonly ITableMetadataRepository _metadataRepo;
    private readonly IEpochValidationService _epochService;
    private readonly IOptions<GatewayOptions> _options;
    private readonly ILogger<DataCatalogSyncService> _logger;

    public DataCatalogSyncService(
        IDataCatalogClientFactory clientFactory,
        ITableMetadataRepository metadataRepo,
        IEpochValidationService epochService,
        IOptions<GatewayOptions> options,
        ILogger<DataCatalogSyncService> logger)
    {
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        _metadataRepo = metadataRepo ?? throw new ArgumentNullException(nameof(metadataRepo));
        _epochService = epochService ?? throw new ArgumentNullException(nameof(epochService));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<CatalogSyncResult> SyncCatalogAsync(bool dryRun = false, CancellationToken ct = default)
    {
        var client = _clientFactory.GetActiveClient();
        _logger.LogInformation("Starting data catalog sync from provider {Provider} (DryRun: {DryRun})...", client.ProviderType, dryRun);

        var tables = await client.GetTablesAsync(null, ct).ConfigureAwait(false);
        var affectedTables = new List<TableIdentifier>();
        var warnings = new List<string>();

        int syncedTables = 0;
        int syncedColumns = 0;
        int maskedColumns = 0;
        int art9Tables = 0;

        var catalogOpts = _options.Value.Catalog;

        foreach (var tableAsset in tables)
        {
            syncedTables++;
            affectedTables.Add(tableAsset.Identifier);

            var existing = await _metadataRepo.GetTableMetadataAsync(tableAsset.Identifier, ct).ConfigureAwait(false);

            bool isArt9 = tableAsset.Tags.Any(t => catalogOpts.GdprArticle9Tags.Contains(t, StringComparer.OrdinalIgnoreCase)) ||
                          tableAsset.Classifications.Any(c => catalogOpts.GdprArticle9Tags.Contains(c, StringComparer.OrdinalIgnoreCase));

            if (isArt9)
            {
                art9Tables++;
            }

            var maskingRules = new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase);
            var tableColumns = new List<TableColumn>();

            foreach (var col in tableAsset.Columns)
            {
                syncedColumns++;
                var matchedTag = col.Tags.FirstOrDefault(t => catalogOpts.TagToMaskingRuleMap.ContainsKey(t));
                var isSensitive = matchedTag != null || col.Tags.Any(t => catalogOpts.PiiTags.Contains(t, StringComparer.OrdinalIgnoreCase));

                // Ratchet: Never downgrade sensitive status if existing column is already sensitive
                var existingCol = existing?.Columns?.FirstOrDefault(c => string.Equals(c.ColumnName, col.ColumnName, StringComparison.OrdinalIgnoreCase));
                if (existingCol?.IsSensitive == true)
                {
                    isSensitive = true;
                }

                if (matchedTag != null && catalogOpts.TagToMaskingRuleMap.TryGetValue(matchedTag, out var ruleType))
                {
                    maskedColumns++;
                    maskingRules[col.ColumnName] = new MaskingRule
                    {
                        RuleType = ruleType
                    };
                }

                tableColumns.Add(new TableColumn
                {
                    ColumnName = col.ColumnName,
                    DataType = col.DataType,
                    IsSensitive = isSensitive,
                    Description = col.Description,
                    DocumentationSource = "DataCatalog"
                });
            }

            // Merge with existing masking rules so custom / manual rules are preserved
            if (existing?.ColumnMaskingRules != null)
            {
                foreach (var (colName, rule) in existing.ColumnMaskingRules)
                {
                    if (!maskingRules.ContainsKey(colName))
                    {
                        maskingRules[colName] = rule;
                    }
                }
            }

            // Ratchet: Sensitivity must not be downgraded from HIGH/RESTRICTED to NORMAL
            string finalSensitivity = isArt9 ? "HIGH" : (existing?.Table?.Sensitivity ?? "NORMAL");
            if (existing?.Table != null &&
                (string.Equals(existing.Table.Sensitivity, "HIGH", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(existing.Table.Sensitivity, "RESTRICTED", StringComparison.OrdinalIgnoreCase)) &&
                !isArt9)
            {
                finalSensitivity = existing.Table.Sensitivity;
                warnings.Add($"Table '{tableAsset.Identifier}': Retained existing high sensitivity '{existing.Table.Sensitivity}'.");
            }

            bool finalRequiresFourEyes = isArt9 || (existing?.Table?.RequiresFourEyes == true);
            var finalDataSourceType = existing?.Table?.DataSourceType ?? DataSourceType.Sql;

            var metadata = new TableMetadata
            {
                Identifier = tableAsset.Identifier,
                Table = new Table
                {
                    SourceName = tableAsset.Identifier.Domain,
                    SchemaName = tableAsset.Identifier.Schema,
                    TableName = tableAsset.Identifier.TableName,
                    DisplayName = tableAsset.DisplayName ?? tableAsset.Identifier.TableName,
                    Description = tableAsset.Description,
                    DocumentationSource = "DataCatalog",
                    DataSourceType = finalDataSourceType,
                    SourceType = tableAsset.SourceType,
                    Sensitivity = finalSensitivity,
                    RequiresFourEyes = finalRequiresFourEyes
                },
                Columns = tableColumns,
                ColumnMaskingRules = maskingRules
            };

            // SEC M-32: merge with the persisted state – security flags can only be tightened by a catalog sync.
            metadata = CatalogGovernanceRatchet.Merge(metadata, existing);

            if (!dryRun)
            {
                await _metadataRepo.UpsertTableMetadataAsync(metadata, ct).ConfigureAwait(false);
            }
        }

        if (!dryRun && affectedTables.Count > 0)
        {
            foreach (var affectedTable in affectedTables)
            {
                await _epochService.InvalidateEpochAsync(affectedTable, ct).ConfigureAwait(false);
            }
            _logger.LogInformation("Invalidated governance epochs after syncing {Count} tables from catalog.", affectedTables.Count);
        }

        _logger.LogInformation("Data catalog sync complete. Synced {Tables} tables, {Columns} columns ({Masked} masked).",
            syncedTables, syncedColumns, maskedColumns);

        return new CatalogSyncResult(
            SyncedTablesCount: syncedTables,
            SyncedColumnsCount: syncedColumns,
            MaskedColumnsCount: maskedColumns,
            Art9ProtectedTablesCount: art9Tables,
            AffectedTables: affectedTables,
            Warnings: warnings,
            Success: true
        );
    }

    public async Task<TableMetadata?> EnrichOrReferenceTableAsync(TableIdentifier table, CancellationToken ct = default)
    {
        var client = _clientFactory.GetActiveClient();
        var asset = await client.GetTableAsync(table, ct).ConfigureAwait(false);
        if (asset == null) return null;

        var existing = await _metadataRepo.GetTableMetadataAsync(table, ct).ConfigureAwait(false);
        return existing;
    }
}

/// <summary>
/// SEC M-32: Merge semantics for catalog / metadata mirror syncs. Governance-relevant flags may only be tightened:
/// four-eyes, sensitivity, column sensitivity and masking rules are never weakened, a deactivated table stays inactive,
/// and data source routing fields (DataSourceType, HttpEndpoint, PluginName, SourceType) unknown to catalogs are preserved.
/// Columns missing from the incoming snapshot keep their persisted protection.
/// </summary>
public static class CatalogGovernanceRatchet
{
    public static TableMetadata Merge(TableMetadata incoming, TableMetadata? existing)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        if (existing == null)
        {
            return incoming;
        }

        var inTable = incoming.Table;
        var exTable = existing.Table;
        var tableId = exTable.Id != Guid.Empty ? exTable.Id : inTable.Id;

        var table = new Table
        {
            Id = tableId,
            SourceType = !string.IsNullOrWhiteSpace(exTable.SourceType) ? exTable.SourceType : inTable.SourceType,
            SourceName = inTable.SourceName,
            SchemaName = inTable.SchemaName,
            TableName = inTable.TableName,
            DisplayName = !string.IsNullOrWhiteSpace(inTable.DisplayName) ? inTable.DisplayName : exTable.DisplayName,
            Description = inTable.Description ?? exTable.Description,
            LongDescription = inTable.LongDescription ?? exTable.LongDescription,
            DocumentationSource = inTable.DocumentationSource ?? exTable.DocumentationSource,
            Sensitivity = StricterSensitivity(exTable.Sensitivity, inTable.Sensitivity),
            RequiresFourEyes = exTable.RequiresFourEyes || inTable.RequiresFourEyes,
            IsActive = exTable.IsActive && inTable.IsActive,
            DataSourceType = exTable.DataSourceType,
            HttpEndpoint = exTable.HttpEndpoint,
            PluginName = exTable.PluginName
        };

        var existingColumns = new Dictionary<string, TableColumn>(StringComparer.OrdinalIgnoreCase);
        foreach (var col in existing.Columns)
        {
            existingColumns.TryAdd(col.ColumnName, col);
        }

        var mergedColumns = new List<TableColumn>(Math.Max(incoming.Columns.Count, existing.Columns.Count));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var col in incoming.Columns)
        {
            if (!seen.Add(col.ColumnName))
            {
                continue;
            }

            existingColumns.TryGetValue(col.ColumnName, out var exCol);
            mergedColumns.Add(new TableColumn
            {
                Id = exCol?.Id ?? col.Id,
                TableId = tableId,
                ColumnName = col.ColumnName,
                DataType = string.IsNullOrWhiteSpace(col.DataType) && exCol != null ? exCol.DataType : col.DataType,
                IsSensitive = col.IsSensitive || exCol?.IsSensitive == true,
                Description = col.Description ?? exCol?.Description,
                LongDescription = col.LongDescription ?? exCol?.LongDescription,
                DocumentationSource = col.DocumentationSource ?? exCol?.DocumentationSource,
                Meta = col.Meta.Count > 0 ? col.Meta : (exCol?.Meta ?? col.Meta)
            });
        }

        foreach (var exCol in existing.Columns)
        {
            if (seen.Add(exCol.ColumnName))
            {
                // Column vanished from the catalog snapshot: keep it (and its protection) unchanged.
                mergedColumns.Add(exCol);
            }
        }

        var mergedRules = new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase);
        foreach (var (colName, rule) in existing.ColumnMaskingRules)
        {
            mergedRules[colName] = rule;
        }

        foreach (var (colName, rule) in incoming.ColumnMaskingRules)
        {
            if (!mergedRules.TryGetValue(colName, out var current) || MaskingRuleStrength(rule) > MaskingRuleStrength(current))
            {
                mergedRules[colName] = rule;
            }
        }

        return new TableMetadata
        {
            Table = table,
            Identifier = incoming.Identifier,
            Columns = mergedColumns,
            ColumnMaskingRules = mergedRules,
            PrimaryKeyColumns = existing.PrimaryKeyColumns
        };
    }

    public static string StricterSensitivity(string? existing, string? incoming)
    {
        if (string.IsNullOrWhiteSpace(existing)) return string.IsNullOrWhiteSpace(incoming) ? "NORMAL" : incoming;
        if (string.IsNullOrWhiteSpace(incoming)) return existing;
        return SensitivityRank(incoming) > SensitivityRank(existing) ? incoming : existing;
    }

    private static int SensitivityRank(string sensitivity) => sensitivity.Trim().ToUpperInvariant() switch
    {
        "LOW" or "PUBLIC" => 0,
        "NORMAL" or "INTERNAL" => 1,
        "MEDIUM" => 2,
        "CONFIDENTIAL" => 3,
        "HIGH" => 4,
        "RESTRICTED" or "SECRET" => 5,
        _ => 4 // unknown classifications are treated conservatively
    };

    private static int MaskingRuleStrength(MaskingRule rule) => (rule.RuleType ?? string.Empty).Trim().ToUpperInvariant() switch
    {
        "NULLIFY" => 4,
        "REDACT" => 3,
        "HMAC" or "HMAC_SHA256" => 2,
        _ => 1
    };
}
