namespace GqlGateway.Application.Mcp.Interfaces;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GqlGateway.Domain.Common;
using GqlGateway.Domain.Model;

/// <summary>
/// Semantic MCP compiler fusing dbt documentation and catalog glossaries into AI tool definitions and resources (F-AI-02).
/// </summary>
public interface ISemanticMcpCompiler
{
    /// <summary>
    /// Compiles an MCP tool definition enriched with semantic contracts and compact descriptions.
    /// </summary>
    Task<McpToolDefinition> CompileToolAsync(
        string toolName,
        TableIdentifier targetTable,
        CancellationToken ct = default);

    /// <summary>
    /// Retrieves semantic resources (glossary://, dbt://) for on-demand LLM context grounding with optional user consent filtering.
    /// </summary>
    Task<IReadOnlyList<McpResourceItem>> GetSemanticResourcesAsync(
        string? domainScope = null,
        System.Security.Claims.ClaimsPrincipal? principal = null,
        CancellationToken ct = default);
}
