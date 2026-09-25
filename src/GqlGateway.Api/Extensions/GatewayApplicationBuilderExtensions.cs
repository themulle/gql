using GqlGateway.Api.Middleware;
using GqlGateway.Application.Interfaces;
using GqlGateway.Domain.Options;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

namespace GqlGateway.Api.Extensions;

public static class GatewayApplicationBuilderExtensions
{
    public static WebApplication UseGatewayPipeline(this WebApplication app, GatewayOptions gatewayOptions)
    {
        app.UseForwardedHeaders();

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
            }

            await next();
        });

        app.UseMiddleware<PreAuthIpRateLimitingMiddleware>();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseMiddleware<PostAuthSidRateLimitingMiddleware>();

        return app;
    }

    public static WebApplication MapGatewayEndpoints(this WebApplication app, GatewayOptions gatewayOptions)
    {
        app.MapGet("/health/live", () => Results.Ok(new { status = "Live", timestamp = DateTimeOffset.UtcNow }));

        app.MapGet("/health/ready", (ITrafficDrainController controller) =>
        {
            if (controller.IsDraining)
            {
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }
            return Results.Ok(new { status = "Ready", timestamp = DateTimeOffset.UtcNow });
        });

        var endpoint = gatewayOptions.GraphQL.EndpointPath.StartsWith('/')
            ? gatewayOptions.GraphQL.EndpointPath
            : "/" + gatewayOptions.GraphQL.EndpointPath;

        app.MapGraphQL(endpoint).RequireAuthorization();

        return app;
    }
}
