using NetArchTest.Rules;
using Shouldly;
using Xunit;

namespace GqlGateway.Tests.Architecture;

public class ArchitectureTests
{
    private const string DomainNamespace = "GqlGateway.Domain";
    private const string ApplicationNamespace = "GqlGateway.Application";
    private const string InfrastructureNamespace = "GqlGateway.Infrastructure";
    private const string GraphQlNamespace = "GqlGateway.GraphQL";
    private const string ApiNamespace = "GqlGateway.Api";
    private const string ExtensionsNamespace = "GqlGateway.Extensions";

    [Fact]
    public void Domain_ShouldNotHaveDependencyOnOtherProjects()
    {
        var result = Types.InAssembly(typeof(GqlGateway.Domain.Common.Sid).Assembly)
            .ShouldNot()
            .HaveDependencyOnAny(ApplicationNamespace, InfrastructureNamespace, GraphQlNamespace, ApiNamespace)
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(
            $"Domain layer violates Clean Architecture dependencies: {string.Join(", ", result.FailingTypeNames ?? Array.Empty<string>())}");
    }

    [Fact]
    public void Application_ShouldNotHaveDependencyOnInfrastructureOrApi()
    {
        var result = Types.InAssembly(typeof(GqlGateway.Application.Services.ConsentResolutionService).Assembly)
            .ShouldNot()
            .HaveDependencyOnAny(InfrastructureNamespace, GraphQlNamespace, ApiNamespace)
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(
            $"Application layer violates Clean Architecture dependencies: {string.Join(", ", result.FailingTypeNames ?? Array.Empty<string>())}");
    }

    [Fact]
    public void Infrastructure_ShouldNotHaveDependencyOnApi()
    {
        var result = Types.InAssembly(typeof(GqlGateway.Infrastructure.Persistence.SqliteGovernanceRepository).Assembly)
            .ShouldNot()
            .HaveDependencyOnAny(ApiNamespace)
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(
            $"Infrastructure layer violates Clean Architecture dependencies: {string.Join(", ", result.FailingTypeNames ?? Array.Empty<string>())}");
    }

    [Fact]
    public void Application_ShouldNotHaveDependencyOnAspNetCore()
    {
        var result = Types.InAssembly(typeof(GqlGateway.Application.Services.ConsentResolutionService).Assembly)
            .ShouldNot()
            .HaveDependencyOn("Microsoft.AspNetCore")
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(
            $"Application layer has forbidden dependency on Microsoft.AspNetCore: {string.Join(", ", result.FailingTypeNames ?? Array.Empty<string>())}");
    }

    [Fact]
    public void Domain_ShouldNotHaveDependencyOnAspNetCore()
    {
        var result = Types.InAssembly(typeof(GqlGateway.Domain.Common.Sid).Assembly)
            .ShouldNot()
            .HaveDependencyOn("Microsoft.AspNetCore")
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(
            $"Domain layer has forbidden dependency on Microsoft.AspNetCore: {string.Join(", ", result.FailingTypeNames ?? Array.Empty<string>())}");
    }

    [Fact]
    public void CoreLayers_ShouldNotHaveDependencyOnExtensions()
    {
        // EXT-MOVE: only the composition root (Api) references GqlGateway.Extensions; Domain, Application,
        // Infrastructure and GraphQL only provide interfaces, orchestration and governance logic.
        var coreAssemblies = new[]
        {
            typeof(GqlGateway.Domain.Common.Sid).Assembly,
            typeof(GqlGateway.Application.Services.ConsentResolutionService).Assembly,
            typeof(GqlGateway.Infrastructure.Persistence.SqliteGovernanceRepository).Assembly,
            typeof(GqlGateway.GraphQL.Types.DataCatalogSyncPayload).Assembly
        };

        foreach (var assembly in coreAssemblies)
        {
            var result = Types.InAssembly(assembly)
                .ShouldNot()
                .HaveDependencyOn(ExtensionsNamespace)
                .GetResult();

            result.IsSuccessful.ShouldBeTrue(
                $"{assembly.GetName().Name} must not depend on GqlGateway.Extensions: {string.Join(", ", result.FailingTypeNames ?? Array.Empty<string>())}");
        }
    }

    [Fact]
    public void Extensions_ShouldNotHaveDependencyOnInfrastructureGraphQlOrApi()
    {
        var result = Types.InAssembly(typeof(GqlGateway.Extensions.ExtensionsServiceCollectionExtensions).Assembly)
            .ShouldNot()
            .HaveDependencyOnAny(InfrastructureNamespace, GraphQlNamespace, ApiNamespace)
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(
            $"GqlGateway.Extensions may only depend on Application/Domain: {string.Join(", ", result.FailingTypeNames ?? Array.Empty<string>())}");
    }

    [Fact]
    public void CoreLayers_ShouldNotContainForeignSystemClients()
    {
        // EXT-MOVE: HttpClient based connectors to foreign systems (catalogs, ITSM, lineage export, AI triage,
        // Backstage export, CDC sources) are implemented exclusively in GqlGateway.Extensions.
        var connectorInterfaces = new[]
        {
            typeof(GqlGateway.Application.DataCatalog.Interfaces.IDataCatalogClient),
            typeof(GqlGateway.Application.DataCatalog.Interfaces.IDataCatalogClientFactory),
            typeof(GqlGateway.Application.DataCatalog.Interfaces.IDataCatalogSyncService),
            typeof(GqlGateway.Application.Interfaces.IItsmWorkflowClient),
            typeof(GqlGateway.Application.Interfaces.IItsmWebhookHandler),
            typeof(GqlGateway.Application.Interfaces.IOpenLineageClient),
            typeof(GqlGateway.Application.Interfaces.IOpenJevClient),
            typeof(GqlGateway.Application.OpenMetadata.Interfaces.IOpenMetadataClient),
            typeof(GqlGateway.Application.Integrations.Backstage.IBackstageCatalogExportService),
            typeof(GqlGateway.Application.Streaming.Interfaces.IMssqlChangeTrackingPoller)
        };

        var coreAssemblies = new[]
        {
            typeof(GqlGateway.Domain.Common.Sid).Assembly,
            typeof(GqlGateway.Application.Services.ConsentResolutionService).Assembly,
            typeof(GqlGateway.Infrastructure.Persistence.SqliteGovernanceRepository).Assembly,
            typeof(GqlGateway.GraphQL.Types.DataCatalogSyncPayload).Assembly
        };

        var violations = coreAssemblies
            .SelectMany(a => a.GetTypes())
            .Where(t => t is { IsClass: true, IsAbstract: false } && connectorInterfaces.Any(i => i.IsAssignableFrom(t)))
            .Select(t => t.FullName)
            .ToList();

        violations.ShouldBeEmpty($"Foreign system connectors found in core layers: {string.Join(", ", violations)}");
    }
}
