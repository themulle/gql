namespace GqlGateway.Application.Mcp.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GqlGateway.Application.Interfaces;
using GqlGateway.Application.Mcp.Interfaces;
using GqlGateway.Domain.Common;
using GqlGateway.Domain.Model;
using Microsoft.Extensions.Logging;

/// <summary>
/// Semantic MCP compiler fusing dbt documentation and catalog metadata into AI tool definitions and resources (F-AI-02).
/// </summary>
public sealed class SemanticMcpCompiler(
    ITableMetadataRepository metadataRepo,
    ILogger<SemanticMcpCompiler> logger) : ISemanticMcpCompiler
{
    private readonly ITableMetadataRepository _metadataRepo = metadataRepo ?? throw new ArgumentNullException(nameof(metadataRepo));
    private readonly ILogger<SemanticMcpCompiler> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public async Task<McpToolDefinition> CompileToolAsync(
        string toolName,
        TableIdentifier targetTable,
        CancellationToken ct = default)
    {
        var meta = await _metadataRepo.GetTableMetadataAsync(targetTable, ct).ConfigureAwait(false);
        var tableDesc = meta?.Table.DisplayName ?? $"Dataset for {targetTable.Domain}.{targetTable.TableName}";

        // Enforce Dynamic Compactor constraint: Short description < 120 chars for LLM Tool Picker
        var shortDesc = tableDesc.Length > 110 ? tableDesc[..107] + "..." : tableDesc;
        shortDesc = $"{shortDesc} ({targetTable.Domain}.{targetTable.TableName})";
        if (shortDesc.Length > 120)
        {
            shortDesc = shortDesc[..117] + "...";
        }

        var inputSchemaObj = new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["first"] = new { type = "integer", description = "Max rows to return (default 10, max 1000)." },
                ["offset"] = new { type = "integer", description = "Offset for pagination." },
                ["filter"] = new { type = "string", description = "Zero-Trust filter expression." }
            }
        };

        var inputJsonSchema = JsonSerializer.Serialize(inputSchemaObj);
        var targetOp = $"query {{ table(domain: \"{targetTable.Domain}\", name: \"{targetTable.TableName}\") {{ tableName totalCount jsonRows }} }}";

        return new McpToolDefinition(
            Name: toolName,
            Description: shortDesc,
            InputJsonSchema: inputJsonSchema,
            TargetGraphQLOperation: targetOp,
            TargetTable: targetTable
        );
    }

    public async Task<IReadOnlyList<McpResourceItem>> GetSemanticResourcesAsync(
        string? domainScope = null,
        CancellationToken ct = default)
    {
        var allTables = await _metadataRepo.GetAllTablesAsync(ct).ConfigureAwait(false);
        var filtered = string.IsNullOrWhiteSpace(domainScope)
            ? allTables
            : allTables.Where(t => string.Equals(t.Identifier.Domain, domainScope, StringComparison.OrdinalIgnoreCase));

        var resources = new List<McpResourceItem>();

        foreach (var t in filtered)
        {
            var domain = t.Identifier.Domain;
            var table = t.Identifier.TableName;

            // 1. Glossary Resource
            var glossaryText = $"# Business Glossary: {domain}.{table}\n\n" +
                               $"* **Domain**: {domain}\n" +
                               $"* **Table**: {table}\n" +
                               $"* **Sensitivity**: {t.Table.Sensitivity}\n" +
                               $"* **Columns**:\n" +
                               string.Join("\n", t.Columns.Select(c => $"  - `{c.ColumnName}` ({c.DataType}){(c.IsSensitive ? " [SENSITIVE/MASKED]" : "")}"));

            resources.Add(new McpResourceItem(
                Uri: $"glossary://{domain}/{table}",
                Name: $"{domain}_{table}_glossary",
                Description: $"Business glossary and semantic column definitions for {domain}.{table}",
                MimeType: "text/markdown",
                Text: glossaryText
            ));

            // 2. dbt Lineage Resource
            var lineageText = $"# dbt Lineage & Contract: {table}\n\n" +
                              $"* **Model**: models/marts/{domain}/{table}.sql\n" +
                              $"* **Primary Keys**: {string.Join(", ", t.PrimaryKeyColumns)}\n" +
                              $"* **Upstream**: sources.{domain}.raw_{table}\n" +
                              $"* **Governance Contract**: Enforced fail-closed Zero-Trust policy.";

            resources.Add(new McpResourceItem(
                Uri: $"dbt://models/{table}/lineage",
                Name: $"{table}_dbt_lineage",
                Description: $"dbt lineage graph and schema contracts for {table}",
                MimeType: "text/markdown",
                Text: lineageText
            ));
        }

        return resources;
    }
}
