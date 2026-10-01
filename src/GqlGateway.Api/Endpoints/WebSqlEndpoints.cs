namespace GqlGateway.Api.Endpoints;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Security;
using System.Text.Json;
using System.Threading.Tasks;
using GqlGateway.Application.Sql.Interfaces;
using GqlGateway.Domain.Common;
using GqlGateway.Domain.Options;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public static class WebSqlEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public sealed record WebSqlRequestDto(
        string? Sql,
        Dictionary<string, object?>? Parameters,
        string? DataSource);

    public static IEndpointRouteBuilder MapWebSqlEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/sql", HandleWebSqlRequest)
           .WithName("ExecuteGovernedWebSqlV1")
           .RequireAuthorization();

        app.MapPost("/api/sql", HandleWebSqlRequest)
           .WithName("ExecuteGovernedWebSql")
           .RequireAuthorization();

        return app;
    }

    private static async Task HandleWebSqlRequest(
        HttpContext httpContext,
        IGovernedSqlExecutionService sqlService,
        IOptions<GatewayOptions> gatewayOptions,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("GqlGateway.Api.WebSql");
        var ct = httpContext.RequestAborted;

        // Content Length validation
        if (httpContext.Request.ContentLength > 2 * 1024 * 1024)
        {
            httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            await httpContext.Response.WriteAsJsonAsync(new { error = "Request body exceeds maximum size limit (2 MB)." }, ct);
            return;
        }

        string? sql = null;
        Dictionary<string, object?>? parameters = null;
        string? dataSource = null;

        var contentType = httpContext.Request.ContentType ?? string.Empty;
        if (contentType.Contains("text/plain", StringComparison.OrdinalIgnoreCase) ||
            contentType.Contains("application/sql", StringComparison.OrdinalIgnoreCase))
        {
            using var reader = new StreamReader(httpContext.Request.Body);
            sql = await reader.ReadToEndAsync(ct);
        }
        else
        {
            try
            {
                var bodyDto = await JsonSerializer.DeserializeAsync<WebSqlRequestDto>(
                    httpContext.Request.Body,
                    JsonOptions,
                    ct);

                sql = bodyDto?.Sql;
                parameters = bodyDto?.Parameters;
                dataSource = bodyDto?.DataSource;
            }
            catch (JsonException ex)
            {
                httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
                await httpContext.Response.WriteAsJsonAsync(new { error = "Invalid JSON payload.", details = ex.Message }, ct);
                return;
            }
        }

        if (string.IsNullOrWhiteSpace(sql))
        {
            httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            await httpContext.Response.WriteAsJsonAsync(new { error = "Missing 'sql' query parameter in request body." }, ct);
            return;
        }

        // Resolve Tenant
        var user = httpContext.User;
        var tenantClaim = user.FindFirst("tenant_id")?.Value
            ?? user.FindFirst("tid")?.Value
            ?? user.FindFirst("tenant")?.Value;

        var tenantId = TenantId.TryParse(tenantClaim, out var tid)
            ? tid
            : TenantId.LegacySingleTenant;

        // Check if array format requested (?format=arrays)
        bool formatArrays = httpContext.Request.Query.TryGetValue("format", out var formatVal) &&
                            string.Equals(formatVal.ToString(), "arrays", StringComparison.OrdinalIgnoreCase);

        var governedRequest = new GovernedSqlQueryRequest(sql, parameters, dataSource);

        try
        {
            httpContext.Response.StatusCode = StatusCodes.Status200OK;
            httpContext.Response.ContentType = "application/json; charset=utf-8";

            await using var writer = new Utf8JsonWriter(httpContext.Response.Body);
            writer.WriteStartObject();

            int rowCount = 0;
            string[] columnNames = Array.Empty<string>();

            await sqlService.ExecuteGovernedQueryAsync(
                governedRequest,
                user,
                tenantId,
                async (reader, token) =>
                {
                    int fieldCount = reader.FieldCount;
                    columnNames = new string[fieldCount];
                    for (int i = 0; i < fieldCount; i++)
                    {
                        columnNames[i] = reader.GetName(i);
                    }

                    // Write "columns": [...]
                    writer.WriteStartArray("columns");
                    for (int i = 0; i < fieldCount; i++)
                    {
                        writer.WriteStringValue(columnNames[i]);
                    }
                    writer.WriteEndArray();

                    // Write "rows": [...]
                    writer.WriteStartArray("rows");

                    while (await reader.ReadAsync(token))
                    {
                        rowCount++;
                        if (formatArrays)
                        {
                            writer.WriteStartArray();
                            for (int i = 0; i < fieldCount; i++)
                            {
                                WriteDbValue(writer, reader.IsDBNull(i) ? null : reader.GetValue(i));
                            }
                            writer.WriteEndArray();
                        }
                        else
                        {
                            writer.WriteStartObject();
                            for (int i = 0; i < fieldCount; i++)
                            {
                                writer.WritePropertyName(columnNames[i]);
                                WriteDbValue(writer, reader.IsDBNull(i) ? null : reader.GetValue(i));
                            }
                            writer.WriteEndObject();
                        }

                        // Flush writer periodically to maintain streaming response for large row sets
                        if (rowCount % 250 == 0)
                        {
                            await writer.FlushAsync(token);
                        }
                    }

                    writer.WriteEndArray();
                },
                ct);

            writer.WriteNumber("rowCount", rowCount);
            writer.WriteEndObject();
            await writer.FlushAsync(ct);
            await httpContext.Response.Body.FlushAsync(ct);
        }
        catch (SecurityException secEx)
        {
            logger.LogWarning(secEx, "WebSQL Security Violation: {Message}", secEx.Message);
            if (!httpContext.Response.HasStarted)
            {
                httpContext.Response.StatusCode = StatusCodes.Status403Forbidden;
                httpContext.Response.ContentType = "application/json; charset=utf-8";
                await httpContext.Response.WriteAsJsonAsync(new
                {
                    error = "Forbidden",
                    message = secEx.Message
                }, ct);
            }
        }
        catch (ArgumentException argEx)
        {
            logger.LogWarning(argEx, "WebSQL Bad Request: {Message}", argEx.Message);
            if (!httpContext.Response.HasStarted)
            {
                httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
                httpContext.Response.ContentType = "application/json; charset=utf-8";
                await httpContext.Response.WriteAsJsonAsync(new
                {
                    error = "BadRequest",
                    message = argEx.Message
                }, ct);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "WebSQL Execution Failed: {Message}", ex.Message);
            if (!httpContext.Response.HasStarted)
            {
                httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;
                httpContext.Response.ContentType = "application/json; charset=utf-8";
                await httpContext.Response.WriteAsJsonAsync(new
                {
                    error = "InternalServerError",
                    message = ex.Message
                }, ct);
            }
        }
    }

    private static void WriteDbValue(Utf8JsonWriter writer, object? val)
    {
        if (val is null or DBNull)
        {
            writer.WriteNullValue();
            return;
        }

        switch (val)
        {
            case bool b:
                writer.WriteBooleanValue(b);
                break;
            case byte by:
                writer.WriteNumberValue(by);
                break;
            case short s:
                writer.WriteNumberValue(s);
                break;
            case int i:
                writer.WriteNumberValue(i);
                break;
            case long l:
                writer.WriteNumberValue(l);
                break;
            case float f:
                writer.WriteNumberValue(f);
                break;
            case double d:
                writer.WriteNumberValue(d);
                break;
            case decimal dec:
                writer.WriteNumberValue(dec);
                break;
            case DateTime dt:
                writer.WriteStringValue(dt.ToString("o"));
                break;
            case DateTimeOffset dto:
                writer.WriteStringValue(dto.ToString("o"));
                break;
            case Guid g:
                writer.WriteStringValue(g.ToString());
                break;
            case byte[] bytes:
                writer.WriteBase64StringValue(bytes);
                break;
            default:
                writer.WriteStringValue(val.ToString());
                break;
        }
    }
}
