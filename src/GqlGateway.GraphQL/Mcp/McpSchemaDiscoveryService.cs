namespace GqlGateway.GraphQL.Mcp;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GqlGateway.Application.Mcp.Interfaces;
using GqlGateway.Domain.Model;
using HotChocolate;
using HotChocolate.Execution;
using HotChocolate.Types;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// Hosted background service discovering GraphQL schema fields annotated with @mcpTool
/// or standard registered query roots, and dynamically registering them in the IMcpToolRegistry.
/// </summary>
public sealed class McpSchemaDiscoveryService : IHostedService
{
    private readonly IRequestExecutorResolver _executorResolver;
    private readonly IMcpToolRegistry _toolRegistry;
    private readonly ILogger<McpSchemaDiscoveryService> _logger;

    public McpSchemaDiscoveryService(
        IRequestExecutorResolver executorResolver,
        IMcpToolRegistry toolRegistry,
        ILogger<McpSchemaDiscoveryService> logger)
    {
        _executorResolver = executorResolver ?? throw new ArgumentNullException(nameof(executorResolver));
        _toolRegistry = toolRegistry ?? throw new ArgumentNullException(nameof(toolRegistry));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var executor = await _executorResolver.GetRequestExecutorAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            var schema = executor.Schema;

            if (schema.QueryType is { } queryType)
            {
                int discoveredCount = 0;
                foreach (var field in queryType.Fields)
                {
                    var directive = field.Directives.FirstOrDefault(d => d.Type.Name.Equals("mcpTool", StringComparison.OrdinalIgnoreCase));
                    if (directive != null)
                    {
                        var toolName = directive.GetArgumentValue<string?>("name") ?? field.Name;
                        var description = directive.GetArgumentValue<string?>("description")
                            ?? field.Description
                            ?? $"Executes GraphQL query operation {field.Name}";

                        var inputSchema = BuildInputJsonSchema(field.Arguments);
                        var targetOp = BuildGraphQLOperation(field);

                        var toolDef = new McpToolDefinition(toolName, description, inputSchema, targetOp);
                        _toolRegistry.RegisterTool(toolDef);
                        discoveredCount++;

                        _logger.LogInformation("Discovered and registered MCP Tool '{ToolName}' from GraphQL field '{FieldName}'.",
                            toolName, field.Name);
                    }
                }

                _logger.LogInformation("MCP Schema Discovery completed: {Count} dynamic tools registered.", discoveredCount);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "McpSchemaDiscoveryService encountered an issue during startup schema reflection. Default tools remain active.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static string BuildInputJsonSchema(IReadOnlyCollection<IInputField> arguments)
    {
        var properties = new Dictionary<string, object>();
        var requiredList = new List<string>();

        foreach (var arg in arguments)
        {
            string jsonType = "string";
            IType innerType = arg.Type;
            if (innerType is NonNullType nonNull)
            {
                requiredList.Add(arg.Name);
                innerType = nonNull.Type;
            }

            var typeName = innerType.TypeName().ToString();
            if (typeName.Contains("Int", StringComparison.OrdinalIgnoreCase)) jsonType = "integer";
            else if (typeName.Contains("Float", StringComparison.OrdinalIgnoreCase)) jsonType = "number";
            else if (typeName.Contains("Boolean", StringComparison.OrdinalIgnoreCase)) jsonType = "boolean";

            properties[arg.Name] = new
            {
                type = jsonType,
                description = arg.Description ?? $"Parameter {arg.Name}"
            };
        }

        var schemaObj = new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = properties
        };

        if (requiredList.Count > 0)
        {
            schemaObj["required"] = requiredList;
        }

        return JsonSerializer.Serialize(schemaObj);
    }

    private static string BuildGraphQLOperation(IOutputField field)
    {
        var argDefs = new List<string>();
        var argUsages = new List<string>();

        foreach (var arg in field.Arguments)
        {
            argDefs.Add($"${arg.Name}: {arg.Type.TypeName()}");
            argUsages.Add($"{arg.Name}: ${arg.Name}");
        }

        string argDefString = argDefs.Count > 0 ? $"({string.Join(", ", argDefs)})" : "";
        string argUsageString = argUsages.Count > 0 ? $"({string.Join(", ", argUsages)})" : "";

        return $"query AutoGenerated_{field.Name}{argDefString} {{ {field.Name}{argUsageString} }}";
    }
}
