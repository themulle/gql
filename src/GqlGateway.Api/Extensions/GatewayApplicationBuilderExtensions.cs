using GqlGateway.Api.Middleware;
using GqlGateway.Application.Interfaces;
using GqlGateway.Application.OpenMetadata.Interfaces;
using GqlGateway.Application.Dbt.Interfaces;
using GqlGateway.Application.Mcp.Interfaces;
using GqlGateway.Extensions.OData;
using GqlGateway.Domain.Common;
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

        if (!app.Environment.IsDevelopment())
        {
            app.UseHsts();
            app.UseHttpsRedirection();
        }

        if (gatewayOptions.HasAnySecurityBypassActive)
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
        });

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
        });

        var endpoint = gatewayOptions.GraphQL.EndpointPath.StartsWith('/')
            ? gatewayOptions.GraphQL.EndpointPath
            : "/" + gatewayOptions.GraphQL.EndpointPath;

        var pluginManager = app.Services.GetService<GqlGateway.Application.Plugins.IPluginManager>();
        if (pluginManager != null && !string.IsNullOrWhiteSpace(gatewayOptions.Plugins.Directory))
        {
            pluginManager.LoadPluginsFromDirectory(gatewayOptions.Plugins.Directory);
        }

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

        app.MapPost("/api/webhooks/itsm/status-change", async (
            HttpContext context,
            IItsmWebhookHandler webhookHandler,
            IOptions<GatewayOptions> gatewayOptions) =>
        {
            if (context.Request.ContentLength > 2 * 1024 * 1024)
            {
                return Results.BadRequest(new { error = "Payload size exceeds maximum allowed size (2 MB)." });
            }

            using var reader = new StreamReader(context.Request.Body);
            var payload = await reader.ReadToEndAsync(context.RequestAborted);

            var opts = gatewayOptions.Value;
            string? signature = context.Request.Headers["X-ITSM-Signature"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(signature))
            {
                if (!opts.IsWebhookSignatureBypassed)
                {
                    return Results.Unauthorized();
                }
                signature = "bypassed";
            }

            DateTimeOffset timestamp;
            if (!context.Request.Headers.TryGetValue("X-ITSM-Timestamp", out var tsHeader) ||
                !DateTimeOffset.TryParse(tsHeader.FirstOrDefault(), out timestamp))
            {
                if (!opts.IsWebhookTimestampToleranceIgnored)
                {
                    return Results.BadRequest(new { error = "Header X-ITSM-Timestamp is required and must be a valid ISO 8601 timestamp." });
                }
                timestamp = DateTimeOffset.UtcNow;
            }

            var success = await webhookHandler.HandleStatusChangeAsync(payload, signature, timestamp, context.RequestAborted);
            if (!success)
            {
                return Results.Unauthorized();
            }

            return Results.Ok(new { status = "Processed" });
        }).AllowAnonymous();

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

                var session = mcpHandler.CreateSession(principalId, tenantId);

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

            // 3. Session Teardown
            app.MapDelete($"{mcpBasePath}/session/{{id}}", (
                string id,
                IMcpProtocolHandler mcpHandler) =>
            {
                var removed = mcpHandler.RemoveSession(id);
                return removed ? Results.NoContent() : Results.NotFound();
            });
        }

        return app;
    }
}
