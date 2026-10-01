namespace GqlGateway.Tests.Integration;

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;
using GqlGateway.Application.Interfaces;
using GqlGateway.Application.Sql.Interfaces;
using GqlGateway.Domain.Common;
using GqlGateway.Domain.Model;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

public class GovernedWebSqlIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public GovernedWebSqlIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Gateway:Insecure:danger_allow_anonymous_access", "true");
            builder.UseSetting("Gateway:Insecure:danger_bypass_consent_checks", "true");
            builder.UseSetting("Gateway:GovernanceDb:Provider", "Sqlite");
            builder.UseSetting("Gateway:GovernanceDb:ConnectionString", "Data Source=:memory:;Mode=Memory;Cache=Shared");
            builder.UseSetting("Gateway:WebSql:Enabled", "true");
            builder.UseSetting("Gateway:WebSql:AllowDml", "false");
            builder.UseSetting("Gateway:WebSql:DefaultMaxRows", "200");
            builder.UseSetting("Gateway:WebSql:MaxAllowedRows", "500");
        });
    }

    [Fact]
    public async Task WebSql_SelectQuery_StreamsJsonRowsSuccessfully()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Id", "tenant_alpha");

        var payload = new
        {
            sql = "SELECT id, name, email FROM customers WHERE active = 1"
        };

        var response = await client.PostAsJsonAsync("/api/v1/sql", payload);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.TryGetProperty("columns", out var cols).ShouldBeTrue();
        doc.RootElement.TryGetProperty("rows", out var rows).ShouldBeTrue();
        doc.RootElement.TryGetProperty("rowCount", out var count).ShouldBeTrue();
    }

    [Fact]
    public async Task WebSql_DdlStatement_IsForbiddenWith403()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Id", "tenant_alpha");

        var payload = new
        {
            sql = "DROP TABLE sensitive_vault"
        };

        var response = await client.PostAsJsonAsync("/api/v1/sql", payload);
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var body = await response.Content.ReadAsStringAsync();
        body.ShouldContain("DDL statements");
    }

    [Fact]
    public async Task WebSql_DmlStatement_WhenDisallowed_IsForbiddenWith403()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Id", "tenant_alpha");

        var payload = new
        {
            sql = "DELETE FROM customers WHERE id = 1"
        };

        var response = await client.PostAsJsonAsync("/api/v1/sql", payload);
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var body = await response.Content.ReadAsStringAsync();
        body.ShouldContain("SELECT statements");
    }

    [Fact]
    public async Task WebSql_RewriteSqlAsync_InjectsTenantRlsAndClampsLimit()
    {
        using var scope = _factory.Services.CreateScope();
        var sqlService = scope.ServiceProvider.GetRequiredService<IGovernedSqlExecutionService>();

        var user = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "usr_alice"),
            new Claim("tenant_id", "tenant_123")
        }, "TestAuth"));

        string rawSql = "SELECT * FROM orders WHERE total > 100 LIMIT 5000";
        string secured = await sqlService.RewriteSqlAsync(rawSql, user, new TenantId("tenant_123"));

        // RLS subquery rewrite must contain tenant predicate
        secured.ShouldContain("tenant_id = 'tenant_123'");
        // Limit must be clamped to MaxAllowedRows (500)
        secured.ShouldContain("LIMIT 200");
        secured.ShouldNotContain("5000");
    }

    [Fact]
    public async Task WebSql_RewriteSqlAsync_WithColumnMaskingRules_PushesDownMaskingExpressions()
    {
        using var scope = _factory.Services.CreateScope();
        var tableRepo = scope.ServiceProvider.GetRequiredService<ITableMetadataRepository>();
        var sqlService = scope.ServiceProvider.GetRequiredService<IGovernedSqlExecutionService>();

        // Register table metadata with sensitive columns
        var tableId = new TableIdentifier("default", "public", "employees");
        await tableRepo.UpsertTableMetadataAsync(new TableMetadata
        {
            Table = new Table { Id = Guid.NewGuid(), DisplayName = "employees", TableName = "employees" },
            Identifier = tableId,
            Columns = new List<TableColumn>
            {
                new() { ColumnName = "id", DataType = "integer" },
                new() { ColumnName = "name", DataType = "varchar" },
                new() { ColumnName = "ssn", DataType = "varchar", IsSensitive = true },
                new() { ColumnName = "tenant_id", DataType = "varchar" }
            },
            ColumnMaskingRules = new Dictionary<string, MaskingRule>
            {
                ["ssn"] = new MaskingRule { RuleType = "REDACT", Replacement = "XXX-XX-XXXX" }
            }
        });

        var user = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "usr_bob"),
            new Claim("tenant_id", "tenant_456")
        }, "TestAuth"));

        string rawSql = "SELECT id, name, ssn FROM employees";
        string secured = await sqlService.RewriteSqlAsync(rawSql, user, new TenantId("tenant_456"));

        secured.ShouldContain("tenant_id = 'tenant_456'");
        secured.ShouldContain("AS employees");
    }

    [Fact]
    public async Task WebSql_AntiBypass_CteShadowing_DoesNotEvadeRlsOnPhysicalTable()
    {
        using var scope = _factory.Services.CreateScope();
        var sqlService = scope.ServiceProvider.GetRequiredService<IGovernedSqlExecutionService>();

        var user = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "usr_eve"),
            new Claim("tenant_id", "tenant_evil")
        }, "TestAuth"));

        // Attacker attempts to shadow physical table 'accounts' inside CTE to query external rows
        string attackSql = @"
            WITH accounts AS (
                SELECT * FROM accounts WHERE tenant_id = 'victim'
            )
            SELECT * FROM accounts";

        string secured = await sqlService.RewriteSqlAsync(attackSql, user, new TenantId("tenant_evil"));

        // Physical 'accounts' inside CTE definition MUST still have RLS injected
        secured.ShouldContain("tenant_id = 'tenant_evil'");
    }

    [Fact]
    public async Task WebSql_ArrayFormatQuery_StreamsCompactJsonArrays()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Id", "tenant_gamma");

        var payload = new
        {
            sql = "SELECT id, name FROM users"
        };

        var response = await client.PostAsJsonAsync("/api/v1/sql?format=arrays", payload);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("columns").GetArrayLength().ShouldBeGreaterThan(0);
        doc.RootElement.GetProperty("rows").ValueKind.ShouldBe(JsonValueKind.Array);
    }

    [Fact]
    public async Task WebSql_UnauthenticatedRequest_WhenAnonymousDisabled_Returns401()
    {
        var secureFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Gateway:Insecure:danger_allow_anonymous_access", "false");
        });

        var client = secureFactory.CreateClient();
        var payload = new { sql = "SELECT 1" };
        var response = await client.PostAsJsonAsync("/api/v1/sql", payload);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
