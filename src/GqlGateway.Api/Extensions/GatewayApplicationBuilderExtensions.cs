using GqlGateway.Api.Endpoints;
using GqlGateway.Api.Middleware;
using GqlGateway.Domain.Options;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Prometheus;
using System;
using System.Linq;

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

        if (gatewayOptions.ReverseProxy.Enabled)
        {
            app.UseForwardedHeaders();
        }
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

            bool isGraphQLEndpoint = (HttpMethods.IsPost(context.Request.Method) ||
                 (HttpMethods.IsGet(context.Request.Method) && context.Request.Query.ContainsKey("query")))
                && context.Request.Path.StartsWithSegments(endpoint);

            bool isStateChangingRestEndpoint = (HttpMethods.IsPost(context.Request.Method) ||
                                                HttpMethods.IsPut(context.Request.Method) ||
                                                HttpMethods.IsDelete(context.Request.Method) ||
                                                HttpMethods.IsPatch(context.Request.Method))
                                               && (context.Request.Path.StartsWithSegments("/api") || context.Request.Path.StartsWithSegments("/odata"));

            if (isGraphQLEndpoint || isStateChangingRestEndpoint)
            {
                // For REST endpoints, CSRF attack vectors require browser execution with ambient credentials (Cookie, cached Basic/Negotiate)
                // indicated by Cookie, Sec-Fetch-*, or Origin/Referer headers.
                // Exclude explicit login exchange (/api/auth/login) unless ambient Cookie is present.
                bool isLoginEndpoint = context.Request.Path.Equals("/api/auth/login", StringComparison.OrdinalIgnoreCase);
                bool hasBrowserIndicators = context.Request.Headers.ContainsKey("Cookie") ||
                                            context.Request.Headers.ContainsKey("Origin") ||
                                            context.Request.Headers.ContainsKey("Referer") ||
                                            context.Request.Headers.ContainsKey("Sec-Fetch-Site");

                bool requiresCsrfProtection = isGraphQLEndpoint || (!isLoginEndpoint && hasBrowserIndicators);

                if (requiresCsrfProtection)
                {
                    bool hasPreflightHeader = context.Request.Headers.ContainsKey("GraphQL-Preflight") ||
                                              context.Request.Headers.ContainsKey("X-Requested-With") ||
                                              context.Request.Headers.ContainsKey("X-CSRF-Token");

                    if (!hasPreflightHeader)
                    {
                        context.Response.StatusCode = StatusCodes.Status400BadRequest;
                        var endpointType = isGraphQLEndpoint ? "GraphQL" : "REST";
                        await context.Response.WriteAsJsonAsync(new
                        {
                            error = $"CSRF Protection: Missing custom preflight header ('GraphQL-Preflight: 1', 'X-Requested-With', or 'X-CSRF-Token') on {endpointType} request."
                        });
                        return;
                    }
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
        app.UseMiddleware<TenantResolutionMiddleware>();
        app.UseMiddleware<ResourceGroupMiddleware>();
        app.UseMiddleware<OpenTelemetryTracingMiddleware>();
        app.UseMiddleware<GatewayExtensibilityMiddleware>();

        return app;
    }

    public static WebApplication MapGatewayEndpoints(this WebApplication app, GatewayOptions gatewayOptions)
    {
        app.MapMetrics().RequireAuthorization();

        // 1. Health & Readiness Probes
        app.MapHealthEndpoints(gatewayOptions, app.Environment);

        // 2. Core GraphQL Engine & Dynamic Plugins
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

        // 3. Modular Feature Endpoints (Route Groups)
        app.MapAuthEndpoints();
        app.MapWebhookEndpoints(gatewayOptions);
        app.MapStreamingCdcEndpoints();
        app.MapDbtEndpoints();
        app.MapGovernanceEndpoints();
        app.MapSystemEndpoints();
        app.MapODataEndpoints(gatewayOptions);
        app.MapMcpEndpoints(gatewayOptions);
        app.MapSchemaRegistryEndpoints();
        app.MapBackstageEndpoints(gatewayOptions);
        app.MapExportEndpoints();
        app.MapHitLEndpoints();

        return app;
    }
}
