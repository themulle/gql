namespace GqlGateway.Tests.Unit;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using GqlGateway.GraphQL.Subscriptions;
using Shouldly;
using Xunit;

#pragma warning disable CS0618

public sealed class WebSocketAuthInterceptorTests
{
    [Fact]
    public void ExtractToken_WithBearerPrefix_ExtractsCleanToken()
    {
        var dict = new Dictionary<string, object?>
        {
            ["Authorization"] = "Bearer my-secret-jwt-token"
        };

        var token = WebSocketAuthInterceptor.ExtractToken(dict);

        token.ShouldBe("my-secret-jwt-token");
    }

    [Fact]
    public void ExtractToken_WithApiKeyHeader_ExtractsToken()
    {
        var dict = new Dictionary<string, object?>
        {
            ["x-api-key"] = "enterprise-api-key-99"
        };

        var token = WebSocketAuthInterceptor.ExtractToken(dict);

        token.ShouldBe("enterprise-api-key-99");
    }

    [Fact]
    public void ExtractToken_EmptyOrMissing_ReturnsNull()
    {
        var dict = new Dictionary<string, object?>
        {
            ["unrelated"] = "value"
        };

        var token = WebSocketAuthInterceptor.ExtractToken(dict);

        token.ShouldBeNull();
    }

#pragma warning disable CS0618
    [Fact]
    public void CreatePrincipalFromToken_PopulatesClaimsCorrectly()
    {
        var principal = WebSocketAuthInterceptor.CreatePrincipalFromToken("ALICE");

        principal.Identity.ShouldNotBeNull();
        principal.Identity.IsAuthenticated.ShouldBeTrue();
        principal.FindFirst(ClaimTypes.NameIdentifier)?.Value.ShouldBe("ALICE");
        principal.FindFirst(ClaimTypes.PrimarySid)?.Value.ShouldBe("S-1-5-21-ALICE");
        principal.FindFirst("tenant_id")?.Value.ShouldBe("default");
    }

    [Theory]
    [InlineData("S-1-5-32-544")] // Builtin Administrators
    [InlineData("S-1-5-32-545")] // Builtin Users
    [InlineData("S-1-5-21-123456789-500")] // Domain Administrator
    public void CreatePrincipalFromToken_WithPrivilegedSid_ThrowsSecurityException(string privilegedSid)
    {
        Should.Throw<System.Security.SecurityException>(() =>
            WebSocketAuthInterceptor.CreatePrincipalFromToken(privilegedSid));
    }
#pragma warning restore CS0618
}
