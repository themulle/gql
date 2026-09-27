namespace GqlGateway.Tests.Unit;

using System;
using System.Text.Json;
using System.Threading.Tasks;
using GqlGateway.Application.Mcp.Interfaces;
using GqlGateway.Application.Mcp.Services;
using GqlGateway.Domain.Model;
using GqlGateway.Domain.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

public sealed class McpServerTests
{
    [Fact]
    public void McpToolRegistry_ShouldContainDefaultTools()
    {
        // Arrange
        var registry = new McpToolRegistry();

        // Act
        var tools = registry.GetAvailableTools();
        var customerTool = registry.FindTool("query_customers");
        var invoiceTool = registry.FindTool("query_invoices");
        var catalogTool = registry.FindTool("query_data_catalog");

        // Assert
        tools.Count.ShouldBeGreaterThanOrEqualTo(3);
        customerTool.ShouldNotBeNull();
        customerTool.Description.ShouldContain("customer records");
        invoiceTool.ShouldNotBeNull();
        catalogTool.ShouldNotBeNull();
    }

    [Fact]
    public void McpToolRegistry_CustomToolRegistration_ShouldBeRetrievable()
    {
        // Arrange
        var registry = new McpToolRegistry();
        var custom = new McpToolDefinition(
            Name: "custom_analytics",
            Description: "Analyzes quarterly metrics",
            InputJsonSchema: """{"type":"object"}""",
            TargetGraphQLOperation: "query GetAnalytics { metrics { revenue } }"
        );

        // Act
        registry.RegisterTool(custom);
        var found = registry.FindTool("custom_analytics");

        // Assert
        found.ShouldNotBeNull();
        found.Name.ShouldBe("custom_analytics");
        found.TargetGraphQLOperation.ShouldContain("GetAnalytics");
    }

    [Fact]
    public async Task AiDataGuardrailService_ShouldMaskPiiAndGdprArt9ByDefault()
    {
        // Arrange
        var registry = new McpToolRegistry();
        var options = Options.Create(new GatewayOptions
        {
            Mcp = new McpOptions { Enabled = true, RequirePiiMasking = true }
        });
        var logger = NullLogger<AiDataGuardrailService>.Instance;
        var guardrail = new AiDataGuardrailService(registry, options, logger);

        var session = new McpSessionContext("sess-1", "agent-principal", "tenant-alpha", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var request = new McpToolCallRequest("query_customers", "{}");

        // Act
        var result = await guardrail.ExecuteToolWithGuardrailAsync(request, session);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.IsMasked.ShouldBeTrue();

        // Email masked: erika.mustermann@acme-corp.com -> e***@acme-corp.com
        result.ContentJson.ShouldNotContain("erika.mustermann@acme-corp.com");
        result.ContentJson.ShouldContain("***@acme-corp.com");

        // IBAN masked: DE89 3704 ... -> **** **** **** 1234
        result.ContentJson.ShouldNotContain("DE89 3704 0044 0532 0130 00");
        result.ContentJson.ShouldContain("**** **** **** 1234");

        // GDPR Art. 9: HealthCondition -> [REDACTED-GDPR-ART9]
        result.ContentJson.ShouldNotContain("Diabetes Type 2");
        result.ContentJson.ShouldContain("[REDACTED-GDPR-ART9]");
    }

    [Fact]
    public async Task AiDataGuardrailService_WhenInsecureUnmaskedAllowed_ShouldNotMaskPii()
    {
        // Arrange
        var registry = new McpToolRegistry();
        var options = Options.Create(new GatewayOptions
        {
            Mcp = new McpOptions { Enabled = true, warn_allow_unmasked_ai_access = true }
        });
        var logger = NullLogger<AiDataGuardrailService>.Instance;
        var guardrail = new AiDataGuardrailService(registry, options, logger);

        var session = new McpSessionContext("sess-2", "agent-principal", "tenant-alpha", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var request = new McpToolCallRequest("query_customers", "{}");

        // Act
        var result = await guardrail.ExecuteToolWithGuardrailAsync(request, session);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.IsMasked.ShouldBeFalse();
        result.ContentJson.ShouldContain("erika.mustermann@acme-corp.com");
        result.ContentJson.ShouldContain("Diabetes Type 2");
    }

    [Fact]
    public async Task AiDataGuardrailService_ShouldTruncateWhenExceedingTokenBudget()
    {
        // Arrange
        var registry = new McpToolRegistry();
        var options = Options.Create(new GatewayOptions
        {
            Mcp = new McpOptions { Enabled = true, MaxTokensPerCall = 30 } // Very small token budget
        });
        var logger = NullLogger<AiDataGuardrailService>.Instance;
        var guardrail = new AiDataGuardrailService(registry, options, logger);

        var session = new McpSessionContext("sess-3", "agent-principal", "tenant-alpha", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var request = new McpToolCallRequest("query_customers", "{}");

        // Act
        var result = await guardrail.ExecuteToolWithGuardrailAsync(request, session);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.TruncatedDueToBudget.ShouldBeTrue();
        result.ContentJson.ShouldContain("[TRUNCATED DUE TO MCP TOKEN BUDGET]");
    }

    [Fact]
    public async Task McpProtocolHandler_LifecycleAndJsonRpcWorkflow_ShouldSucceed()
    {
        // Arrange
        var registry = new McpToolRegistry();
        var options = Options.Create(new GatewayOptions { Mcp = new McpOptions { Enabled = true } });
        var guardrail = new AiDataGuardrailService(registry, options, NullLogger<AiDataGuardrailService>.Instance);
        var sessionStore = new McpSessionStore(NullLogger<McpSessionStore>.Instance);
        var handler = new McpProtocolHandler(sessionStore, registry, guardrail, NullLogger<McpProtocolHandler>.Instance);

        // 1. Session creation
        var session = handler.CreateSession("svc-ai-claude", "tenant-prod-1");
        session.SessionId.ShouldNotBeNullOrWhiteSpace();
        session.ServicePrincipalId.ShouldBe("svc-ai-claude");
        session.TenantId.ShouldBe("tenant-prod-1");

        // 2. JSON-RPC 'initialize'
        var initPayload = """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""";
        var initResponse = await handler.HandleMessageAsync(session.SessionId, initPayload);
        using var initDoc = JsonDocument.Parse(initResponse);
        initDoc.RootElement.GetProperty("result").GetProperty("protocolVersion").GetString().ShouldBe("2024-11-05");
        initDoc.RootElement.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString().ShouldBe("GqlGateway.McpServer");

        // 3. JSON-RPC 'ping'
        var pingPayload = """{"jsonrpc":"2.0","id":2,"method":"ping"}""";
        var pingResponse = await handler.HandleMessageAsync(session.SessionId, pingPayload);
        using var pingDoc = JsonDocument.Parse(pingResponse);
        pingDoc.RootElement.GetProperty("id").GetInt64().ShouldBe(2);

        // 4. JSON-RPC 'tools/list'
        var toolsListPayload = """{"jsonrpc":"2.0","id":3,"method":"tools/list"}""";
        var toolsListResponse = await handler.HandleMessageAsync(session.SessionId, toolsListPayload);
        using var toolsDoc = JsonDocument.Parse(toolsListResponse);
        var toolsArray = toolsDoc.RootElement.GetProperty("result").GetProperty("tools");
        toolsArray.GetArrayLength().ShouldBeGreaterThanOrEqualTo(3);

        // 5. JSON-RPC 'tools/call'
        var toolCallPayload = """{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"query_invoices","arguments":{"currency":"EUR"}}}""";
        var toolCallResponse = await handler.HandleMessageAsync(session.SessionId, toolCallPayload);
        using var toolDoc = JsonDocument.Parse(toolCallResponse);
        var content = toolDoc.RootElement.GetProperty("result").GetProperty("content");
        content.GetArrayLength().ShouldBe(1);
        var textContent = content[0].GetProperty("text").GetString();
        textContent.ShouldNotBeNull();
        textContent.ShouldContain("INV-2026-001");

        // 6. Session termination
        handler.RemoveSession(session.SessionId).ShouldBeTrue();
        handler.GetSession(session.SessionId).ShouldBeNull();

        // 7. Call with terminated session fails
        var expiredResponse = await handler.HandleMessageAsync(session.SessionId, pingPayload);
        using var expDoc = JsonDocument.Parse(expiredResponse);
        expDoc.RootElement.GetProperty("error").GetProperty("code").GetInt32().ShouldBe(-32000);
    }
}
