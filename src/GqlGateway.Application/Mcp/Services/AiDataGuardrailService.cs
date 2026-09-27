namespace GqlGateway.Application.Mcp.Services;

using System;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using GqlGateway.Application.Mcp.Interfaces;
using GqlGateway.Domain.Model;
using GqlGateway.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Guardrail engine enforcing zero-trust data protection, automated PII scrubbing,
/// token budgeting, and GDPR Article 9 redaction on data supplied to AI agents.
/// </summary>
public sealed class AiDataGuardrailService : IAiDataGuardrailService
{
    private readonly IMcpToolRegistry _toolRegistry;
    private readonly IOptions<GatewayOptions> _options;
    private readonly ILogger<AiDataGuardrailService> _logger;

    // High-performance compiled regexes for automated PII detection
    private static readonly Regex EmailRegex = new(
        @"\b([A-Za-z0-9._%+-])[A-Za-z0-9._%+-]*@([A-Za-z0-9.-]+\.[A-Za-z]{2,})\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex IbanRegex = new(
        @"\b[A-Z]{2}[0-9]{2}(?:[ ]?[0-9]{4}){3,7}(?:[ ]?[0-9]{1,4})?\b",
        RegexOptions.Compiled);

    private static readonly Regex GdprArt9Regex = new(
        @"""(healthCondition|diagnosis|medicalRecord|biometricData|geneticMarker|religiousAffiliation|politicalBelief)""\s*:\s*""([^""]+)""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public AiDataGuardrailService(
        IMcpToolRegistry toolRegistry,
        IOptions<GatewayOptions> options,
        ILogger<AiDataGuardrailService> logger)
    {
        _toolRegistry = toolRegistry ?? throw new ArgumentNullException(nameof(toolRegistry));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public ValueTask<McpToolCallResult> ExecuteToolWithGuardrailAsync(
        McpToolCallRequest request,
        McpSessionContext sessionContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(sessionContext);

        var tool = _toolRegistry.FindTool(request.ToolName);
        if (tool == null)
        {
            _logger.LogWarning("AI Agent attempted to call unauthorized or unknown MCP tool '{ToolName}'.", request.ToolName);
            return ValueTask.FromResult(new McpToolCallResult(
                IsSuccess: false,
                ContentJson: "{}",
                ErrorMessage: $"Tool '{request.ToolName}' is not registered or allowed."
            ));
        }

        // 1. Mock execution or data extraction based on tool name and tenant context
        string rawDataJson = GenerateRawToolResponse(tool, request.ArgumentsJson, sessionContext.TenantId);

        // 2. Automated PII & GDPR Art. 9 Scrubbing
        bool shouldMask = !_options.Value.IsMcpUnmaskedAllowed;
        bool wasMasked = false;
        string scrubbedJson = rawDataJson;

        if (shouldMask)
        {
            scrubbedJson = ScrubPiiAndSensitiveData(rawDataJson, out wasMasked);
        }
        else
        {
            _logger.LogWarning("SECURITY ALERT [WARN]: AI tool execution for tool '{ToolName}' is running unmasked (warn_allow_unmasked_ai_access is ACTIVE).", tool.Name);
        }

        // 3. Token-Budgeting & Context Window Safeguards
        int maxTokens = _options.Value.Mcp.MaxTokensPerCall > 0 ? _options.Value.Mcp.MaxTokensPerCall : 4096;
        int estimatedTokens = Math.Max(1, scrubbedJson.Length / 4);
        bool truncated = false;

        if (estimatedTokens > maxTokens)
        {
            _logger.LogWarning("Tool result for '{ToolName}' ({EstimatedTokens} tokens) exceeded max token budget ({MaxTokens}). Truncating result.",
                tool.Name, estimatedTokens, maxTokens);

            int maxChars = maxTokens * 4;
            if (scrubbedJson.Length > maxChars)
            {
                scrubbedJson = scrubbedJson[..maxChars] + " ... [TRUNCATED DUE TO MCP TOKEN BUDGET]";
                estimatedTokens = maxTokens;
                truncated = true;
            }
        }

        return ValueTask.FromResult(new McpToolCallResult(
            IsSuccess: true,
            ContentJson: scrubbedJson,
            EstimatedTokens: estimatedTokens,
            IsMasked: wasMasked,
            TruncatedDueToBudget: truncated
        ));
    }

    private static string ScrubPiiAndSensitiveData(string input, out bool wasModified)
    {
        wasModified = false;
        if (string.IsNullOrWhiteSpace(input)) return input;

        string result = input;

        // Mask emails: john.doe@example.com -> j***@example.com
        if (EmailRegex.IsMatch(result))
        {
            result = EmailRegex.Replace(result, "$1***@$2");
            wasModified = true;
        }

        // Mask IBANs: DE89 3704 ... 1234 -> **** **** **** 1234
        if (IbanRegex.IsMatch(result))
        {
            result = IbanRegex.Replace(result, "**** **** **** 1234");
            wasModified = true;
        }

        // GDPR Art. 9 Sensitive Categories -> [REDACTED-GDPR-ART9]
        if (GdprArt9Regex.IsMatch(result))
        {
            result = GdprArt9Regex.Replace(result, @"""$1"":""[REDACTED-GDPR-ART9]""");
            wasModified = true;
        }

        return result;
    }

    private static string GenerateRawToolResponse(McpToolDefinition tool, string argumentsJson, string tenantId)
    {
        // Produce structured JSON responses adhering to enterprise schemas and tenant boundaries
        return tool.Name.ToLowerInvariant() switch
        {
            "query_customers" => $$"""
            {
              "tenantId": "{{tenantId}}",
              "customers": [
                {
                  "id": "CUST-1001",
                  "name": "Erika Mustermann",
                  "email": "erika.mustermann@acme-corp.com",
                  "iban": "DE89 3704 0044 0532 0130 00",
                  "healthCondition": "Diabetes Type 2",
                  "tier": "Enterprise"
                },
                {
                  "id": "CUST-1002",
                  "name": "Max Mustermann",
                  "email": "max.mustermann@partner.org",
                  "iban": "DE12 5001 0517 0648 4898 90",
                  "tier": "Standard"
                }
              ]
            }
            """,

            "query_invoices" => $$"""
            {
              "tenantId": "{{tenantId}}",
              "invoices": [
                {
                  "invoiceId": "INV-2026-001",
                  "amount": 14500.00,
                  "currency": "EUR",
                  "status": "PAID"
                },
                {
                  "invoiceId": "INV-2026-002",
                  "amount": 3200.50,
                  "currency": "EUR",
                  "status": "PENDING"
                }
              ]
            }
            """,

            "query_data_catalog" => $$"""
            {
              "tenantId": "{{tenantId}}",
              "assets": [
                {
                  "tableName": "customers",
                  "classification": "CONFIDENTIAL",
                  "sensitivity": "HIGH",
                  "owner": "data-steward-sales@company.com",
                  "tags": ["PII", "GDPR.Article9", "Financial"]
                }
              ]
            }
            """,

            _ => $$"""
            {
              "tenantId": "{{tenantId}}",
              "tool": "{{tool.Name}}",
              "status": "COMPLETED",
              "data": { "operation": "{{tool.TargetGraphQLOperation}}" }
            }
            """
        };
    }
}
