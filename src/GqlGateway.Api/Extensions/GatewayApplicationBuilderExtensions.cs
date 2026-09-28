using GqlGateway.Api.Middleware;
using GqlGateway.Application.Interfaces;
using GqlGateway.Application.OpenMetadata.Interfaces;
using GqlGateway.Application.DataCatalog.Interfaces;
using GqlGateway.Application.Dbt.Interfaces;
using GqlGateway.Application.Mcp.Interfaces;
using GqlGateway.Application.Governance.Interfaces;
using GqlGateway.Application.Streaming.Interfaces;
using GqlGateway.Infrastructure.Streaming;
using GqlGateway.Application.SchemaRegistry;
using GqlGateway.Extensions.OData;
using GqlGateway.Domain.Common;
using GqlGateway.Domain.Model;
using GqlGateway.Domain.Options;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Prometheus;

namespace GqlGateway.Api.Extensions;

public static class GatewayApplicationBuilderExtensions
{
    public static WebApplication UseGatewayPipeline(this WebApplication app, GatewayOptions gatewayOptions)
    {
        // Capture physical TCP remote IP before UseForwardedHeaders() overrides it with X-Forwarded-For
        app.Use(async (context, next) =>
        {
            if (context.Connection.RemoteIpAddress != null)
            {
                context.Items["OriginalTcpRemoteIp"] = context.Connection.RemoteIpAddress;
            }
            await next();
        });

        app.UseForwardedHeaders();
        app.UseCors();

        // HTTP Security Response Headers (MED-01)
        app.Use(async (context, next) =>
        {
            context.Response.Headers.Append("X-Content-Type-Options", "nosniff");
            context.Response.Headers.Append("X-Frame-Options", "DENY");
            context.Response.Headers.Append("Referrer-Policy", "strict-origin-when-cross-origin");
            context.Response.Headers.Append("Permissions-Policy", "camera=(), microphone=(), geolocation=()");
            context.Response.Headers.Append("Content-Security-Policy", "default-src 'self'; frame-ancestors 'none'; object-src 'none'; base-uri 'self';");
            await next();
        });

        if (!app.Environment.IsDevelopment())
        {
            app.UseHsts();
            app.UseHttpsRedirection();
        }

        if (app.Environment.IsDevelopment() && gatewayOptions.HasAnySecurityBypassActive)
        {
            app.Use(async (context, next) =>
            {
                context.Response.Headers.Append("X-Gateway-Insecure-Mode", string.Join("; ", gatewayOptions.GetAllActiveBypasses()));
                await next();
            });
        }

        var endpoint = gatewayOptions.GraphQL.EndpointPath.StartsWith('/')
            ? gatewayOptions.GraphQL.EndpointPath
            : "/" + gatewayOptions.GraphQL.EndpointPath;

        // Anti-CSRF Middleware: Enforce custom preflight header on GraphQL POST and GET query requests
        app.Use(async (context, next) =>
        {
            if (gatewayOptions.IsAllCorsAllowed)
            {
                if (context.Request.Headers.TryGetValue("Origin", out var originVal))
                {
                    context.Response.Headers.AccessControlAllowOrigin = originVal;
                    context.Response.Headers.AccessControlAllowMethods = "GET, POST, OPTIONS";
                    context.Response.Headers.AccessControlAllowHeaders = "*";
                }

                if (HttpMethods.IsOptions(context.Request.Method))
                {
                    context.Response.StatusCode = StatusCodes.Status200OK;
                    return;
                }

                await next();
                return;
            }

            if ((HttpMethods.IsPost(context.Request.Method) ||
                 (HttpMethods.IsGet(context.Request.Method) && context.Request.Query.ContainsKey("query")))
                && context.Request.Path.StartsWithSegments(endpoint))
            {
                bool hasPreflightHeader = context.Request.Headers.ContainsKey("GraphQL-Preflight") ||
                                          context.Request.Headers.ContainsKey("X-Requested-With");

                if (!hasPreflightHeader)
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    await context.Response.WriteAsJsonAsync(new
                    {
                        error = "CSRF Protection: Missing custom preflight header ('GraphQL-Preflight: 1' or 'X-Requested-With')."
                    });
                    return;
                }

                // Origin / Referer validation
                string? originHeader = context.Request.Headers.Origin.FirstOrDefault();
                if (string.IsNullOrWhiteSpace(originHeader))
                {
                    var refererHeader = context.Request.Headers.Referer.FirstOrDefault();
                    if (!string.IsNullOrWhiteSpace(refererHeader) && Uri.TryCreate(refererHeader, UriKind.Absolute, out var refUri))
                    {
                        originHeader = $"{refUri.Scheme}://{refUri.Authority}";
                    }
                }

                if (!string.IsNullOrWhiteSpace(originHeader))
                {
                    bool isOriginTrusted = false;
                    if (Uri.TryCreate(originHeader, UriKind.Absolute, out var originUri))
                    {
                        if (string.Equals(originUri.Authority, context.Request.Host.Value, StringComparison.OrdinalIgnoreCase))
                        {
                            isOriginTrusted = true;
                        }
                        else
                        {
                            foreach (var trusted in gatewayOptions.GraphQL.TrustedOrigins)
                            {
                                if (string.Equals(trusted, "*", StringComparison.OrdinalIgnoreCase))
                                {
                                    if (app.Environment.IsDevelopment())
                                    {
                                        isOriginTrusted = true;
                                        break;
                                    }
                                    continue;
                                }
                                if (Uri.TryCreate(trusted, UriKind.Absolute, out var trustedUri))
                                {
                                    if (string.Equals(trustedUri.Authority, originUri.Authority, StringComparison.OrdinalIgnoreCase) &&
                                        string.Equals(trustedUri.Scheme, originUri.Scheme, StringComparison.OrdinalIgnoreCase))
                                    {
                                        isOriginTrusted = true;
                                        break;
                                    }
                                }
                                else if (string.Equals(trusted, originHeader, StringComparison.OrdinalIgnoreCase))
                                {
                                    isOriginTrusted = true;
                                    break;
                                }
                            }
                        }
                    }

                    if (!isOriginTrusted)
                    {
                        context.Response.StatusCode = StatusCodes.Status403Forbidden;
                        await context.Response.WriteAsJsonAsync(new
                        {
                            error = $"CSRF Protection: Request Origin '{originHeader}' is not trusted."
                        });
                        return;
                    }
                }
                else if (gatewayOptions.GraphQL.TrustedOrigins.Count > 0 && !app.Environment.IsDevelopment())
                {
                    // If browser-originating request omits Origin/Referer in production with trusted origins configured, reject
                    var secFetchSite = context.Request.Headers["Sec-Fetch-Site"].FirstOrDefault();
                    if (!string.IsNullOrEmpty(secFetchSite) && !string.Equals(secFetchSite, "none", StringComparison.OrdinalIgnoreCase))
                    {
                        context.Response.StatusCode = StatusCodes.Status403Forbidden;
                        await context.Response.WriteAsJsonAsync(new
                        {
                            error = "CSRF Protection: Origin or Referer header required for browser requests."
                        });
                        return;
                    }
                }
            }

            await next();
        });

        app.UseHttpMetrics();
        app.UseMiddleware<PreAuthIpRateLimitingMiddleware>();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseMiddleware<TenantResolutionMiddleware>();
        app.UseMiddleware<OpenTelemetryTracingMiddleware>();
        app.UseMiddleware<PostAuthSidRateLimitingMiddleware>();
        app.UseMiddleware<GatewayExtensibilityMiddleware>();

        return app;
    }

    public static WebApplication MapGatewayEndpoints(this WebApplication app, GatewayOptions gatewayOptions)
    {
        app.MapMetrics();
        app.MapGet("/health/live", () =>
        {
            if (app.Environment.IsDevelopment())
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
                    if (app.Environment.IsDevelopment())
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

            if (app.Environment.IsDevelopment())
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

        var endpoint = gatewayOptions.GraphQL.EndpointPath.StartsWith('/')
            ? gatewayOptions.GraphQL.EndpointPath
            : "/" + gatewayOptions.GraphQL.EndpointPath;

        var pluginManager = app.Services.GetService<GqlGateway.Application.Plugins.IPluginManager>();
        if (pluginManager != null && !string.IsNullOrWhiteSpace(gatewayOptions.Plugins.Directory))
        {
            pluginManager.LoadPluginsFromDirectory(gatewayOptions.Plugins.Directory);
        }

        app.UseWebSockets();
        app.MapGraphQL(endpoint).RequireAuthorization();

        app.MapGet("/api/auth/login", (ClaimsPrincipal principal) =>
        {
            var sid = principal.GetUserSid()?.Value;
            var name = principal.Identity?.Name ?? sid;
            var roles = principal.GetUserRoles().ToList();
            var groups = principal.GetGroupSids().Select(g => g.Value).ToList();

            return Results.Ok(new
            {
                authenticated = true,
                user = name,
                sid = sid,
                roles = roles,
                groups = groups,
                authenticationType = principal.Identity?.AuthenticationType ?? "Basic"
            });
        }).RequireAuthorization();

        app.MapPost("/api/auth/login", (ClaimsPrincipal principal) =>
        {
            var sid = principal.GetUserSid()?.Value;
            var name = principal.Identity?.Name ?? sid;
            var roles = principal.GetUserRoles().ToList();
            var groups = principal.GetGroupSids().Select(g => g.Value).ToList();

            return Results.Ok(new
            {
                authenticated = true,
                user = name,
                sid = sid,
                roles = roles,
                groups = groups,
                authenticationType = principal.Identity?.AuthenticationType ?? "Basic"
            });
        }).RequireAuthorization();

        app.MapPost("/api/webhooks/openmetadata", async (
            HttpContext context,
            IOpenMetadataSyncService syncService,
            IOptions<GatewayOptions> gatewayOptions) =>
        {
            if (context.Request.ContentLength > 2 * 1024 * 1024)
            {
                return Results.BadRequest(new { error = "Payload size exceeds maximum allowed size (2 MB)." });
            }

            using var reader = new StreamReader(context.Request.Body);
            var payload = await reader.ReadToEndAsync(context.RequestAborted);

            string? signature = context.Request.Headers["X-OpenMetadata-Signature"].FirstOrDefault() ??
                                context.Request.Headers["X-OM-Signature"].FirstOrDefault();

            if (string.IsNullOrWhiteSpace(signature) && gatewayOptions.Value.IsWebhookSignatureBypassed)
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
            IOptions<GatewayOptions> gatewayOptions,
            string defaultSystemName)
        {
            if (context.Request.ContentLength > 2 * 1024 * 1024)
            {
                return Results.BadRequest(new { error = "Payload size exceeds maximum allowed size (2 MB)." });
            }

            using var reader = new StreamReader(context.Request.Body);
            var payload = await reader.ReadToEndAsync(context.RequestAborted);

            var opts = gatewayOptions.Value;
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

            string? instanceHeader = context.Request.Headers["X-Instance-ID"].FirstOrDefault()
                                     ?? context.Request.Headers["X-ServiceNow-Instance"].FirstOrDefault()
                                     ?? context.Request.Headers["X-Jira-Instance"].FirstOrDefault()
                                     ?? context.Request.Query["instance"].FirstOrDefault();

            var success = await webhookHandler.HandleStatusChangeAsync(payload, signature, timestamp, instanceHeader, context.RequestAborted);
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
            IOptions<GatewayOptions> gatewayOptions) => ProcessItsmWebhookAsync(context, webhookHandler, gatewayOptions, "ITSM")).AllowAnonymous();

        // Dedicated ServiceNow Webhook Endpoint (Business Rules / REST Messages / Flow Designer)
        app.MapPost("/api/webhooks/servicenow", (
            HttpContext context,
            IItsmWebhookHandler webhookHandler,
            IOptions<GatewayOptions> gatewayOptions) => ProcessItsmWebhookAsync(context, webhookHandler, gatewayOptions, "ServiceNow")).AllowAnonymous();

        // Dedicated Jira Webhook Endpoint (Jira Automation / Webhook Listeners)
        app.MapPost("/api/webhooks/jira", (
            HttpContext context,
            IItsmWebhookHandler webhookHandler,
            IOptions<GatewayOptions> gatewayOptions) => ProcessItsmWebhookAsync(context, webhookHandler, gatewayOptions, "Jira")).AllowAnonymous();

        // Real-Time Data Catalog Webhook Endpoint (OpenMetadata, Purview, Collibra, Alation)
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
                using var doc = System.Text.Json.JsonDocument.Parse(payload);
                string? validationCode = null;
                if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
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

            DateTimeOffset? timestamp = null;
            if (context.Request.Headers.TryGetValue("X-Catalog-Timestamp", out var tsHeader) ||
                context.Request.Headers.TryGetValue("X-Timestamp", out tsHeader))
            {
                if (DateTimeOffset.TryParse(tsHeader.FirstOrDefault(), out var ts))
                {
                    timestamp = ts;
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
                using var doc = System.Text.Json.JsonDocument.Parse(payload);
                string? validationCode = null;
                if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
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

            DateTimeOffset? timestamp = null;
            if (context.Request.Headers.TryGetValue("X-Catalog-Timestamp", out var tsHeader) ||
                context.Request.Headers.TryGetValue("X-Timestamp", out tsHeader))
            {
                if (DateTimeOffset.TryParse(tsHeader.FirstOrDefault(), out var ts))
                {
                    timestamp = ts;
                }
            }

            var result = await webhookHandler.HandleWebhookAsync(payload, signature, timestamp, provider, context.RequestAborted);
            if (!result.Success)
            {
                return Results.Json(result, statusCode: StatusCodes.Status401Unauthorized);
            }

            return Results.Ok(result);
        }).AllowAnonymous();

        // CDC & Realtime Streaming Ingestion Endpoint (P5)
        app.MapPost("/api/v1/cdc/events", async (
            HttpRequest request,
            ICdcEventIngestionService ingestionService,
            ILoggerFactory loggerFactory) =>
        {
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

                // SEC-4: Enforce strict tenant isolation on ingested CDC events to prevent cross-tenant event spoofing
                if (!string.IsNullOrWhiteSpace(callerTenant) && !isClusterAdmin)
                {
                    if (!string.IsNullOrWhiteSpace(cdcEvent.TenantId) &&
                        !string.Equals(cdcEvent.TenantId, callerTenant, StringComparison.OrdinalIgnoreCase))
                    {
                        return Results.Forbid();
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

        // dbt Ingestion & Exposure Endpoints (F-DATA-11)
        app.MapPost("/api/extensions/dbt/sync", async (
            HttpContext context,
            IDbtMetadataIngestionService dbtService) =>
        {
            if (context.Request.ContentLength > 100 * 1024 * 1024)
            {
                return Results.BadRequest(new { error = "Manifest size exceeds maximum allowed size (100 MB)." });
            }

            var dryRun = context.Request.Query.ContainsKey("dryRun") &&
                         bool.TryParse(context.Request.Query["dryRun"], out var dr) && dr;

            var result = await dbtService.IngestManifestStreamAsync(context.Request.Body, dryRun, context.RequestAborted);
            if (!result.Success)
            {
                return Results.BadRequest(result);
            }

            return Results.Ok(result);
        }).RequireAuthorization();

        app.MapGet("/api/extensions/dbt/exposures", async (
            IDbtExposurePublisher exposurePublisher,
            HttpContext context) =>
        {
            var yaml = await exposurePublisher.GenerateExposuresYamlAsync(context.RequestAborted);
            return Results.Content(yaml, "text/yaml; charset=utf-8");
        }).RequireAuthorization();

        app.MapGet("/api/extensions/dbt/proposals", async (
            IDbtMetadataIngestionService dbtService,
            HttpContext context) =>
        {
            TableIdentifier? table = null;
            if (context.Request.Query.TryGetValue("database", out var db) &&
                context.Request.Query.TryGetValue("schema", out var schema) &&
                context.Request.Query.TryGetValue("table", out var tableName))
            {
                table = new TableIdentifier(db!, schema!, tableName!);
            }

            var proposals = await dbtService.GetPendingProposalsAsync(table, context.RequestAborted);
            return Results.Ok(proposals);
        }).RequireAuthorization();

        app.MapPost("/api/extensions/dbt/proposals/{id:guid}/approve", async (
            Guid id,
            IDbtMetadataIngestionService dbtService,
            HttpContext context) =>
        {
            var isPrivileged = context.User.IsInRole("GovernanceAdmin") ||
                               context.User.IsInRole("ClusterAdmin") ||
                               context.User.IsInRole("DataOwner");
            if (!isPrivileged)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var user = context.User.Identity?.Name ?? context.User.GetUserSid()?.Value ?? "system_admin";
            try
            {
                var approved = await dbtService.ApproveProposalAsync(id, user, context.RequestAborted);
                return Results.Ok(approved);
            }
            catch (System.Collections.Generic.KeyNotFoundException)
            {
                return Results.NotFound(new { error = $"Proposal '{id}' not found." });
            }
        }).RequireAuthorization();

        app.MapPost("/api/extensions/dbt/proposals/{id:guid}/reject", async (
            Guid id,
            IDbtMetadataIngestionService dbtService,
            HttpContext context) =>
        {
            var isPrivileged = context.User.IsInRole("GovernanceAdmin") ||
                               context.User.IsInRole("ClusterAdmin") ||
                               context.User.IsInRole("DataOwner");
            if (!isPrivileged)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var user = context.User.Identity?.Name ?? context.User.GetUserSid()?.Value ?? "system_admin";
            try
            {
                var rejected = await dbtService.RejectProposalAsync(id, user, context.RequestAborted);
                return Results.Ok(rejected);
            }
            catch (System.Collections.Generic.KeyNotFoundException)
            {
                return Results.NotFound(new { error = $"Proposal '{id}' not found." });
            }
        }).RequireAuthorization();


        app.MapPost("/api/extensions/dbt/validate-contract", async (
            HttpContext context,
            IDbtContractValidator validator) =>
        {
            if (context.Request.ContentLength > 100 * 1024 * 1024)
            {
                return Results.BadRequest(new { error = "Manifest size exceeds maximum allowed size (100 MB)." });
            }

            var result = await validator.ValidateContractsStreamAsync(context.Request.Body, context.RequestAborted);
            return result.IsCompatible ? Results.Ok(result) : Results.UnprocessableEntity(result);
        }).RequireAuthorization();


        // P10: Multi-Tenant Policy Simulation Sandbox ("What-If" Replay via Audit Logs)
        app.MapPost("/api/governance/policy-simulation/replay", async (
            PolicySimulationRequest request,
            IPolicySimulationService simulationService,
            HttpContext context) =>
        {
            var result = await simulationService.SimulateAsync(request, context.RequestAborted);
            return Results.Ok(result);
        }).RequireAuthorization();


        // P11: Automated Schema Deprecation & Client-Impact Sunsetting (Smart Sunsetting Engine)
        app.MapGet("/api/governance/sunsetting/rules", async (
            ISchemaSunsettingService sunsettingService,
            HttpContext context) =>
        {
            var rules = await sunsettingService.GetRulesAsync(context.RequestAborted);
            return Results.Ok(rules);
        }).RequireAuthorization();

        app.MapPost("/api/governance/sunsetting/rules", async (
            FieldSunsettingRule rule,
            ISchemaSunsettingService sunsettingService,
            HttpContext context) =>
        {
            var isPrivileged = context.User.IsInRole("GovernanceAdmin") ||
                               context.User.IsInRole("SchemaAdmin") ||
                               context.User.IsInRole("ClusterAdmin");
            if (!isPrivileged)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            await sunsettingService.RegisterRuleAsync(rule, context.RequestAborted);
            return Results.Created($"/api/governance/sunsetting/rules/{rule.Id}", rule);
        }).RequireAuthorization();

        app.MapPost("/api/governance/sunsetting/evaluate", async (
            EvaluateFieldSunsettingRequest request,
            ISchemaSunsettingService sunsettingService,
            HttpContext context) =>
        {
            var evaluation = await sunsettingService.EvaluateFieldAsync(
                request.TargetTable,
                request.FieldName,
                request.EvaluationDate,
                context.RequestAborted);

            if (evaluation == null)
            {
                return Results.Ok(new { isDeprecated = false });
            }

            if (evaluation.IsHardSunsetBlocked)
            {
                context.Response.Headers["Sunset"] = evaluation.HttpSunsetHeader;
                return Results.Json(new
                {
                    error = evaluation.DeprecationNotice,
                    phase = evaluation.Phase.ToString(),
                    sunsetDate = evaluation.SunsetDate,
                    replacement = evaluation.Rule.ReplacementField
                }, statusCode: StatusCodes.Status410Gone);
            }

            if (evaluation.ShouldInjectSyntheticLatency && evaluation.SyntheticLatencyMs > 0)
            {
                await Task.Delay(evaluation.SyntheticLatencyMs, context.RequestAborted);
            }

            if (evaluation.ShouldRejectWith426)
            {
                context.Response.Headers["Sunset"] = evaluation.HttpSunsetHeader;
                return Results.Json(new
                {
                    error = "Chaos Testing: Upgrade Required. " + evaluation.DeprecationNotice,
                    phase = evaluation.Phase.ToString(),
                    sunsetDate = evaluation.SunsetDate,
                    replacement = evaluation.Rule.ReplacementField
                }, statusCode: StatusCodes.Status426UpgradeRequired);
            }

            context.Response.Headers["Sunset"] = evaluation.HttpSunsetHeader;
            return Results.Ok(evaluation);
        }).RequireAuthorization();


        // P12: Federated Differential Privacy & Dynamic Epsilon-Perturbation Engine
        app.MapGet("/api/governance/differential-privacy/budget/{clientId}", async (
            string clientId,
            IDifferentialPrivacyEngine dpEngine,
            HttpContext context) =>
        {
            var budget = await dpEngine.GetBudgetAsync(clientId, context.RequestAborted);
            return Results.Ok(budget);
        }).RequireAuthorization();

        app.MapPost("/api/governance/differential-privacy/budget/{clientId}/reset", async (
            string clientId,
            IDifferentialPrivacyEngine dpEngine,
            HttpContext context) =>
        {
            var isPrivileged = context.User.IsInRole("GovernanceAdmin") ||
                               context.User.IsInRole("PrivacyAdmin") ||
                               context.User.IsInRole("DataProtectionOfficer") ||
                               context.User.IsInRole("ClusterAdmin");
            if (!isPrivileged)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            await dpEngine.ResetBudgetAsync(clientId, context.RequestAborted);
            return Results.Ok(new { message = $"Privacy budget reset for client '{clientId}'." });
        }).RequireAuthorization();

        app.MapPost("/api/governance/differential-privacy/perturb", async (
            DifferentialPrivacyPerturbationRequest request,
            IDifferentialPrivacyEngine dpEngine,
            HttpContext context) =>
        {
            try
            {
                var result = await dpEngine.PerturbAsync(request, context.RequestAborted);
                context.Response.Headers["X-Privacy-Budget-Consumed"] = result.ConsumedEpsilon.ToString("F2");
                context.Response.Headers["X-Privacy-Budget-Remaining"] = result.RemainingEpsilon.ToString("F2");
                return Results.Ok(result);
            }
            catch (PrivacyBudgetExhaustedException ex)
            {
                context.Response.Headers["X-Privacy-Budget-Exhausted"] = "true";
                return Results.Json(new
                {
                    error = ex.Message,
                    clientId = ex.ClientId,
                    consumed = ex.ConsumedEpsilon,
                    totalBudget = ex.TotalBudget
                }, statusCode: StatusCodes.Status429TooManyRequests);
            }
        }).RequireAuthorization();


        // OData v4 / Power BI & Excel Direct Adapter Endpoints
        app.MapGet("/odata/v4", async (
            IODataHandler odataHandler,
            HttpContext context) =>
        {
            var serviceRoot = $"{context.Request.Scheme}://{context.Request.Host}/odata/v4";
            var doc = await odataHandler.GetServiceDocumentAsync(serviceRoot, context.RequestAborted);
            return Results.Json(doc, contentType: "application/json;odata.metadata=minimal;charset=utf-8");
        }).RequireAuthorization();

        app.MapGet("/odata/v4/$metadata", async (
            IODataHandler odataHandler,
            HttpContext context) =>
        {
            var xml = await odataHandler.GetMetadataCsdlAsync(context.RequestAborted);
            return Results.Content(xml, "application/xml;charset=utf-8");
        }).RequireAuthorization();

        app.MapGet("/odata/v4/{domain}/{schema}/{tableName}", async (
            string domain,
            string schema,
            string tableName,
            IODataHandler odataHandler,
            HttpContext context) =>
        {
            var serviceRoot = $"{context.Request.Scheme}://{context.Request.Host}/odata/v4";
            var tableId = new TableIdentifier(domain, schema, tableName);

            int? top = null;
            if (context.Request.Query.TryGetValue("$top", out var topVal))
            {
                if (!int.TryParse(topVal, out var t) || t < 0)
                {
                    return Results.Json(
                        new { error = new { code = "InvalidQueryOption", message = "The query parameter '$top' must be a non-negative integer." } },
                        statusCode: StatusCodes.Status400BadRequest,
                        contentType: "application/json;odata.metadata=minimal;charset=utf-8"
                    );
                }
                top = t;
            }

            int? skip = null;
            if (context.Request.Query.TryGetValue("$skip", out var skipVal))
            {
                if (!int.TryParse(skipVal, out var s) || s < 0)
                {
                    return Results.Json(
                        new { error = new { code = "InvalidQueryOption", message = "The query parameter '$skip' must be a non-negative integer." } },
                        statusCode: StatusCodes.Status400BadRequest,
                        contentType: "application/json;odata.metadata=minimal;charset=utf-8"
                    );
                }
                skip = s;
            }

            string? select = context.Request.Query["$select"].FirstOrDefault();
            bool includeCount = context.Request.Query.TryGetValue("$count", out var countVal) && bool.TryParse(countVal, out var c) && c;

            var headers = context.Request.Headers.ToDictionary(h => h.Key, h => h.Value.Select(v => v ?? string.Empty).ToArray());

            var result = await odataHandler.ExecuteEntitySetQueryAsync(
                principal: context.User,
                serviceRootUrl: serviceRoot,
                table: tableId,
                top: top,
                skip: skip,
                select: select,
                includeCount: includeCount,
                headers: headers,
                ct: context.RequestAborted
            );

            return Results.Json(result.Payload, statusCode: result.StatusCode, contentType: "application/json;odata.metadata=minimal;charset=utf-8");
        }).RequireAuthorization();

        // -------------------------------------------------------------
        // Model Context Protocol (MCP) Server Endpoints (F-AI-01)
        // -------------------------------------------------------------
        if (gatewayOptions.Mcp.Enabled)
        {
            var mcpBasePath = string.IsNullOrWhiteSpace(gatewayOptions.Mcp.EndpointPath)
                ? "/mcp"
                : gatewayOptions.Mcp.EndpointPath.TrimEnd('/');

            // 1. SSE Connection Handshake
            var sseEndpoint = app.MapGet($"{mcpBasePath}/sse", async (
                IMcpProtocolHandler mcpHandler,
                IMcpSessionStore sessionStore,
                HttpContext context) =>
            {
                var principal = context.User;
                var isAuthenticated = principal.Identity?.IsAuthenticated == true;

                if (!isAuthenticated && !gatewayOptions.IsMcpAuthBypassed)
                {
                    return Results.Unauthorized();
                }

                var principalId = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value
                    ?? principal.Identity?.Name
                    ?? (gatewayOptions.IsMcpAuthBypassed ? "anonymous-ai-agent" : "unknown-agent");

                var tenantId = principal.FindFirst("tenant_id")?.Value
                    ?? principal.FindFirst("tid")?.Value
                    ?? "default";

                var userSid = principal.GetUserSid()?.Value;
                var roles = principal.GetUserRoles().ToList();
                var groupSids = principal.GetGroupSids().Select(s => s.Value).ToList();

                var session = mcpHandler.CreateSession(principalId, tenantId, userSid, roles, groupSids);

                sessionStore.RegisterSseSender(session.SessionId, async (evt, data) =>
                {
                    // SEC-3: Sanitize event name and properly format multi-line data to eliminate SSE CRLF injection
                    var cleanEvt = string.IsNullOrWhiteSpace(evt) ? "message" : System.Text.RegularExpressions.Regex.Replace(evt, @"[\r\n]", string.Empty);
                    var normalizedData = (data ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
                    var lines = normalizedData.Split('\n');
                    var sb = new System.Text.StringBuilder();
                    sb.Append("event: ").Append(cleanEvt).Append('\n');
                    foreach (var line in lines)
                    {
                        sb.Append("data: ").Append(line).Append('\n');
                    }
                    sb.Append('\n');
                    await context.Response.WriteAsync(sb.ToString(), context.RequestAborted).ConfigureAwait(false);
                    await context.Response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);
                });

                context.Response.Headers.ContentType = "text/event-stream";
                context.Response.Headers.CacheControl = "no-cache";
                context.Response.Headers.Connection = "keep-alive";

                var messageUri = $"{mcpBasePath}/message?sessionId={session.SessionId}";
                await context.Response.WriteAsync($"event: endpoint\r\ndata: {messageUri}\r\n\r\n", context.RequestAborted).ConfigureAwait(false);
                await context.Response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);

                try
                {
                    while (!context.RequestAborted.IsCancellationRequested)
                    {
                        await Task.Delay(15000, context.RequestAborted).ConfigureAwait(false);
                        await context.Response.WriteAsync(": ping\r\n\r\n", context.RequestAborted).ConfigureAwait(false);
                        await context.Response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    // Normal client disconnect
                }
                finally
                {
                    mcpHandler.RemoveSession(session.SessionId);
                }

                return Results.Empty;
            });

            if (!gatewayOptions.IsMcpAuthBypassed)
            {
                sseEndpoint.RequireAuthorization();
            }

            // 2. JSON-RPC Message Receiver
            var messageEndpoint = app.MapPost($"{mcpBasePath}/message", async (
                IMcpProtocolHandler mcpHandler,
                HttpContext context) =>
            {
                var sessionId = context.Request.Query["sessionId"].FirstOrDefault()
                    ?? context.Request.Headers["X-MCP-Session-Id"].FirstOrDefault();

                if (string.IsNullOrWhiteSpace(sessionId))
                {
                    return Results.BadRequest(new { error = "Missing 'sessionId' query parameter or 'X-MCP-Session-Id' header." });
                }

                var session = mcpHandler.GetSession(sessionId);
                if (session == null)
                {
                    return Results.NotFound(new { error = $"Invalid or expired MCP session '{sessionId}'." });
                }

                if (!gatewayOptions.IsMcpAuthBypassed)
                {
                    var callerId = context.User.FindFirst("client_id")?.Value
                        ?? context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                        ?? context.User.FindFirst("sub")?.Value
                        ?? context.User.FindFirst("appid")?.Value
                        ?? context.User.Identity?.Name;

                    if (!string.IsNullOrWhiteSpace(callerId) &&
                        !string.Equals(session.ServicePrincipalId, callerId, StringComparison.OrdinalIgnoreCase))
                    {
                        return Results.StatusCode(StatusCodes.Status403Forbidden);
                    }
                }

                using var reader = new System.IO.StreamReader(context.Request.Body);
                var payload = await reader.ReadToEndAsync(context.RequestAborted).ConfigureAwait(false);

                if (string.IsNullOrWhiteSpace(payload))
                {
                    return Results.BadRequest(new { error = "Empty JSON-RPC payload." });
                }

                var responseJson = await mcpHandler.HandleMessageAsync(sessionId, payload, context.RequestAborted).ConfigureAwait(false);

                return Results.Content(responseJson, "application/json; charset=utf-8");
            });

            if (!gatewayOptions.IsMcpAuthBypassed)
            {
                messageEndpoint.RequireAuthorization();
            }

            // 2b. Streamable HTTP Transport (MCP 2024-11-05 Specification)
            // Allows developer CLIs and HTTP clients to directly stream JSON-RPC requests via POST /mcp or POST /mcp/stream
            var streamableHttpEndpoint = app.MapPost(mcpBasePath, async (
                IMcpProtocolHandler mcpHandler,
                HttpContext context) =>
            {
                var sessionId = context.Request.Headers["X-MCP-Session-Id"].FirstOrDefault()
                    ?? context.Request.Headers["Mcp-Session-Id"].FirstOrDefault()
                    ?? context.Request.Query["sessionId"].FirstOrDefault();

                McpSessionContext? session = null;
                if (!string.IsNullOrWhiteSpace(sessionId))
                {
                    session = mcpHandler.GetSession(sessionId);
                    if (session != null && !gatewayOptions.IsMcpAuthBypassed)
                    {
                        var callerId = context.User.FindFirst("client_id")?.Value
                            ?? context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                            ?? context.User.FindFirst("sub")?.Value
                            ?? context.User.FindFirst("appid")?.Value
                            ?? context.User.Identity?.Name;

                        if (!string.IsNullOrWhiteSpace(callerId) &&
                            !string.Equals(session.ServicePrincipalId, callerId, StringComparison.OrdinalIgnoreCase))
                        {
                            return Results.StatusCode(StatusCodes.Status403Forbidden);
                        }
                    }
                }

                if (session == null)
                {
                    var principal = context.User;
                    var principalId = principal.FindFirst("client_id")?.Value
                        ?? principal.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                        ?? principal.FindFirst("sub")?.Value
                        ?? principal.FindFirst("appid")?.Value
                        ?? principal.Identity?.Name
                        ?? (gatewayOptions.IsMcpAuthBypassed ? "anonymous-ai-agent" : "cli-developer");

                    var tenantId = principal.FindFirst("tenant_id")?.Value
                        ?? principal.FindFirst("tid")?.Value
                        ?? "default";

                    var userSid = principal.GetUserSid()?.Value;
                    var roles = principal.GetUserRoles().ToList();
                    var groupSids = principal.GetGroupSids().Select(s => s.Value).ToList();

                    session = mcpHandler.CreateSession(principalId, tenantId, userSid, roles, groupSids);
                }

                using var reader = new System.IO.StreamReader(context.Request.Body);
                var payload = await reader.ReadToEndAsync(context.RequestAborted).ConfigureAwait(false);

                if (string.IsNullOrWhiteSpace(payload))
                {
                    return Results.BadRequest(new { error = "Empty JSON-RPC payload." });
                }

                context.Response.Headers["X-MCP-Session-Id"] = session.SessionId;
                context.Response.Headers["Mcp-Session-Id"] = session.SessionId;

                var responseJson = await mcpHandler.HandleMessageAsync(session.SessionId, payload, context.RequestAborted).ConfigureAwait(false);
                return Results.Content(responseJson, "application/json; charset=utf-8");
            });

            if (!gatewayOptions.IsMcpAuthBypassed)
            {
                streamableHttpEndpoint.RequireAuthorization();
            }


            // 3. Session Teardown
            var sessionEndpoint = app.MapDelete($"{mcpBasePath}/session/{{id}}", (
                string id,
                IMcpProtocolHandler mcpHandler,
                HttpContext context) =>
            {
                var session = mcpHandler.GetSession(id);
                if (session == null)
                {
                    return Results.NotFound(new { error = $"Session '{id}' not found." });
                }

                if (!gatewayOptions.IsMcpAuthBypassed)
                {
                    var callerId = context.User.FindFirst("client_id")?.Value
                        ?? context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                        ?? context.User.FindFirst("sub")?.Value
                        ?? context.User.FindFirst("appid")?.Value
                        ?? context.User.Identity?.Name;

                    if (!string.IsNullOrWhiteSpace(callerId) &&
                        !string.Equals(session.ServicePrincipalId, callerId, StringComparison.OrdinalIgnoreCase))
                    {
                        return Results.StatusCode(StatusCodes.Status403Forbidden);
                    }
                }

                var removed = mcpHandler.RemoveSession(id);
                return removed ? Results.NoContent() : Results.NotFound();
            });

            if (!gatewayOptions.IsMcpAuthBypassed)
            {
                sessionEndpoint.RequireAuthorization();
            }
        }

        // Schema Registry & CI/CD Endpoints (P8)
        app.MapPost("/api/schema-registry/publish", async (
            SchemaRegistrationRequest request,
            ISchemaRegistryService registry,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
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
            SchemaRegistrationRequest request,
            ISchemaRegistryService registry,
            CancellationToken ct) =>
        {
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

        // GDPR Article 15 PDF Export for Data Protection Officers (DSB)
        app.MapGet("/api/governance/gdpr/export-pdf", async (
            string? domain,
            string? schema,
            string? table,
            string? subjectSid,
            int? timeWindowDays,
            ILineageImpactAnalyzerService lineageService,
            IGdprAuditReportExporter pdfExporter,
            HttpContext context,
            CancellationToken ct) =>
        {
            var principal = context.User;
            var userSid = principal.GetUserSid() ?? new Sid("S-1-5-21-ANONYMOUS");
            var groupSids = principal.GetGroupSids().ToList();
            var roles = principal.GetUserRoles().ToList();

            var tenantId = TenantId.LegacySingleTenant;
            if (context.Items.TryGetValue("TenantId", out var tidObj) == true && tidObj is TenantId tid)
            {
                tenantId = tid;
            }

            bool isGovAdmin = roles.Contains("GovernanceAdmin", StringComparer.OrdinalIgnoreCase);
            bool isClusterAdmin = roles.Contains("ClusterAdmin", StringComparer.OrdinalIgnoreCase);
            bool isPrivacyAdmin = roles.Contains("PrivacyAdmin", StringComparer.OrdinalIgnoreCase) ||
                                  roles.Contains("DataProtectionOfficer", StringComparer.OrdinalIgnoreCase);

            var callerContext = new CallerSecurityContext(
                userSid,
                groupSids,
                roles,
                tenantId,
                isGovAdmin,
                isClusterAdmin);

            TableIdentifier? tableId = !string.IsNullOrWhiteSpace(domain) && !string.IsNullOrWhiteSpace(schema) && !string.IsNullOrWhiteSpace(table)
                ? new TableIdentifier(domain, schema, table)
                : null;
            Sid? sid = !string.IsNullOrWhiteSpace(subjectSid) ? new Sid(subjectSid) : (Sid?)null;

            bool canAccessForeignReports = isPrivacyAdmin || isGovAdmin || isClusterAdmin;
            var effectiveSid = sid ?? (canAccessForeignReports ? (Sid?)null : callerContext.UserSid);

            if (effectiveSid.HasValue && !effectiveSid.Value.Equals(callerContext.UserSid) && !canAccessForeignReports)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var report = await lineageService.GetGdprDataDisclosureReportAsync(tableId, effectiveSid, timeWindowDays ?? 365, callerContext, ct);
            var exportResult = pdfExporter.ExportReportToPdf(report);

            context.Response.Headers["X-Audit-Seal-SHA256"] = exportResult.Sha256AuditSeal;
            return Results.File(exportResult.DocumentBytes, exportResult.ContentType, exportResult.FileName);
        }).RequireAuthorization();

        // OpenLineage Lineage Push Trigger
        app.MapPost("/api/lineage/openlineage/sync", async (
            IOpenLineageClient openLineageClient,
            HttpContext context,
            CancellationToken ct) =>
        {
            var tenantId = context.User.FindFirst("tenant_id")?.Value ?? "default";
            var success = await openLineageClient.PushLineageGraphAsync(new TenantId(tenantId), ct);
            return success
                ? Results.Ok(new { message = "OpenLineage sync completed successfully." })
                : Results.StatusCode(StatusCodes.Status502BadGateway);
        }).RequireAuthorization();

        return app;
    }
}

