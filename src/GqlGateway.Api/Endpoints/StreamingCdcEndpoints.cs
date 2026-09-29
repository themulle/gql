namespace GqlGateway.Api.Endpoints;

using System;
using System.IO;
using System.Threading.Tasks;
using GqlGateway.Application.Streaming.Interfaces;
using GqlGateway.Infrastructure.Streaming;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

public static class StreamingCdcEndpoints
{
    public static IEndpointRouteBuilder MapStreamingCdcEndpoints(this IEndpointRouteBuilder app)
    {
        // CDC & Realtime Streaming Ingestion Endpoint (P5)
        app.MapPost("/api/v1/cdc/events", async (
            HttpRequest request,
            ICdcEventIngestionService ingestionService,
            ILoggerFactory loggerFactory) =>
        {
            if (request.ContentLength > 10 * 1024 * 1024)
            {
                return Results.BadRequest(new { error = "CDC payload exceeds maximum allowed size (10 MB)." });
            }

            using var reader = new StreamReader(request.Body);
            var body = await reader.ReadToEndAsync(request.HttpContext.RequestAborted);
            if (string.IsNullOrWhiteSpace(body))
            {
                return Results.BadRequest(new { error = "Empty CDC payload" });
            }

            try
            {
                var user = request.HttpContext.User;
                var callerTenant = user.FindFirst("tenant_id")?.Value
                                  ?? user.FindFirst("tid")?.Value
                                  ?? user.FindFirst("tenant")?.Value;

                var isClusterAdmin = user.IsInRole("ClusterAdmin") || user.IsInRole("PlatformAdmin");

                var cdcEvent = DebeziumCdcParser.Parse(body);

                // SEC-4: Enforce strict fail-closed tenant isolation on ingested CDC events
                if (!isClusterAdmin)
                {
                    if (string.IsNullOrWhiteSpace(callerTenant))
                    {
                        return Results.StatusCode(StatusCodes.Status403Forbidden);
                    }

                    if (!string.IsNullOrWhiteSpace(cdcEvent.TenantId) &&
                        !string.Equals(cdcEvent.TenantId, callerTenant, StringComparison.OrdinalIgnoreCase))
                    {
                        return Results.StatusCode(StatusCodes.Status403Forbidden);
                    }

                    if (string.IsNullOrWhiteSpace(cdcEvent.TenantId))
                    {
                        cdcEvent = cdcEvent with { TenantId = callerTenant };
                    }
                }

                await ingestionService.PublishEventAsync(cdcEvent, request.HttpContext.RequestAborted);
                return Results.Accepted(value: new { status = "Ingested", eventId = cdcEvent.EventId });
            }
            catch (Exception ex)
            {
                var logger = loggerFactory.CreateLogger("GqlGateway.CdcEndpoint");
                logger.LogWarning(ex, "Failed to parse or ingest CDC event payload.");
                return Results.BadRequest(new { error = "Invalid CDC event format" });
            }
        }).RequireAuthorization();

        return app;
    }
}
