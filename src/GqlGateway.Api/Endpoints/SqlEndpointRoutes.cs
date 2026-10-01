namespace GqlGateway.Api.Endpoints;

using System;
using System.Collections.Generic;
using System.IO;
using System.Security;
using System.Text.Json;
using System.Threading.Tasks;
using GqlGateway.Application.SqlEndpoints.Interfaces;
using GqlGateway.Domain.Common;
using GqlGateway.Domain.Options;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public static class SqlEndpointRoutes
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static IEndpointRouteBuilder MapSqlEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/queries")
                       .RequireAuthorization();

        group.MapGet("/", HandleListEndpoints)
             .WithName("ListSqlEndpoints");

        group.MapGet("/openapi.json", HandleOpenApiSpec)
             .WithName("GetSqlEndpointsOpenApiSpec");

        group.MapGet("/{name}", HandleGetEndpoint)
             .WithName("ExecuteSqlEndpointGet");

        group.MapPost("/{name}", HandlePostEndpoint)
             .WithName("ExecuteSqlEndpointPost");

        return app;
    }

    private static IResult HandleListEndpoints(ISqlEndpointRegistry registry)
    {
        var endpoints = registry.GetAll();
        return Results.Ok(endpoints);
    }

    private static IResult HandleOpenApiSpec(ISqlEndpointRegistry registry)
    {
        var endpoints = registry.GetAll();
        var paths = new Dictionary<string, object>();

        foreach (var ep in endpoints)
        {
            var queryParams = new List<object>();
            foreach (var p in ep.Parameters)
            {
                var schema = new Dictionary<string, object>
                {
                    ["type"] = GetJsonType(p.ClrType)
                };
                if (p.DefaultValue != null)
                {
                    schema["default"] = p.DefaultValue;
                }
                if (p.ClrType == typeof(DateTimeOffset) || p.ClrType == typeof(DateTime))
                {
                    schema["format"] = "date-time";
                }

                queryParams.Add(new Dictionary<string, object?>
                {
                    ["name"] = p.Name,
                    ["in"] = "query",
                    ["required"] = p.IsRequired,
                    ["description"] = p.Description ?? $"Parameter {p.Name}",
                    ["schema"] = schema
                });
            }

            var properties = new Dictionary<string, object>();
            foreach (var proj in ep.Projections)
            {
                properties[proj.ColumnName] = new Dictionary<string, object>
                {
                    ["type"] = "string"
                };
            }

            var operation = new Dictionary<string, object>
            {
                ["summary"] = ep.Summary,
                ["description"] = !string.IsNullOrWhiteSpace(ep.Summary) ? ep.Summary : $"Executes declarative SQL query '{ep.Name}'",
                ["operationId"] = ep.Name,
                ["parameters"] = queryParams,
                ["responses"] = new Dictionary<string, object>
                {
                    ["200"] = new Dictionary<string, object>
                    {
                        ["description"] = "Successful query response",
                        ["content"] = new Dictionary<string, object>
                        {
                            ["application/json"] = new Dictionary<string, object>
                            {
                                ["schema"] = new Dictionary<string, object>
                                {
                                    ["type"] = "array",
                                    ["items"] = new Dictionary<string, object>
                                    {
                                        ["type"] = "object",
                                        ["properties"] = properties
                                    }
                                }
                            }
                        }
                    },
                    ["400"] = new Dictionary<string, object> { ["description"] = "Invalid parameters" },
                    ["403"] = new Dictionary<string, object> { ["description"] = "Access forbidden by ABAC policy" },
                    ["404"] = new Dictionary<string, object> { ["description"] = "SQL query not found" }
                }
            };

            paths[$"/api/v1/queries/{ep.Name}"] = new Dictionary<string, object>
            {
                ["get"] = operation
            };
        }

        var openApiDoc = new Dictionary<string, object>
        {
            ["openapi"] = "3.0.1",
            ["info"] = new Dictionary<string, object>
            {
                ["title"] = "GqlGateway Declarative SQL Endpoints API",
                ["version"] = "v1",
                ["description"] = "Auto-generated OpenAPI / Swagger documentation from declarative SQL endpoints with AST-inferred parameter types and zero-trust governance."
            },
            ["paths"] = paths
        };

        return Results.Ok(openApiDoc);
    }

    private static string GetJsonType(Type clrType)
    {
        if (clrType == typeof(int) || clrType == typeof(long) || clrType == typeof(short)) return "integer";
        if (clrType == typeof(decimal) || clrType == typeof(double) || clrType == typeof(float)) return "number";
        if (clrType == typeof(bool)) return "boolean";
        return "string";
    }

    private static async Task HandleGetEndpoint(
        string name,
        HttpContext httpContext,
        ISqlEndpointExecutionService executionService,
        IOptions<GatewayOptions> gatewayOptions,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("GqlGateway.Api.SqlEndpoints");
        var ct = httpContext.RequestAborted;

        try
        {
            var rawInputs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in httpContext.Request.Query)
            {
                rawInputs[k] = v.ToString();
            }

            var tenantId = ResolveTenantId(httpContext);
            var result = await executionService.ExecuteEndpointAsync(
                name,
                rawInputs,
                httpContext.User,
                tenantId,
                ct).ConfigureAwait(false);

            httpContext.Response.ContentType = "application/json; charset=utf-8";
            httpContext.Response.StatusCode = StatusCodes.Status200OK;
            await JsonSerializer.SerializeAsync(httpContext.Response.Body, result.Rows, JsonOptions, ct).ConfigureAwait(false);
        }
        catch (KeyNotFoundException knf)
        {
            httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
            await httpContext.Response.WriteAsJsonAsync(new { error = knf.Message }, ct).ConfigureAwait(false);
        }
        catch (ArgumentException argEx)
        {
            httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            await httpContext.Response.WriteAsJsonAsync(new { error = argEx.Message }, ct).ConfigureAwait(false);
        }
        catch (SecurityException secEx)
        {
            logger.LogWarning(secEx, "Security policy violation when executing endpoint '{EndpointName}'.", name);
            httpContext.Response.StatusCode = StatusCodes.Status403Forbidden;
            await httpContext.Response.WriteAsJsonAsync(new { error = secEx.Message }, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error executing SQL endpoint '{EndpointName}'.", name);
            httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await httpContext.Response.WriteAsJsonAsync(new { error = "An internal error occurred during query execution." }, ct).ConfigureAwait(false);
        }
    }

    private static async Task HandlePostEndpoint(
        string name,
        HttpContext httpContext,
        ISqlEndpointExecutionService executionService,
        IOptions<GatewayOptions> gatewayOptions,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("GqlGateway.Api.SqlEndpoints");
        var ct = httpContext.RequestAborted;

        if (httpContext.Request.ContentLength > 2 * 1024 * 1024)
        {
            httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            await httpContext.Response.WriteAsJsonAsync(new { error = "Request body exceeds maximum size limit (2 MB)." }, ct).ConfigureAwait(false);
            return;
        }

        try
        {
            Dictionary<string, object?>? rawInputs = null;
            if (httpContext.Request.ContentLength > 0)
            {
                using var reader = new StreamReader(httpContext.Request.Body);
                string body = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(body))
                {
                    rawInputs = JsonSerializer.Deserialize<Dictionary<string, object?>>(body, JsonOptions);
                }
            }

            var tenantId = ResolveTenantId(httpContext);
            var result = await executionService.ExecuteEndpointAsync(
                name,
                rawInputs,
                httpContext.User,
                tenantId,
                ct).ConfigureAwait(false);

            httpContext.Response.ContentType = "application/json; charset=utf-8";
            httpContext.Response.StatusCode = StatusCodes.Status200OK;
            await JsonSerializer.SerializeAsync(httpContext.Response.Body, result.Rows, JsonOptions, ct).ConfigureAwait(false);
        }
        catch (KeyNotFoundException knf)
        {
            httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
            await httpContext.Response.WriteAsJsonAsync(new { error = knf.Message }, ct).ConfigureAwait(false);
        }
        catch (ArgumentException argEx)
        {
            httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            await httpContext.Response.WriteAsJsonAsync(new { error = argEx.Message }, ct).ConfigureAwait(false);
        }
        catch (SecurityException secEx)
        {
            logger.LogWarning(secEx, "Security policy violation when executing endpoint '{EndpointName}'.", name);
            httpContext.Response.StatusCode = StatusCodes.Status403Forbidden;
            await httpContext.Response.WriteAsJsonAsync(new { error = secEx.Message }, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error executing SQL endpoint '{EndpointName}'.", name);
            httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await httpContext.Response.WriteAsJsonAsync(new { error = "An internal error occurred during query execution." }, ct).ConfigureAwait(false);
        }
    }

    private static TenantId ResolveTenantId(HttpContext httpContext)
    {
        var tenantClaim = httpContext.User.FindFirst("tenant_id")?.Value
                       ?? httpContext.User.FindFirst("tid")?.Value
                       ?? httpContext.Request.Headers["X-Tenant-ID"].ToString();

        return !string.IsNullOrWhiteSpace(tenantClaim)
            ? new TenantId(tenantClaim)
            : new TenantId("default");
    }
}
