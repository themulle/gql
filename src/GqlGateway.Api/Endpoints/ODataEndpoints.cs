namespace GqlGateway.Api.Endpoints;

using System;
using System.Linq;
using GqlGateway.Api.Middleware;
using GqlGateway.Application.OData.Interfaces;
using GqlGateway.Domain.Common;
using GqlGateway.Domain.Options;
using GqlGateway.Extensions.OData;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

public static class ODataEndpoints
{
    public static IEndpointRouteBuilder MapODataEndpoints(this IEndpointRouteBuilder app, GatewayOptions gatewayOptions)
    {
        // OData v4 / Power BI & Excel Direct Adapter Endpoints
        app.MapGet("/odata/v4", async (
            IODataHandler odataHandler,
            HttpContext context) =>
        {
            var serviceRoot = $"{context.Request.Scheme}://{context.Request.Host}/odata/v4";
            var doc = await odataHandler.GetServiceDocumentAsync(serviceRoot, context.User, context.RequestAborted);
            return Results.Json(doc, contentType: "application/json;odata.metadata=minimal;charset=utf-8");
        }).RequireAuthorization();

        app.MapGet("/odata/v4/$metadata", async (
            IODataHandler odataHandler,
            HttpContext context) =>
        {
            var xml = await odataHandler.GetMetadataCsdlAsync(context.User, context.RequestAborted);
            return Results.Content(xml, "application/xml;charset=utf-8");
        }).RequireAuthorization();

        // Dynamic OpenAPI 3.1 & Swagger UI Explorer (F-API-03)
        app.MapGet("/odata/v4/$openapi", async (
            IDynamicOpenApiGenerator generator,
            IOpenApiCacheManager cacheManager,
            HttpContext context) =>
        {
            var roles = context.User.GetUserRoles();
            bool isPrivileged = roles.Contains("GovernanceAdmin") ||
                               roles.Contains("ClusterAdmin") ||
                               roles.Contains("DataOwner") ||
                               roles.Contains("SchemaAdmin") ||
                               roles.Contains("CatalogReader");
            if (!isPrivileged)
            {
                return Results.Forbid();
            }

            var format = context.Request.Query["format"].ToString();
            var accept = context.Request.Headers.Accept.ToString();
            bool isYaml = string.Equals(format, "yaml", StringComparison.OrdinalIgnoreCase) ||
                          accept.Contains("application/yaml", StringComparison.OrdinalIgnoreCase);

            var bytes = await cacheManager.GetOrAddAsync(
                domainScope: null,
                isYaml: isYaml,
                factory: ct => isYaml ? generator.GenerateOpenApiYamlAsync(null, ct) : generator.GenerateOpenApiJsonAsync(null, ct),
                ct: context.RequestAborted);

            var contentType = isYaml ? "application/yaml;charset=utf-8" : "application/json;charset=utf-8";
            return Results.Bytes(bytes, contentType: contentType);
        }).RequireAuthorization();

        app.MapGet("/odata/v4/{domain}/openapi.json", async (
            string domain,
            IDynamicOpenApiGenerator generator,
            IOpenApiCacheManager cacheManager,
            HttpContext context) =>
        {
            var roles = context.User.GetUserRoles();
            bool isPrivileged = roles.Contains("GovernanceAdmin") ||
                               roles.Contains("ClusterAdmin") ||
                               roles.Contains("DataOwner") ||
                               roles.Contains("SchemaAdmin") ||
                               roles.Contains("CatalogReader");
            if (!isPrivileged)
            {
                return Results.Forbid();
            }

            var bytes = await cacheManager.GetOrAddAsync(
                domainScope: domain,
                isYaml: false,
                factory: ct => generator.GenerateOpenApiJsonAsync(domain, ct),
                ct: context.RequestAborted);

            return Results.Bytes(bytes, contentType: "application/json;charset=utf-8");
        }).RequireAuthorization();

        app.MapGet("/odata/v4/{domain}/openapi.yaml", async (
            string domain,
            IDynamicOpenApiGenerator generator,
            IOpenApiCacheManager cacheManager,
            HttpContext context) =>
        {
            var roles = context.User.GetUserRoles();
            bool isPrivileged = roles.Contains("GovernanceAdmin") ||
                               roles.Contains("ClusterAdmin") ||
                               roles.Contains("DataOwner") ||
                               roles.Contains("SchemaAdmin") ||
                               roles.Contains("CatalogReader");
            if (!isPrivileged)
            {
                return Results.Forbid();
            }

            var bytes = await cacheManager.GetOrAddAsync(
                domainScope: domain,
                isYaml: true,
                factory: ct => generator.GenerateOpenApiYamlAsync(domain, ct),
                ct: context.RequestAborted);

            return Results.Bytes(bytes, contentType: "application/yaml;charset=utf-8");
        }).RequireAuthorization();

        app.MapGet("/odata/v4/$swagger", (HttpContext context, IWebHostEnvironment env) =>
        {
            if (!env.IsDevelopment() && context.User?.Identity?.IsAuthenticated != true)
            {
                return Results.Unauthorized();
            }
            var nonce = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
            context.Response.Headers.ContentSecurityPolicy = $"default-src 'self'; script-src 'self' 'nonce-{nonce}' https://unpkg.com; style-src 'self' 'unsafe-inline' https://unpkg.com; img-src 'self' data: https://unpkg.com; connect-src 'self'; frame-ancestors 'none'; object-src 'none'; base-uri 'self';";
            return Results.Content(GetSwaggerUiHtml(nonce), "text/html;charset=utf-8");
        });

        app.MapGet("/docs", (HttpContext context, IWebHostEnvironment env) =>
        {
            if (!env.IsDevelopment() && context.User?.Identity?.IsAuthenticated != true)
            {
                return Results.Unauthorized();
            }
            var nonce = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
            context.Response.Headers.ContentSecurityPolicy = $"default-src 'self'; script-src 'self' 'nonce-{nonce}' https://unpkg.com; style-src 'self' 'unsafe-inline' https://unpkg.com; img-src 'self' data: https://unpkg.com; connect-src 'self'; frame-ancestors 'none'; object-src 'none'; base-uri 'self';";
            return Results.Content(GetSwaggerUiHtml(nonce), "text/html;charset=utf-8");
        });

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

        return app;
    }

    private static string GetSwaggerUiHtml(string nonce) => $$"""
    <!DOCTYPE html>
    <html lang="en">
    <head>
      <meta charset="utf-8" />
      <meta name="viewport" content="width=device-width, initial-scale=1" />
      <title>GqlGateway - OData v4 OpenAPI 3.1 Explorer</title>
      <link rel="stylesheet" href="https://unpkg.com/swagger-ui-dist@5.18.2/swagger-ui.css" crossorigin="anonymous" />
    </head>
    <body>
    <div id="swagger-ui"></div>
    <script src="https://unpkg.com/swagger-ui-dist@5.18.2/swagger-ui-bundle.js" crossorigin="anonymous"></script>
    <script nonce="{{nonce}}">
      window.onload = () => {
        window.ui = SwaggerUIBundle({
          url: '/odata/v4/$openapi',
          dom_id: '#swagger-ui',
          presets: [
            SwaggerUIBundle.presets.apis,
            SwaggerUIBundle.SwaggerUIStandalonePreset
          ],
          layout: "BaseLayout",
          deepLinking: true
        });
      };
    </script>
    </body>
    </html>
    """;
}
