namespace GqlGateway.Application.Mcp.Services;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using GqlGateway.Application.Mcp.Interfaces;
using GqlGateway.Domain.Model;
using GqlGateway.Domain.Options;
using Microsoft.Extensions.Options;

/// <summary>
/// Thread-safe in-memory registry of MCP tools exposed to AI agents.
/// Pre-populates default tools from GatewayOptions.Mcp.AllowedOperations or built-in standard queries.
/// </summary>
public sealed class McpToolRegistry : IMcpToolRegistry
{
    private readonly ConcurrentDictionary<string, McpToolDefinition> _tools = new(StringComparer.OrdinalIgnoreCase);

    public McpToolRegistry(IOptions<GatewayOptions>? options = null)
    {
        var mcpOpts = options?.Value.Mcp;
        InitializeDefaultTools(mcpOpts?.AllowedOperations);
    }

    public void RegisterTool(McpToolDefinition tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        _tools[tool.Name] = tool;
    }

    public IReadOnlyList<McpToolDefinition> GetAvailableTools()
    {
        return _tools.Values.ToList();
    }

    public McpToolDefinition? FindTool(string toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName)) return null;
        return _tools.TryGetValue(toolName, out var tool) ? tool : null;
    }

    private void InitializeDefaultTools(IReadOnlyList<string>? allowedOperations)
    {
        // 1. Built-in tool: Query Customers
        RegisterTool(new McpToolDefinition(
            Name: "query_customers",
            Description: "Queries customer records with automatic PII masking and tenant isolation.",
            InputJsonSchema: """
            {
              "type": "object",
              "properties": {
                "customerId": { "type": "string", "description": "Optional customer identifier filter." },
                "limit": { "type": "integer", "description": "Maximum number of rows to return (default 50)." }
              }
            }
            """,
            TargetGraphQLOperation: "query GetCustomers($customerId: String, $limit: Int) { customers(customerId: $customerId, limit: $limit) { id name email iban createdDate } }"
        ));

        // 2. Built-in tool: Query Invoices
        RegisterTool(new McpToolDefinition(
            Name: "query_invoices",
            Description: "Queries enterprise financial invoices and billing lines with ABAC authorization.",
            InputJsonSchema: """
            {
              "type": "object",
              "properties": {
                "invoiceId": { "type": "string", "description": "Optional specific invoice ID." },
                "currency": { "type": "string", "description": "Filter by currency code (e.g. EUR, USD)." }
              }
            }
            """,
            TargetGraphQLOperation: "query GetInvoices($invoiceId: String, $currency: String) { invoices(invoiceId: $invoiceId, currency: $currency) { invoiceId amount currency status } }"
        ));

        // 3. Built-in tool: Query Data Catalog Metadata
        RegisterTool(new McpToolDefinition(
            Name: "query_data_catalog",
            Description: "Inspects enterprise data catalog assets, classifications, and data stewards.",
            InputJsonSchema: """
            {
              "type": "object",
              "properties": {
                "tableName": { "type": "string", "description": "Table or asset name to inspect." }
              }
            }
            """,
            TargetGraphQLOperation: "query GetCatalogMetadata($tableName: String) { catalogAssets(tableName: $tableName) { tableName sensitivity classification owner tags } }"
        ));

        // Register any explicitly declared operations
        if (allowedOperations != null)
        {
            foreach (var op in allowedOperations)
            {
                var cleanName = op.ToLowerInvariant().Replace(' ', '_');
                if (!_tools.ContainsKey(cleanName))
                {
                    RegisterTool(new McpToolDefinition(
                        Name: cleanName,
                        Description: $"Executes the curated enterprise GraphQL operation '{op}'.",
                        InputJsonSchema: """{"type":"object","properties":{}}""",
                        TargetGraphQLOperation: op
                    ));
                }
            }
        }
    }
}
