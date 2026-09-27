using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GqlGateway.Infrastructure.Diagnostics;
using Microsoft.AspNetCore.Mvc.Testing;
using Shouldly;
using Xunit;

namespace GqlGateway.Tests.Integration;

public class QueryDefenseStressTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public QueryDefenseStressTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Gateway:Authentication:EnableTestAuthHandler", "true");
            builder.UseSetting("Gateway:GovernanceDb:Provider", "Sqlite");
            builder.UseSetting("Gateway:GovernanceDb:ConnectionString", "Data Source=:memory:;Mode=Memory;Cache=Shared");
            builder.UseSetting("Gateway:GraphQL:MaxAllowedExecutionDepth", "6");
            builder.UseSetting("Gateway:GraphQL:MaxAllowedComplexity", "250");
        });
    }

    private HttpClient CreateClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("GraphQL-Preflight", "1");
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", "S-1-5-21-test-analyst");
        return client;
    }

    [Fact]
    public async Task QueryCostAnalyzer_SmallLimit_PassesComplexityBudget()
    {
        var client = CreateClient();

        // Query requesting first: 5 should have cost ~ 50 + child costs <= 250
        var payload = new
        {
            query = @"
                query GetInvoices {
                    finance {
                        invoicesWithItems(first: 5) {
                            id
                            amount
                            vendor
                            email
                        }
                    }
                }"
        };

        var response = await client.PostAsJsonAsync("/graphql", payload);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.ShouldNotContain("QUERY_TOO_COMPLEX");
    }

    [Fact]
    public async Task QueryCostAnalyzer_ExcessiveLimit_FailsWithQueryTooComplex()
    {
        var client = CreateClient();

        // Query requesting first: 5000 should exceed maximum allowed cost (250)
        var payload = new
        {
            query = @"
                query GetMassiveInvoices {
                    finance {
                        invoicesWithItems(first: 5000) {
                            id
                            amount
                            vendor
                            email
                        }
                    }
                }"
        };

        var response = await client.PostAsJsonAsync("/graphql", payload);
        // Hot Chocolate validation errors return HTTP 400 Bad Request
        var content = await response.Content.ReadAsStringAsync();
        content.ShouldContain("QUERY_TOO_COMPLEX");
    }

    [Fact]
    public async Task QueryCostAnalyzer_OmittedLimit_DefaultsToWorstCaseBudgetAndFails()
    {
        var client = CreateClient();

        // Query with no first/last pagination argument defaults to MaxResponseRows worst case
        var payload = new
        {
            query = @"
                query GetAllInvoicesWithoutLimit {
                    finance {
                        invoicesWithItems {
                            id
                            amount
                            vendor
                            email
                        }
                    }
                }"
        };

        var response = await client.PostAsJsonAsync("/graphql", payload);
        var content = await response.Content.ReadAsStringAsync();
        content.ShouldContain("QUERY_TOO_COMPLEX");
    }

    [Fact]
    public async Task MaxExecutionDepth_ExceedingDepthLimit6_FailsOnAstValidation()
    {
        var client = CreateClient();

        // Query exceeding depth 6
        var payload = new
        {
            query = @"
                query DeepQuery {
                    finance {
                        invoicesWithItems(first: 1) {
                            items {
                                invoice {
                                    items {
                                        invoice {
                                            items {
                                                id
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }"
        };

        var response = await client.PostAsJsonAsync("/graphql", payload);
        var content = await response.Content.ReadAsStringAsync();
        content.ShouldContain("depth");
    }
}
