namespace GqlGateway.Tests.Unit;

using System;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using GqlGateway.Api.Extensions;
using GqlGateway.Api.Middleware;
using GqlGateway.Api.Security;
using GqlGateway.Application.Governance;
using GqlGateway.Application.Services;
using GqlGateway.Domain.Common;
using GqlGateway.Domain.Options;
using GqlGateway.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

public class SecurityFindingsRemediationTests
{
    // =========================================================================
    // Finding 1: Cross-Tenant Bypass Prevention (TenantResolutionMiddleware + Auth Handlers)
    // =========================================================================

    [Fact]
    public async Task TenantResolution_AuthenticatedUser_WithMismatchedHeader_Returns403()
    {
        // Arrange
        RequestDelegate next = _ => Task.CompletedTask;
        var middleware = new TenantResolutionMiddleware(next);

        var context = new DefaultHttpContext();
        var claims = new[]
        {
            new Claim("tenant_id", "tenant-alpha"),
            new Claim(ClaimTypes.Name, "alice")
        };
        var identity = new ClaimsIdentity(claims, "Basic");
        context.User = new ClaimsPrincipal(identity);
        context.Request.Headers["X-Tenant-ID"] = "tenant-beta";
        context.Response.Body = new MemoryStream();

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        context.Response.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(context.Response.Body);
        var responseText = await reader.ReadToEndAsync();
        responseText.ShouldContain("CROSS_TENANT_ACCESS_FORBIDDEN");
    }

    [Fact]
    public async Task TenantResolution_AuthenticatedUser_WithoutTenantClaim_SpoofingHeader_Returns403()
    {
        // Arrange: User is authenticated but has NO tenant claim
        RequestDelegate next = _ => Task.CompletedTask;
        var middleware = new TenantResolutionMiddleware(next);

        var context = new DefaultHttpContext();
        var claims = new[]
        {
            new Claim(ClaimTypes.Name, "bob"),
            new Claim(ClaimTypes.Role, "StandardUser")
        };
        var identity = new ClaimsIdentity(claims, "TestScheme");
        context.User = new ClaimsPrincipal(identity);
        context.Request.Headers["X-Tenant-ID"] = "victim-tenant";
        context.Response.Body = new MemoryStream();

        // Act
        await middleware.InvokeAsync(context);

        // Assert: Access is forbidden because non-admin cannot specify arbitrary tenant without a claim
        context.Response.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(context.Response.Body);
        var responseText = await reader.ReadToEndAsync();
        responseText.ShouldContain("CROSS_TENANT_ACCESS_FORBIDDEN");
    }

    [Fact]
    public async Task TenantResolution_GatewayAdmin_CanSwitchTenantViaHeader()
    {
        // Arrange: User has GatewayAdmin role
        bool nextCalled = false;
        RequestDelegate next = _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };
        var middleware = new TenantResolutionMiddleware(next);

        var context = new DefaultHttpContext();
        var claims = new[]
        {
            new Claim(ClaimTypes.Name, "admin"),
            new Claim(ClaimTypes.Role, "GatewayAdmin"),
            new Claim("tenant_id", "admin-home-tenant")
        };
        var identity = new ClaimsIdentity(claims, "Basic");
        context.User = new ClaimsPrincipal(identity);
        context.Request.Headers["X-Tenant-ID"] = "target-tenant";

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        nextCalled.ShouldBeTrue();
        context.Items[TenantResolutionMiddleware.TenantIdItemKey].ShouldBe(new TenantId("target-tenant"));
    }

    [Fact]
    public async Task BasicAuthenticationHandler_EmitsTenantClaim()
    {
        // Arrange
        var options = new GatewayOptions
        {
            Authentication = new GqlGateway.Domain.Options.AuthenticationOptions
            {
                BasicAuth = new BasicAuthOptions
                {
                    Enabled = true,
                    Users =
                    [
                        new BasicAuthUserConfig
                        {
                            Username = "tenant_user",
                            Password = "secretPassword123",
                            TenantId = "tenant-finance"
                        }
                    ]
                }
            }
        };

        var mockEnv = Substitute.For<IWebHostEnvironment>();
        mockEnv.EnvironmentName.Returns("Development");

        var schemeOptionsMonitor = new TestOptionsMonitor<AuthenticationSchemeOptions>(new AuthenticationSchemeOptions());
        var handler = new BasicAuthenticationHandler(
            schemeOptionsMonitor,
            NullLoggerFactory.Instance,
            System.Text.Encodings.Web.UrlEncoder.Default,
            Options.Create(options),
            mockEnv);

        var context = new DefaultHttpContext();
        await handler.InitializeAsync(new AuthenticationScheme(BasicAuthenticationHandler.SchemeName, "Basic", typeof(BasicAuthenticationHandler)), context);

        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes("tenant_user:secretPassword123"));
        context.Request.Headers.Authorization = $"Basic {credentials}";

        // Act
        var result = await handler.AuthenticateAsync();

        // Assert
        result.Succeeded.ShouldBeTrue();
        result.Principal.ShouldNotBeNull();
        result.Principal.FindFirst("tenant_id")?.Value.ShouldBe("tenant-finance");
        result.Principal.FindFirst("tenant")?.Value.ShouldBe("tenant-finance");
        result.Principal.FindFirst("tid")?.Value.ShouldBe("tenant-finance");
    }

    // =========================================================================
    // Finding 2: Consistent Invariant Enforcement for TestAuth and Anonymous Access
    // =========================================================================

    [Fact]
    public void ValidateSecurityInvariants_InProduction_EnableTestAuthHandler_ThrowsValidationException()
    {
        // Arrange
        var mockEnv = Substitute.For<IHostEnvironment>();
        mockEnv.EnvironmentName.Returns("Production");

        var options = new GatewayOptions
        {
            Authentication = new GqlGateway.Domain.Options.AuthenticationOptions
            {
                EnableTestAuthHandler = true
            }
        };

        // Act & Assert
        var ex = Should.Throw<ValidationException>(() =>
            GatewayServiceCollectionExtensions.ValidateGatewayOptions(options, mockEnv));
        ex.Message.ShouldContain("EnableTestAuthHandler darf AUSSCHLIESSLICH in der Development-Umgebung true sein");
    }

    [Fact]
    public void ValidateSecurityInvariants_InProduction_EnableTestAuthHandler_EvenWithAnonymous_ThrowsValidationException()
    {
        // Arrange
        var mockEnv = Substitute.For<IHostEnvironment>();
        mockEnv.EnvironmentName.Returns("Production");

        var options = new GatewayOptions
        {
            Authentication = new GqlGateway.Domain.Options.AuthenticationOptions
            {
                EnableTestAuthHandler = true
            },
            Insecure = new InsecureGettingStartedOptions
            {
                danger_allow_anonymous_access = true
            }
        };

        // Act & Assert: Finding 2 remediation ensures it is unconditionally forbidden
        var ex = Should.Throw<ValidationException>(() =>
            GatewayServiceCollectionExtensions.ValidateGatewayOptions(options, mockEnv));
        ex.Message.ShouldContain("EnableTestAuthHandler darf AUSSCHLIESSLICH in der Development-Umgebung true sein");
    }

    [Fact]
    public void ValidateSecurityInvariants_InProduction_AnonymousAccess_ThrowsValidationException()
    {
        // Arrange
        var mockEnv = Substitute.For<IHostEnvironment>();
        mockEnv.EnvironmentName.Returns("Production");

        var options = new GatewayOptions
        {
            Insecure = new InsecureGettingStartedOptions
            {
                danger_allow_anonymous_access = true
            }
        };

        // Act & Assert
        var ex = Should.Throw<ValidationException>(() =>
            GatewayServiceCollectionExtensions.ValidateGatewayOptions(options, mockEnv));
        ex.Message.ShouldContain("danger_allow_anonymous_access darf AUSSCHLIESSLICH in der Development-Umgebung true sein");
    }

    // =========================================================================
    // Finding 3: Casbin sub_rule Injection Defense
    // =========================================================================

    [Fact]
    public void CasbinEnforcementService_AddPolicy_WithDangerousSubRule_ThrowsArgumentException()
    {
        // Arrange
        var service = new CasbinEnforcementService();
        var tenant = new TenantId("tenant-audit");

        // Act & Assert
        var ex = Should.Throw<ArgumentException>(() =>
            service.AddPolicy(tenant, "attacker", "table", "read", "System.IO.File.ReadAllText(\"/etc/passwd\")", "allow"));
        ex.Message.ShouldContain("Casbin sub_rule enthält nicht erlaubten Ausdruck");
    }

    [Fact]
    public void CasbinEnforcementService_AddPolicy_WithSafeSubRule_Succeeds()
    {
        // Arrange
        var service = new CasbinEnforcementService();
        var tenant = new TenantId("tenant-audit");

        // Act & Assert: Standard boolean expression succeeds
        Should.NotThrow(() =>
            service.AddPolicy(tenant, "safe_user", "table", "read", "true", "allow"));
    }

    // =========================================================================
    // Finding 4: Bounded Candidate Derivation in DefaultEnvironmentSecretProvider
    // =========================================================================

    [Fact]
    public void DefaultEnvironmentSecretProvider_ResolvesExactCandidatesWithoutCollisions()
    {
        // Arrange: Config has both a generic secret and the dedicated itsm webhook secret
        var configBuilder = new ConfigurationBuilder();
        configBuilder.AddInMemoryCollection(new[]
        {
            new System.Collections.Generic.KeyValuePair<string, string?>("ITSM_WEBHOOK_SECRET", "itsm-secret-12345"),
            new System.Collections.Generic.KeyValuePair<string, string?>("SOME_OTHER_SECRET", "other-999")
        });
        var configuration = configBuilder.Build();

        var mockEnv = Substitute.For<IHostEnvironment>();
        mockEnv.EnvironmentName.Returns("Production");

        var provider = new DefaultEnvironmentSecretProvider(configuration, mockEnv);

        // Act
        var secretBytes = provider.GetSecretBytes("itsm:webhook-token");

        // Assert
        Encoding.UTF8.GetString(secretBytes).ShouldBe("itsm-secret-12345");
    }

    [Fact]
    public void ValidateSecurityInvariants_InProduction_UntrustedCertificatesAllowed_ThrowsValidationException()
    {
        var mockEnv = Substitute.For<IHostEnvironment>();
        mockEnv.EnvironmentName.Returns("Production");

        var options = new GatewayOptions
        {
            Insecure = new InsecureGettingStartedOptions
            {
                danger_allow_untrusted_certificates = true
            }
        };

        var ex = Should.Throw<ValidationException>(() =>
            GatewayServiceCollectionExtensions.ValidateGatewayOptions(options, mockEnv));
        ex.Message.ShouldContain("DANGER:danger_allow_untrusted_certificates");
    }

    [Fact]
    public void ValidateSecurityInvariants_InProduction_DisableRateLimiting_ThrowsValidationException()
    {
        var mockEnv = Substitute.For<IHostEnvironment>();
        mockEnv.EnvironmentName.Returns("Production");

        var options = new GatewayOptions
        {
            Insecure = new InsecureGettingStartedOptions
            {
                warn_disable_rate_limiting = true
            }
        };

        var ex = Should.Throw<ValidationException>(() =>
            GatewayServiceCollectionExtensions.ValidateGatewayOptions(options, mockEnv));
        ex.Message.ShouldContain("WARN:warn_disable_rate_limiting");
    }

    [Fact]
    public void DeclarativeHttp_IsRestrictedIp_IdentifiesPrivateAndLoopbackIps()
    {
        DeclarativeHttpDataSourceExecutor.IsRestrictedIp(System.Net.IPAddress.Parse("127.0.0.1")).ShouldBeTrue();
        DeclarativeHttpDataSourceExecutor.IsRestrictedIp(System.Net.IPAddress.Parse("10.1.2.3")).ShouldBeTrue();
        DeclarativeHttpDataSourceExecutor.IsRestrictedIp(System.Net.IPAddress.Parse("172.16.5.6")).ShouldBeTrue();
        DeclarativeHttpDataSourceExecutor.IsRestrictedIp(System.Net.IPAddress.Parse("192.168.1.100")).ShouldBeTrue();
        DeclarativeHttpDataSourceExecutor.IsRestrictedIp(System.Net.IPAddress.Parse("169.254.169.254")).ShouldBeTrue();
        DeclarativeHttpDataSourceExecutor.IsRestrictedIp(System.Net.IPAddress.Parse("::1")).ShouldBeTrue();
        DeclarativeHttpDataSourceExecutor.IsRestrictedIp(System.Net.IPAddress.Parse("fc00::1")).ShouldBeTrue();

        // Public IPs should not be restricted
        DeclarativeHttpDataSourceExecutor.IsRestrictedIp(System.Net.IPAddress.Parse("8.8.8.8")).ShouldBeFalse();
        DeclarativeHttpDataSourceExecutor.IsRestrictedIp(System.Net.IPAddress.Parse("93.184.216.34")).ShouldBeFalse();
    }

    [Fact]
    public void DeclarativeHttp_IsForbiddenMetadataHost_IdentifiesMetadataHosts()
    {
        DeclarativeHttpDataSourceExecutor.IsForbiddenMetadataHost("metadata.google.internal").ShouldBeTrue();
        DeclarativeHttpDataSourceExecutor.IsForbiddenMetadataHost("sub.metadata.google.internal").ShouldBeTrue();
        DeclarativeHttpDataSourceExecutor.IsForbiddenMetadataHost("kubernetes.default.svc").ShouldBeTrue();
        DeclarativeHttpDataSourceExecutor.IsForbiddenMetadataHost("kubernetes.default.svc.cluster.local").ShouldBeTrue();

        DeclarativeHttpDataSourceExecutor.IsForbiddenMetadataHost("api.corp.com").ShouldBeFalse();
    }

    private sealed class TestOptionsMonitor<T>(T currentValue) : IOptionsMonitor<T>
    {
        public T CurrentValue => currentValue;
        public T Get(string? name) => currentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
