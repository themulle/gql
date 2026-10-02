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

        bool IsOpenApiAuthorized(HttpContext context)
        {
            if (gatewayOptions.IsOpenSchemaAllowed)
            {
                return true;
            }
            if (context.User?.Identity?.IsAuthenticated != true)
            {
                return false;
            }
            var roles = context.User.GetUserRoles();
            return roles.Contains("GovernanceAdmin") ||
                   roles.Contains("ClusterAdmin") ||
                   roles.Contains("DataOwner") ||
                   roles.Contains("SchemaAdmin") ||
                   roles.Contains("CatalogReader");
        }

        IResult? CheckOpenApiAuth(HttpContext context)
        {
            if (IsOpenApiAuthorized(context))
            {
                return null;
            }
            return context.User?.Identity?.IsAuthenticated == true ? Results.Forbid() : Results.Unauthorized();
        }

        RouteHandlerBuilder ConfigureOpenApiAuth(RouteHandlerBuilder builder)
        {
            if (!gatewayOptions.IsOpenSchemaAllowed)
            {
                builder.RequireAuthorization();
            }
            else
            {
                // SEC M-03: explicit opt-out of the authenticated-user fallback policy (OpenSchema docs only)
                builder.AllowAnonymous();
            }
            return builder;
        }

        // Dynamic OpenAPI 3.1 & Swagger UI Explorer (F-API-03)
        ConfigureOpenApiAuth(app.MapGet("/odata/v4/$openapi", async (
            IDynamicOpenApiGenerator generator,
            IOpenApiCacheManager cacheManager,
            HttpContext context) =>
        {
            var authCheck = CheckOpenApiAuth(context);
            if (authCheck != null) return authCheck;

            var format = context.Request.Query["format"].ToString();
            var accept = context.Request.Headers.Accept.ToString();
            bool isYaml = string.Equals(format, "yaml", StringComparison.OrdinalIgnoreCase) ||
                          accept.Contains("application/yaml", StringComparison.OrdinalIgnoreCase);

            var mode = context.Request.Query["mode"].ToString();
            bool isModular = string.Equals(mode, "modular", StringComparison.OrdinalIgnoreCase);

            var bytes = await cacheManager.GetOrAddAsync(
                domainScope: null,
                isYaml: isYaml,
                isModular: isModular,
                factory: ct => isYaml
                    ? generator.GenerateOpenApiYamlAsync(null, isModular, ct)
                    : generator.GenerateOpenApiJsonAsync(null, isModular, ct),
                ct: context.RequestAborted);

            var contentType = isYaml ? "application/yaml;charset=utf-8" : "application/json;charset=utf-8";
            return Results.Bytes(bytes, contentType: contentType);
        }));

        // OpenAPI Catalog Index Endpoint (Lists all available domain slices & API specs)
        ConfigureOpenApiAuth(app.MapGet("/odata/v4/$openapi/index", async (
            IDynamicOpenApiGenerator generator,
            HttpContext context) =>
        {
            var authCheck = CheckOpenApiAuth(context);
            if (authCheck != null) return authCheck;

            var serviceRoot = $"{context.Request.Scheme}://{context.Request.Host}/odata/v4";
            var indexDoc = await generator.GetIndexDocumentAsync(serviceRoot, context.RequestAborted);
            return Results.Json(indexDoc, contentType: "application/json;charset=utf-8");
        }));

        ConfigureOpenApiAuth(app.MapGet("/api/v1/openapi/index", async (
            IDynamicOpenApiGenerator generator,
            HttpContext context) =>
        {
            var authCheck = CheckOpenApiAuth(context);
            if (authCheck != null) return authCheck;

            var serviceRoot = $"{context.Request.Scheme}://{context.Request.Host}/odata/v4";
            var indexDoc = await generator.GetIndexDocumentAsync(serviceRoot, context.RequestAborted);
            return Results.Json(indexDoc, contentType: "application/json;charset=utf-8");
        }));

        // Isolated Entity Schema Endpoint ($ref target for modular OpenAPI specifications)
        ConfigureOpenApiAuth(app.MapGet("/odata/v4/$openapi/schemas/{domain}/{schema}/{tableName}", async (
            string domain,
            string schema,
            string tableName,
            IDynamicOpenApiGenerator generator,
            HttpContext context) =>
        {
            var authCheck = CheckOpenApiAuth(context);
            if (authCheck != null) return authCheck;

            var tableId = new TableIdentifier(domain, schema, tableName);
            var json = await generator.GenerateEntitySchemaJsonAsync(tableId, context.RequestAborted);
            if (json == null)
            {
                return Results.NotFound(new { error = $"Table '{domain}.{schema}.{tableName}' not found in metadata repository." });
            }

            return Results.Content(json, "application/json;charset=utf-8");
        }));

        ConfigureOpenApiAuth(app.MapGet("/odata/v4/{domain}/openapi.json", async (
            string domain,
            IDynamicOpenApiGenerator generator,
            IOpenApiCacheManager cacheManager,
            HttpContext context) =>
        {
            var authCheck = CheckOpenApiAuth(context);
            if (authCheck != null) return authCheck;

            var mode = context.Request.Query["mode"].ToString();
            bool isModular = string.Equals(mode, "modular", StringComparison.OrdinalIgnoreCase);

            var bytes = await cacheManager.GetOrAddAsync(
                domainScope: domain,
                isYaml: false,
                isModular: isModular,
                factory: ct => generator.GenerateOpenApiJsonAsync(domain, isModular, ct),
                ct: context.RequestAborted);

            return Results.Bytes(bytes, contentType: "application/json;charset=utf-8");
        }));

        ConfigureOpenApiAuth(app.MapGet("/odata/v4/{domain}/openapi.yaml", async (
            string domain,
            IDynamicOpenApiGenerator generator,
            IOpenApiCacheManager cacheManager,
            HttpContext context) =>
        {
            var authCheck = CheckOpenApiAuth(context);
            if (authCheck != null) return authCheck;

            var mode = context.Request.Query["mode"].ToString();
            bool isModular = string.Equals(mode, "modular", StringComparison.OrdinalIgnoreCase);

            var bytes = await cacheManager.GetOrAddAsync(
                domainScope: domain,
                isYaml: true,
                isModular: isModular,
                factory: ct => generator.GenerateOpenApiYamlAsync(domain, isModular, ct),
                ct: context.RequestAborted);

            return Results.Bytes(bytes, contentType: "application/yaml;charset=utf-8");
        }));

        app.MapGet("/odata/v4/$swagger", (HttpContext context, IWebHostEnvironment env) =>
        {
            if (!gatewayOptions.IsOpenSchemaAllowed && !env.IsDevelopment() && context.User?.Identity?.IsAuthenticated != true)
            {
                return Results.Unauthorized();
            }
            var nonce = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
            context.Response.Headers.ContentSecurityPolicy = $"default-src 'self'; script-src 'self' 'nonce-{nonce}' https://unpkg.com; style-src 'self' 'unsafe-inline' https://unpkg.com; img-src 'self' data: https://unpkg.com; connect-src 'self'; frame-ancestors 'none'; object-src 'none'; base-uri 'self';";
            return Results.Content(GetSwaggerUiHtml(nonce), "text/html;charset=utf-8");
        }).AllowAnonymous(); // SEC M-03: handler performs its own (OpenSchema/Dev/authenticated) check

        app.MapGet("/docs", (HttpContext context, IWebHostEnvironment env) =>
        {
            if (!gatewayOptions.IsOpenSchemaAllowed && !env.IsDevelopment() && context.User?.Identity?.IsAuthenticated != true)
            {
                return Results.Unauthorized();
            }
            var nonce = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
            context.Response.Headers.ContentSecurityPolicy = $"default-src 'self'; script-src 'self' 'nonce-{nonce}' https://unpkg.com; style-src 'self' 'unsafe-inline' https://unpkg.com; img-src 'self' data: https://unpkg.com; connect-src 'self'; frame-ancestors 'none'; object-src 'none'; base-uri 'self';";
            return Results.Content(GetSwaggerUiHtml(nonce), "text/html;charset=utf-8");
        }).AllowAnonymous(); // SEC M-03: handler performs its own (OpenSchema/Dev/authenticated) check

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
      <title>GqlGateway - OpenAPI 3.1 & OData Explorer</title>
      <link rel="stylesheet" href="https://unpkg.com/swagger-ui-dist@5.18.2/swagger-ui.css" crossorigin="anonymous" />
      <style>
        .swagger-ui .topbar { background-color: #1e293b; padding: 10px 0; }
        .swagger-ui .topbar .download-url-wrapper { display: flex; align-items: center; gap: 8px; }
        .swagger-ui .topbar .download-url-wrapper input[type=text] { border-radius: 4px; padding: 6px 10px; }
      </style>
    </head>
    <body>
    <div id="swagger-ui"></div>
    <script src="https://unpkg.com/swagger-ui-dist@5.18.2/swagger-ui-bundle.js" crossorigin="anonymous"></script>
    <script src="https://unpkg.com/swagger-ui-dist@5.18.2/swagger-ui-standalone-preset.js" crossorigin="anonymous"></script>
    <script nonce="{{nonce}}">
      window.onload = async () => {
        const params = new URLSearchParams(window.location.search);
        const targetDomain = params.get('domain');
        const customUrl = params.get('url');

        let specUrls = [
          { url: '/odata/v4/$openapi', name: 'All Domains (Monolithic)' },
          { url: '/odata/v4/$openapi?mode=modular', name: 'All Domains (Modular $ref)' },
          { url: '/api/v1/queries/openapi.json', name: 'Declarative SQL Queries' }
        ];
        let primaryName = 'All Domains (Monolithic)';

        try {
          const res = await fetch('/odata/v4/$openapi/index', { credentials: 'same-origin' });
          if (res.ok) {
            const data = await res.json();
            if (data && Array.isArray(data.apis)) {
              specUrls = data.apis.map(a => ({ url: a.url, name: a.name }));
            }
          }
        } catch (e) {
          // Graceful fallback to default URLs if unauthenticated or network error
        }

        if (targetDomain) {
          const match = specUrls.find(s => s.name.toLowerCase().includes(targetDomain.toLowerCase()) || s.url.toLowerCase().includes(`/${targetDomain.toLowerCase()}/`));
          if (match) {
            primaryName = match.name;
          }
        } else if (customUrl) {
          const match = specUrls.find(s => s.url === customUrl);
          if (match) {
            primaryName = match.name;
          } else {
            specUrls.unshift({ url: customUrl, name: 'Custom Specification' });
            primaryName = 'Custom Specification';
          }
        }

        window.ui = SwaggerUIBundle({
          urls: specUrls,
          "urls.primaryName": primaryName,
          dom_id: '#swagger-ui',
          presets: [
            SwaggerUIBundle.presets.apis,
            SwaggerUIStandalonePreset
          ],
          layout: "StandaloneLayout",
          deepLinking: true,
          displayRequestDuration: true
        });
      };
    </script>
    </body>
    </html>
    """;
}

