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
}
