using GqlGateway.Api.Middleware;
using GqlGateway.Application.Interfaces;
using GqlGateway.Application.OpenMetadata.Interfaces;
using GqlGateway.Domain.Common;
using GqlGateway.Domain.Options;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
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

        var endpoint = gatewayOptions.GraphQL.EndpointPath.StartsWith('/')
            ? gatewayOptions.GraphQL.EndpointPath
            : "/" + gatewayOptions.GraphQL.EndpointPath;

        // Anti-CSRF Middleware: Enforce custom preflight header on GraphQL POST and GET query requests
        app.Use(async (context, next) =>
        {
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
        app.UseMiddleware<PostAuthSidRateLimitingMiddleware>();

        return app;
    }

    public static WebApplication MapGatewayEndpoints(this WebApplication app, GatewayOptions gatewayOptions)
    {
        app.MapMetrics();
        app.MapGet("/health/live", () => Results.Ok(new { status = "Live", timestamp = DateTimeOffset.UtcNow }));

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
                    return Results.Json(new
                    {
                        status = "Unhealthy",
                        timestamp = DateTimeOffset.UtcNow,
                        components = report.Components
                    }, statusCode: StatusCodes.Status503ServiceUnavailable);
                }
            }

            return Results.Ok(new { status = "Ready", timestamp = DateTimeOffset.UtcNow });
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
            IOpenMetadataSyncService syncService) =>
        {
            if (context.Request.ContentLength > 2 * 1024 * 1024)
            {
                return Results.BadRequest(new { error = "Payload size exceeds maximum allowed size (2 MB)." });
            }

            using var reader = new StreamReader(context.Request.Body);
            var payload = await reader.ReadToEndAsync();

            string? signature = context.Request.Headers["X-OpenMetadata-Signature"].FirstOrDefault() ??
                                context.Request.Headers["X-OM-Signature"].FirstOrDefault();

            var success = await syncService.HandleWebhookEventAsync(payload, signature, context.RequestAborted);
            if (!success)
            {
                return Results.BadRequest(new { error = "Failed to process webhook or invalid signature." });
            }

            return Results.Ok(new { status = "Processed" });
        }).AllowAnonymous();

        return app;
    }
}
