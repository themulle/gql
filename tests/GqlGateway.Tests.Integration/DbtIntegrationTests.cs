namespace GqlGateway.Tests.Integration;

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Shouldly;
using Xunit;

public class DbtIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    private const string SampleDbtManifest = """
    {
      "nodes": {
        "model.analytics.monthly_revenue": {
          "name": "monthly_revenue",
          "database": "corp_dw",
          "schema": "finance",
          "description": "Monthly revenue model",
          "config": { "materialized": "table" },
          "contract": { "enforced": true },
          "tags": ["finance", "reporting"],
          "meta": { "owner": "FinanceTeam" },
          "columns": {
            "account_id": {
              "name": "account_id",
              "data_type": "integer",
              "description": "Account identifier",
              "tags": [],
              "meta": {}
            },
            "contact_email": {
              "name": "contact_email",
              "data_type": "varchar",
              "description": "Finance contact email",
              "tags": ["pii"],
              "meta": { "pii": "true" }
            }
          },
          "depends_on": { "nodes": [] }
        }
      }
    }
    """;

    public DbtIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Gateway:Authentication:EnableTestAuthHandler", "true");
            builder.UseSetting("Gateway:GovernanceDb:Provider", "Sqlite");
            builder.UseSetting("Gateway:GovernanceDb:ConnectionString", "Data Source=:memory:;Mode=Memory;Cache=Shared");
        });
    }

    [Fact]
    public async Task DbtSyncEndpoint_RequiresAuthentication()
    {
        var client = _factory.CreateClient();
        var content = new StringContent(SampleDbtManifest, Encoding.UTF8, "application/json");

        var response = await client.PostAsync("/api/extensions/dbt/sync", content);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task DbtSyncEndpoint_WithAuthenticatedUser_IngestsManifestSuccessfully()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", "S-1-5-21-ADMIN-SID");
        client.DefaultRequestHeaders.Add("X-Test-Roles", "GovernanceAdmin");

        var content = new StringContent(SampleDbtManifest, Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/api/extensions/dbt/sync", content);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.ShouldContain("\"success\":true");
        body.ShouldContain("\"parsedModelsCount\":1");
        body.ShouldContain("\"generatedProposalsCount\":1");
    }

    [Fact]
    public async Task DbtExposuresEndpoint_ReturnsValidYamlExposures()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", "S-1-5-21-USER-1");

        var response = await client.GetAsync("/api/extensions/dbt/exposures");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("text/yaml");

        var yaml = await response.Content.ReadAsStringAsync();
        yaml.ShouldContain("version: 2");
        yaml.ShouldContain("exposures:");
    }
}
