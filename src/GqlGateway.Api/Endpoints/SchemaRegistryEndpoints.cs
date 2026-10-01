namespace GqlGateway.Api.Endpoints;

using System.Security.Claims;
using System.Threading;
using GqlGateway.Application.SchemaRegistry;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

public static class SchemaRegistryEndpoints
{
    public static IEndpointRouteBuilder MapSchemaRegistryEndpoints(this IEndpointRouteBuilder app)
    {
        // Schema Registry & CI/CD Endpoints (P8)
        app.MapPost("/api/schema-registry/publish", async (
            HttpContext httpContext,
            SchemaRegistrationRequest request,
            ISchemaRegistryService registry,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            if (httpContext.Request.ContentLength > 10 * 1024 * 1024)
            {
                return Results.BadRequest(new { error = "Schema registration payload exceeds maximum allowed size (10 MB)." });
            }

            var canPublish = principal.IsInRole("GovernanceAdmin") ||
                             principal.IsInRole("SchemaAdmin") ||
                             principal.IsInRole("GatewayAdmin") ||
                             principal.IsInRole("PlatformAdmin") ||
                             principal.IsInRole("ClusterAdmin") ||
                             principal.IsInRole("Developer") ||
                             principal.IsInRole("DataOwner") ||
                             principal.HasClaim(c => (c.Type == "role" || c.Type == ClaimTypes.Role) &&
                                 (c.Value == "GovernanceAdmin" || c.Value == "SchemaAdmin" || c.Value == "GatewayAdmin" || c.Value == "PlatformAdmin" || c.Value == "ClusterAdmin" || c.Value == "Developer" || c.Value == "DataOwner"));

            if (!canPublish)
            {
                return Results.Json(new { error = "Publishing schemas requires Developer, SchemaAdmin, or ClusterAdmin privileges." }, statusCode: StatusCodes.Status403Forbidden);
            }

            if (request.ForceIfBreaking)
            {
                var isPrivileged = principal.IsInRole("GovernanceAdmin") ||
                                   principal.IsInRole("SchemaAdmin") ||
                                   principal.IsInRole("GatewayAdmin") ||
                                   principal.IsInRole("PlatformAdmin") ||
                                   principal.IsInRole("ClusterAdmin") ||
                                   principal.HasClaim(c => (c.Type == "role" || c.Type == ClaimTypes.Role) &&
                                       (c.Value == "GovernanceAdmin" || c.Value == "SchemaAdmin" || c.Value == "GatewayAdmin" || c.Value == "PlatformAdmin" || c.Value == "ClusterAdmin"));

                if (!isPrivileged)
                {
                    return Results.Json(new { error = "ForceIfBreaking requires administrative privileges (GovernanceAdmin, SchemaAdmin, or ClusterAdmin)." }, statusCode: StatusCodes.Status403Forbidden);
                }
            }

            var response = await registry.RegisterSchemaAsync(request, ct);
            return response.Success
                ? Results.Ok(response)
                : Results.BadRequest(response);
        }).RequireAuthorization();

        app.MapPost("/api/schema-registry/check", async (
            HttpContext httpContext,
            SchemaRegistrationRequest request,
            ISchemaRegistryService registry,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            if (httpContext.Request.ContentLength > 10 * 1024 * 1024)
            {
                return Results.BadRequest(new { error = "Schema check payload exceeds maximum allowed size (10 MB)." });
            }

            var canCheck = principal.IsInRole("GovernanceAdmin") ||
                           principal.IsInRole("SchemaAdmin") ||
                           principal.IsInRole("GatewayAdmin") ||
                           principal.IsInRole("PlatformAdmin") ||
                           principal.IsInRole("ClusterAdmin") ||
                           principal.IsInRole("Developer") ||
                           principal.IsInRole("DataOwner") ||
                           principal.HasClaim(c => (c.Type == "role" || c.Type == ClaimTypes.Role) &&
                               (c.Value == "GovernanceAdmin" || c.Value == "SchemaAdmin" || c.Value == "GatewayAdmin" || c.Value == "PlatformAdmin" || c.Value == "ClusterAdmin" || c.Value == "Developer" || c.Value == "DataOwner"));

            if (!canCheck)
            {
                return Results.Json(new { error = "Checking schemas requires Developer, SchemaAdmin, or ClusterAdmin privileges." }, statusCode: StatusCodes.Status403Forbidden);
            }

            var diff = await registry.CheckSchemaAsync(request.ServiceName, request.Sdl, ct);
            return Results.Ok(new
            {
                serviceName = request.ServiceName,
                isCompatible = diff.IsCompatible,
                hasBreakingChanges = diff.HasBreakingChanges,
                breakingCount = diff.BreakingCount,
                dangerousCount = diff.DangerousCount,
                safeCount = diff.SafeCount,
                changes = diff.Changes
            });
        }).RequireAuthorization();

        app.MapGet("/api/schema-registry/{service}/latest", async (
            string service,
            ISchemaRegistryService registry,
            CancellationToken ct) =>
        {
            var latest = await registry.GetLatestSchemaAsync(service, ct);
            return latest != null ? Results.Ok(latest) : Results.NotFound(new { error = $"No active schema found for service '{service}'." });
        }).RequireAuthorization();

        app.MapGet("/api/schema-registry/{service}/history", async (
            string service,
            ISchemaRegistryService registry,
            CancellationToken ct) =>
        {
            var history = await registry.GetSchemaHistoryAsync(service, ct);
            return Results.Ok(history);
        }).RequireAuthorization();

        app.MapGet("/api/schema-registry/services", async (
            ISchemaRegistryService registry,
            CancellationToken ct) =>
        {
            var services = await registry.GetAllServicesAsync(ct);
            return Results.Ok(services);
        }).RequireAuthorization();

        return app;
    }
}
