namespace GqlGateway.Api.Endpoints;

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using GqlGateway.Application.DataCatalog.Interfaces;
using GqlGateway.Application.Interfaces;
using GqlGateway.Application.OpenMetadata.Interfaces;
using GqlGateway.Domain.Options;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

public static class WebhookEndpoints
{
    public static IEndpointRouteBuilder MapWebhookEndpoints(this IEndpointRouteBuilder app, GatewayOptions gatewayOptions)
    {
        app.MapPost("/api/webhooks/openmetadata", async (
            HttpContext context,
            IOpenMetadataSyncService syncService,
            IOptions<GatewayOptions> options) =>
        {
            if (context.Request.ContentLength > 2 * 1024 * 1024)
            {
                return Results.BadRequest(new { error = "Payload size exceeds maximum allowed size (2 MB)." });
            }

            using var reader = new StreamReader(context.Request.Body);
            var payload = await reader.ReadToEndAsync(context.RequestAborted);

            string? signature = context.Request.Headers["X-OpenMetadata-Signature"].FirstOrDefault() ??
                                context.Request.Headers["X-OM-Signature"].FirstOrDefault();

            if (string.IsNullOrWhiteSpace(signature) && options.Value.IsWebhookSignatureBypassed)
            {
                signature = "bypassed";
            }

            var success = await syncService.HandleWebhookEventAsync(payload, signature, context.RequestAborted);
            if (!success)
            {
                return Results.BadRequest(new { error = "Failed to process webhook or invalid signature." });
            }

            return Results.Ok(new { status = "Processed" });
        }).AllowAnonymous();

        async Task<IResult> ProcessItsmWebhookAsync(
            HttpContext context,
            IItsmWebhookHandler webhookHandler,
            IOptions<GatewayOptions> options,
            string defaultSystemName)
        {
            if (context.Request.ContentLength > 2 * 1024 * 1024)
            {
                return Results.BadRequest(new { error = "Payload size exceeds maximum allowed size (2 MB)." });
            }

            using var reader = new StreamReader(context.Request.Body);
            var payload = await reader.ReadToEndAsync(context.RequestAborted);

            var opts = options.Value;
            string? signature = context.Request.Headers["X-ITSM-Signature"].FirstOrDefault()
                                ?? context.Request.Headers["X-ServiceNow-Signature"].FirstOrDefault()
                                ?? context.Request.Headers["X-Hub-Signature-256"].FirstOrDefault()
                                ?? context.Request.Headers["X-Hub-Signature"].FirstOrDefault();

            if (string.IsNullOrWhiteSpace(signature))
            {
                if (!opts.IsWebhookSignatureBypassed)
                {
                    return Results.Unauthorized();
                }
                signature = "bypassed";
            }

            DateTimeOffset timestamp;
            var tsHeader = context.Request.Headers["X-ITSM-Timestamp"].FirstOrDefault()
                           ?? context.Request.Headers["X-Timestamp"].FirstOrDefault()
                           ?? context.Request.Headers["Date"].FirstOrDefault();

            if (string.IsNullOrWhiteSpace(tsHeader) || !DateTimeOffset.TryParse(tsHeader, out timestamp))
            {
                if (!opts.IsWebhookTimestampToleranceIgnored)
                {
                    return Results.BadRequest(new { error = "Header X-ITSM-Timestamp is required and must be a valid ISO 8601 timestamp." });
                }
                timestamp = DateTimeOffset.UtcNow;
            }
            else if (!opts.IsWebhookTimestampToleranceIgnored)
            {
                var diff = (DateTimeOffset.UtcNow - timestamp).Duration();
                if (diff > TimeSpan.FromMinutes(5))
                {
                    return Results.Unauthorized();
                }
            }

            string? instanceHeader = context.Request.Headers["X-Instance-ID"].FirstOrDefault()
                                     ?? context.Request.Headers["X-ServiceNow-Instance"].FirstOrDefault()
                                     ?? context.Request.Headers["X-Jira-Instance"].FirstOrDefault()
                                     ?? context.Request.Query["instance"].FirstOrDefault();

            var success = await webhookHandler.HandleStatusChangeAsync(payload, signature, timestamp, instanceHeader, tsHeader, context.RequestAborted);
            if (!success)
            {
                return Results.Unauthorized();
            }

            return Results.Ok(new { status = "Processed", system = defaultSystemName });
        }

        // Canonical ITSM Webhook Endpoint
        app.MapPost("/api/webhooks/itsm/status-change", (
            HttpContext context,
            IItsmWebhookHandler webhookHandler,
            IOptions<GatewayOptions> opts) => ProcessItsmWebhookAsync(context, webhookHandler, opts, "ITSM")).AllowAnonymous();

        // Dedicated ServiceNow Webhook Endpoint
        app.MapPost("/api/webhooks/servicenow", (
            HttpContext context,
            IItsmWebhookHandler webhookHandler,
            IOptions<GatewayOptions> opts) => ProcessItsmWebhookAsync(context, webhookHandler, opts, "ServiceNow")).AllowAnonymous();

        // Dedicated Jira Webhook Endpoint
        app.MapPost("/api/webhooks/jira", (
            HttpContext context,
            IItsmWebhookHandler webhookHandler,
            IOptions<GatewayOptions> opts) => ProcessItsmWebhookAsync(context, webhookHandler, opts, "Jira")).AllowAnonymous();

        // Real-Time Data Catalog Webhook Endpoint
        app.MapPost("/api/webhooks/catalog", async (
            HttpContext context,
            IDataCatalogWebhookHandler webhookHandler,
            IOptions<GatewayOptions> options) =>
        {
            var opts = options.Value;
            if (context.Request.ContentLength > 10 * 1024 * 1024)
            {
                return Results.BadRequest(new { error = "Payload exceeds maximum allowed size (10 MB)." });
            }

            using var reader = new StreamReader(context.Request.Body);
            var payload = await reader.ReadToEndAsync(context.RequestAborted);

            // Azure EventGrid SubscriptionValidation handshake
            if (context.Request.Headers.TryGetValue("Aeg-Event-Type", out var eventType) &&
                string.Equals(eventType.FirstOrDefault(), "SubscriptionValidation", StringComparison.OrdinalIgnoreCase))
            {
                using var doc = JsonDocument.Parse(payload);
                string? validationCode = null;
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in doc.RootElement.EnumerateArray())
                    {
                        if (item.TryGetProperty("data", out var dataElem) &&
                            dataElem.TryGetProperty("validationCode", out var vcProp))
                        {
                            validationCode = vcProp.GetString();
                            break;
                        }
                    }
                }
                else if (doc.RootElement.TryGetProperty("data", out var dataElem) &&
                         dataElem.TryGetProperty("validationCode", out var vcProp))
                {
                    validationCode = vcProp.GetString();
                }

                return Results.Ok(new { validationResponse = validationCode ?? "" });
            }

            string? signature = null;
            if (context.Request.Headers.TryGetValue("X-Catalog-Signature", out var sigHeader) ||
                context.Request.Headers.TryGetValue("X-Signature", out sigHeader) ||
                context.Request.Headers.TryGetValue("X-Hub-Signature-256", out sigHeader))
            {
                signature = sigHeader.FirstOrDefault();
            }

            if (string.IsNullOrWhiteSpace(signature))
            {
                if (!opts.IsWebhookSignatureBypassed)
                {
                    return Results.Unauthorized();
                }
                signature = "bypassed";
            }

            DateTimeOffset? timestamp = null;
            if (context.Request.Headers.TryGetValue("X-Catalog-Timestamp", out var tsHeader) ||
                context.Request.Headers.TryGetValue("X-Timestamp", out tsHeader))
            {
                if (DateTimeOffset.TryParse(tsHeader.FirstOrDefault(), out var ts))
                {
                    timestamp = ts;
                }
            }

            if (!opts.IsWebhookTimestampToleranceIgnored)
            {
                if (!timestamp.HasValue)
                {
                    return Results.Json(new { error = "Missing required timestamp header (X-Catalog-Timestamp)." }, statusCode: StatusCodes.Status401Unauthorized);
                }

                if ((DateTimeOffset.UtcNow - timestamp.Value).Duration() > TimeSpan.FromMinutes(5))
                {
                    return Results.Json(new { error = "Webhook timestamp is outside the permitted 5-minute tolerance window." }, statusCode: StatusCodes.Status401Unauthorized);
                }
            }

            var provider = context.Request.Query.TryGetValue("provider", out var prov) ? prov.FirstOrDefault() : null;

            var result = await webhookHandler.HandleWebhookAsync(payload, signature, timestamp, provider, context.RequestAborted);
            if (!result.Success)
            {
                return Results.Json(result, statusCode: StatusCodes.Status401Unauthorized);
            }

            return Results.Ok(result);
        }).AllowAnonymous();

        app.MapPost("/api/v1/governance/catalog/webhook/{provider}", async (
            HttpContext context,
            string provider,
            IDataCatalogWebhookHandler webhookHandler,
            IOptions<GatewayOptions> options) =>
        {
            if (context.Request.ContentLength > 10 * 1024 * 1024)
            {
                return Results.BadRequest(new { error = "Payload exceeds maximum allowed size (10 MB)." });
            }

            using var reader = new StreamReader(context.Request.Body);
            var payload = await reader.ReadToEndAsync(context.RequestAborted);

            if (context.Request.Headers.TryGetValue("Aeg-Event-Type", out var eventType) &&
                string.Equals(eventType.FirstOrDefault(), "SubscriptionValidation", StringComparison.OrdinalIgnoreCase))
            {
                using var doc = JsonDocument.Parse(payload);
                string? validationCode = null;
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in doc.RootElement.EnumerateArray())
                    {
                        if (item.TryGetProperty("data", out var dataElem) &&
                            dataElem.TryGetProperty("validationCode", out var vcProp))
                        {
                            validationCode = vcProp.GetString();
                            break;
                        }
                    }
                }
                else if (doc.RootElement.TryGetProperty("data", out var dataElem) &&
                         dataElem.TryGetProperty("validationCode", out var vcProp))
                {
                    validationCode = vcProp.GetString();
                }

                return Results.Ok(new { validationResponse = validationCode ?? "" });
            }

            string? signature = null;
            if (context.Request.Headers.TryGetValue("X-Catalog-Signature", out var sigHeader) ||
                context.Request.Headers.TryGetValue("X-Signature", out sigHeader) ||
                context.Request.Headers.TryGetValue("X-Hub-Signature-256", out sigHeader))
            {
                signature = sigHeader.FirstOrDefault();
            }

            var opts = options.Value;
            if (string.IsNullOrWhiteSpace(signature))
            {
                if (!opts.IsWebhookSignatureBypassed)
                {
                    return Results.Unauthorized();
                }
                signature = "bypassed";
            }

            DateTimeOffset? timestamp = null;
            if (context.Request.Headers.TryGetValue("X-Catalog-Timestamp", out var tsHeader) ||
                context.Request.Headers.TryGetValue("X-Timestamp", out tsHeader))
            {
                if (DateTimeOffset.TryParse(tsHeader.FirstOrDefault(), out var ts))
                {
                    timestamp = ts;
                }
            }

            if (!opts.IsWebhookTimestampToleranceIgnored)
            {
                if (!timestamp.HasValue)
                {
                    return Results.Json(new { error = "Missing required timestamp header (X-Catalog-Timestamp)." }, statusCode: StatusCodes.Status401Unauthorized);
                }

                if ((DateTimeOffset.UtcNow - timestamp.Value).Duration() > TimeSpan.FromMinutes(5))
                {
                    return Results.Json(new { error = "Webhook timestamp is outside the permitted 5-minute tolerance window." }, statusCode: StatusCodes.Status401Unauthorized);
                }
            }

            var result = await webhookHandler.HandleWebhookAsync(payload, signature, timestamp, provider, context.RequestAborted);
            if (!result.Success)
            {
                return Results.Json(result, statusCode: StatusCodes.Status401Unauthorized);
            }

            return Results.Ok(result);
        }).AllowAnonymous();

        return app;
    }
}
