namespace GqlGateway.Api.Endpoints;

using System;
using System.Threading;
using System.Threading.Tasks;
using GqlGateway.Application.Interfaces;
using GqlGateway.Domain.Options;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;

public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app, GatewayOptions gatewayOptions, IHostEnvironment environment)
    {
        app.MapGet("/health/live", () =>
        {
            if (environment.IsDevelopment())
            {
                return Results.Ok(new
                {
                    status = "Live",
                    timestamp = DateTimeOffset.UtcNow,
                    securityMode = gatewayOptions.HasAnySecurityBypassActive ? "INSECURE_DEV_MODE" : "STRICT_ZERO_TRUST"
                });
            }

            return Results.Ok(new
            {
                status = "Live",
                timestamp = DateTimeOffset.UtcNow
            });
        }).AllowAnonymous();

        app.MapGet("/health/ready", async (
            ITrafficDrainController controller,
            IGatewayHealthCheckService? healthCheckService,
            CancellationToken ct) =>
        {
            if (controller.IsDraining)
            {
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }

            if (healthCheckService != null)
            {
                var report = await healthCheckService.CheckHealthAsync(ct).ConfigureAwait(false);
                if (!report.IsHealthy)
                {
                    if (environment.IsDevelopment())
                    {
                        return Results.Json(new
                        {
                            status = "Unhealthy",
                            timestamp = DateTimeOffset.UtcNow,
                            securityMode = gatewayOptions.HasAnySecurityBypassActive ? "INSECURE_DEV_MODE" : "STRICT_ZERO_TRUST",
                            activeBypasses = gatewayOptions.GetAllActiveBypasses(),
                            components = report.Components
                        }, statusCode: StatusCodes.Status503ServiceUnavailable);
                    }

                    return Results.Json(new
                    {
                        status = "Unhealthy",
                        timestamp = DateTimeOffset.UtcNow
                    }, statusCode: StatusCodes.Status503ServiceUnavailable);
                }
            }

            if (environment.IsDevelopment())
            {
                return Results.Ok(new
                {
                    status = "Ready",
                    timestamp = DateTimeOffset.UtcNow,
                    securityMode = gatewayOptions.HasAnySecurityBypassActive ? "INSECURE_DEV_MODE" : "STRICT_ZERO_TRUST",
                    activeBypasses = gatewayOptions.GetAllActiveBypasses()
                });
            }

            return Results.Ok(new
            {
                status = "Ready",
                timestamp = DateTimeOffset.UtcNow
            });
        }).AllowAnonymous();

        return app;
    }
}
