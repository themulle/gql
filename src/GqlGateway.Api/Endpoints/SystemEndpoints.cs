namespace GqlGateway.Api.Endpoints;

using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GqlGateway.Application.Observability;
using GqlGateway.Application.ResourceGroups;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

public static class SystemEndpoints
{
    public static IEndpointRouteBuilder MapSystemEndpoints(this IEndpointRouteBuilder app)
    {
        // F-API-07: Canonical System Metadata & Monitoring Schema ($system / __gateway)
        app.MapGet("/api/governance/system/metrics", async (
            HttpContext context,
            IGatewaySystemMetricsService metricsService,
            CancellationToken ct) =>
        {
            var isPrivileged = context.User.IsInRole("GovernanceAdmin") ||
                               context.User.IsInRole("ClusterAdmin") ||
                               context.User.IsInRole("CatalogReader");
            if (!isPrivileged)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var metrics = await metricsService.CollectSystemMetricsAsync(ct).ConfigureAwait(false);
            return Results.Ok(metrics);
        }).RequireAuthorization();

        app.MapGet("/api/governance/system/health", async (
            HttpContext context,
            IGatewaySystemMetricsService metricsService,
            CancellationToken ct) =>
        {
            var isPrivileged = context.User.IsInRole("GovernanceAdmin") ||
                               context.User.IsInRole("ClusterAdmin") ||
                               context.User.IsInRole("CatalogReader");
            if (!isPrivileged)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var metrics = await metricsService.CollectSystemMetricsAsync(ct).ConfigureAwait(false);
            var isAllHealthy = metrics.Components.All(c => c.Status == "Healthy");

            return Results.Json(new
            {
                status = isAllHealthy ? "Healthy" : "Degraded",
                version = metrics.Version,
                uptime = metrics.Uptime,
                components = metrics.Components
            }, statusCode: isAllHealthy ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
        }).RequireAuthorization();

        app.MapGet("/api/governance/system/resource-groups", (
            HttpContext context,
            IResourceGroupManager resourceGroupManager) =>
        {
            var isPrivileged = context.User.IsInRole("GovernanceAdmin") ||
                               context.User.IsInRole("ClusterAdmin") ||
                               context.User.IsInRole("CatalogReader");
            if (!isPrivileged)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var metrics = resourceGroupManager.GetMetrics();
            return Results.Ok(metrics);
        }).RequireAuthorization();

        return app;
    }
}
