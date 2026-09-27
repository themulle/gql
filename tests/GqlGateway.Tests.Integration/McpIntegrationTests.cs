namespace GqlGateway.Tests.Integration;

using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Shouldly;
using Xunit;

public class McpIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public McpIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Gateway:Mcp:Enabled", "true");
            builder.UseSetting("Gateway:Mcp:EndpointPath", "/mcp");
            builder.UseSetting("Gateway:Insecure:danger_bypass_mcp_auth", "true");
            builder.UseSetting("Gateway:Insecure:danger_allow_anonymous_access", "true");
            builder.UseSetting("Gateway:GovernanceDb:Provider", "Sqlite");
            builder.UseSetting("Gateway:GovernanceDb:ConnectionString", "Data Source=:memory:;Mode=Memory;Cache=Shared");
        });
    }

    [Fact]
    public async Task McpSseAndMessageFlow_ShouldWorkEndToEnd()
    {
        var client = _factory.CreateClient();

        // 1. Establish SSE Connection (GET /mcp/sse)
        using var sseRequest = new HttpRequestMessage(HttpMethod.Get, "/mcp/sse");
        sseRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var sseResponse = await client.SendAsync(sseRequest, HttpCompletionOption.ResponseHeadersRead);
        sseResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        sseResponse.Content.Headers.ContentType?.MediaType.ShouldBe("text/event-stream");

        using var stream = await sseResponse.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);

        // Read initial endpoint event
        var line1 = await reader.ReadLineAsync(); // "event: endpoint"
        var line2 = await reader.ReadLineAsync(); // "data: /mcp/message?sessionId=..."
        await reader.ReadLineAsync(); // empty line

        line1.ShouldBe("event: endpoint");
        line2.ShouldNotBeNull();
        line2.ShouldStartWith("data: /mcp/message?sessionId=");

        var messageUri = line2.Replace("data: ", "").Trim();
        var sessionId = messageUri.Substring(messageUri.IndexOf("sessionId=", StringComparison.Ordinal) + 10);
        sessionId.ShouldNotBeNullOrWhiteSpace();

        // 2. Send JSON-RPC 'initialize' to POST /mcp/message
        var initPayload = """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""";
        var initResp = await client.PostAsync(messageUri, new StringContent(initPayload, Encoding.UTF8, "application/json"));
        initResp.StatusCode.ShouldBe(HttpStatusCode.OK);

        var initJson = await initResp.Content.ReadAsStringAsync();
        using var initDoc = JsonDocument.Parse(initJson);
        initDoc.RootElement.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString().ShouldBe("GqlGateway.McpServer");

        // 3. Send JSON-RPC 'tools/call' for 'query_customers'
        var callPayload = """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"query_customers","arguments":{}}}""";
        var callResp = await client.PostAsync(messageUri, new StringContent(callPayload, Encoding.UTF8, "application/json"));
        callResp.StatusCode.ShouldBe(HttpStatusCode.OK);

        var callJson = await callResp.Content.ReadAsStringAsync();
        using var callDoc = JsonDocument.Parse(callJson);
        var content = callDoc.RootElement.GetProperty("result").GetProperty("content");
        content.GetArrayLength().ShouldBe(1);
        var text = content[0].GetProperty("text").GetString();
        text.ShouldNotBeNull();

        // Verify automated AI Guardrail masking in the response
        text.ShouldNotContain("erika.mustermann@acme-corp.com");
        text.ShouldContain("***@acme-corp.com");
        text.ShouldContain("[REDACTED-GDPR-ART9]");

        // 4. Teardown session
        var deleteResp = await client.DeleteAsync($"/mcp/session/{sessionId}");
        deleteResp.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }
}
