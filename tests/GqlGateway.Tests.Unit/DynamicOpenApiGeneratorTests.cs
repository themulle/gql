namespace GqlGateway.Tests.Unit;

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GqlGateway.Application.Interfaces;
using GqlGateway.Application.OData.Interfaces;
using GqlGateway.Application.OData.Services;
using GqlGateway.Domain.Common;
using GqlGateway.Domain.Interfaces;
using GqlGateway.Domain.Model;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class DynamicOpenApiGeneratorTests
{
    private static List<TableMetadata> CreateSampleTables()
    {
        return new List<TableMetadata>
        {
            new TableMetadata
            {
                Identifier = new TableIdentifier("finance", "dbo", "invoices"),
                Table = new Table { SchemaName = "dbo", TableName = "invoices", DisplayName = "Monthly Invoices" },
                PrimaryKeyColumns = ["id"],
                Columns =
                [
                    new TableColumn { ColumnName = "id", DataType = "integer" },
                    new TableColumn { ColumnName = "customer_name", DataType = "varchar" },
                    new TableColumn { ColumnName = "amount", DataType = "decimal" },
                    new TableColumn { ColumnName = "iban", DataType = "varchar", IsSensitive = true },
                    new TableColumn { ColumnName = "invoice_date", DataType = "date" }
                ]
            },
            new TableMetadata
            {
                Identifier = new TableIdentifier("sales", "dbo", "leads"),
                Table = new Table { SchemaName = "dbo", TableName = "leads", DisplayName = "Sales Leads" },
                PrimaryKeyColumns = ["lead_id"],
                Columns =
                [
                    new TableColumn { ColumnName = "lead_id", DataType = "integer" },
                    new TableColumn { ColumnName = "company", DataType = "varchar" }
                ]
            }
        };
    }

    [Fact]
    public async Task GenerateOpenApiJsonAsync_WithAllTables_ProducesValidOpenApi31Spec()
    {
        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetAllTablesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<TableMetadata>>(CreateSampleTables()));

        var generator = new DynamicOpenApiGenerator(repo, NullLogger<DynamicOpenApiGenerator>.Instance);
        var json = await generator.GenerateOpenApiJsonAsync(null);

        json.ShouldNotBeNullOrWhiteSpace();

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("openapi").GetString().ShouldBe("3.1.0");
        root.GetProperty("info").GetProperty("title").GetString()!.ShouldContain("GqlGateway");

        var paths = root.GetProperty("paths");
        paths.TryGetProperty("/finance/dbo/invoices", out var invoicesPath).ShouldBeTrue();
        paths.TryGetProperty("/sales/dbo/leads", out var leadsPath).ShouldBeTrue();

        var getInvoices = invoicesPath.GetProperty("get");
        getInvoices.GetProperty("summary").GetString().ShouldBe("Query finance.invoices");

        var parameters = getInvoices.GetProperty("parameters");
        parameters.GetArrayLength().ShouldBe(5); // $select, $filter, $top, $skip, $count

        var schemas = root.GetProperty("components").GetProperty("schemas");
        schemas.TryGetProperty("finance_dbo_invoices", out var invoiceSchema).ShouldBeTrue();

        var props = invoiceSchema.GetProperty("properties");
        props.GetProperty("id").GetProperty("type").GetString().ShouldBe("integer");
        props.GetProperty("amount").GetProperty("type").GetString().ShouldBe("number");
        props.GetProperty("iban").GetProperty("x-sensitive").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task GenerateOpenApiJsonAsync_WithDomainScope_FiltersPathsOnlyToTargetDomain()
    {
        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetAllTablesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<TableMetadata>>(CreateSampleTables()));

        var generator = new DynamicOpenApiGenerator(repo, NullLogger<DynamicOpenApiGenerator>.Instance);
        var json = await generator.GenerateOpenApiJsonAsync("finance");

        using var doc = JsonDocument.Parse(json);
        var paths = doc.RootElement.GetProperty("paths");

        paths.TryGetProperty("/finance/dbo/invoices", out _).ShouldBeTrue();
        paths.TryGetProperty("/sales/dbo/leads", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task GenerateOpenApiYamlAsync_ProducesValidYamlStructure()
    {
        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetAllTablesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<TableMetadata>>(CreateSampleTables()));

        var generator = new DynamicOpenApiGenerator(repo, NullLogger<DynamicOpenApiGenerator>.Instance);
        var yaml = await generator.GenerateOpenApiYamlAsync(null);

        yaml.ShouldContain("openapi: \"3.1.0\"");
        yaml.ShouldContain("/finance/dbo/invoices:");
        yaml.ShouldContain("/sales/dbo/leads:");
        yaml.ShouldContain("components:");
    }

    [Fact]
    public async Task OpenApiCacheManager_CachesOutputAndInvalidatesCorrectly()
    {
        var cacheManager = new OpenApiCacheManager();
        int factoryInvocationCount = 0;

        Task<string> Factory(CancellationToken _)
        {
            Interlocked.Increment(ref factoryInvocationCount);
            return Task.FromResult("{\"openapi\": \"3.1.0\"}");
        }

        var bytes1 = await cacheManager.GetOrAddAsync("finance", isYaml: false, Factory);
        var bytes2 = await cacheManager.GetOrAddAsync("finance", isYaml: false, Factory);

        bytes1.ShouldBe(bytes2);
        factoryInvocationCount.ShouldBe(1);

        cacheManager.InvalidateCache();

        var bytes3 = await cacheManager.GetOrAddAsync("finance", isYaml: false, Factory);
        factoryInvocationCount.ShouldBe(2);
    }
}
