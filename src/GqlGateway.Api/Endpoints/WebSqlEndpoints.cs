namespace GqlGateway.Api.Endpoints;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Security;
using System.Text.Json;
using System.Threading.Tasks;
using GqlGateway.Application.Sql;
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

    public const string GenericForbiddenMessage = "The SQL statement was rejected by the gateway security policy.";
    public const string GenericBadRequestMessage = "The SQL request is invalid (syntax error, empty statement or size limit exceeded).";
    public const string GenericServerErrorMessage = "The SQL statement could not be executed. Contact support with the trace id.";

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

    internal static async Task HandleWebSqlRequest(
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
                // SEC M-10: Parser details stay in the server log
                logger.LogWarning(ex, "WebSQL request body is not valid JSON. TraceId={TraceId}", httpContext.TraceIdentifier);
                httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
                await httpContext.Response.WriteAsJsonAsync(new { error = "Invalid JSON payload.", traceId = httpContext.TraceIdentifier }, ct);
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

        // SEC M-10: The JSON writer is created lazily when the first result arrives. Policy/parse errors raised while the
        // statement is governed therefore never start the response, so the 4xx/5xx status and the curated body can still be sent.
        Utf8JsonWriter? writer = null;
        try
        {
            int rowCount = 0;
            string[] columnNames = Array.Empty<string>();

            await sqlService.ExecuteGovernedQueryAsync(
                governedRequest,
                user,
                tenantId,
                async (reader, token) =>
                {
                    httpContext.Response.StatusCode = StatusCodes.Status200OK;
                    httpContext.Response.ContentType = "application/json; charset=utf-8";

                    var w = new Utf8JsonWriter(httpContext.Response.Body);
                    writer = w;
                    w.WriteStartObject();

                    int fieldCount = reader.FieldCount;
                    columnNames = new string[fieldCount];
                    for (int i = 0; i < fieldCount; i++)
                    {
                        columnNames[i] = reader.GetName(i);
                    }

                    // Write "columns": [...]
                    w.WriteStartArray("columns");
                    for (int i = 0; i < fieldCount; i++)
                    {
                        w.WriteStringValue(columnNames[i]);
                    }
                    w.WriteEndArray();

                    // Write "rows": [...]
                    w.WriteStartArray("rows");

                    while (await reader.ReadAsync(token))
                    {
                        rowCount++;
                        if (formatArrays)
                        {
                            w.WriteStartArray();
                            for (int i = 0; i < fieldCount; i++)
                            {
                                WriteDbValue(w, reader.IsDBNull(i) ? null : reader.GetValue(i));
                            }
                            w.WriteEndArray();
                        }
                        else
                        {
                            w.WriteStartObject();
                            for (int i = 0; i < fieldCount; i++)
                            {
                                w.WritePropertyName(columnNames[i]);
                                WriteDbValue(w, reader.IsDBNull(i) ? null : reader.GetValue(i));
                            }
                            w.WriteEndObject();
                        }

                        // Flush writer periodically to maintain streaming response for large row sets
                        if (rowCount % 250 == 0)
                        {
                            await w.FlushAsync(token);
                        }
                    }

                    w.WriteEndArray();
                },
                ct);

            if (writer == null)
            {
                // No result set was produced: emit an empty, well-formed result
                httpContext.Response.StatusCode = StatusCodes.Status200OK;
                httpContext.Response.ContentType = "application/json; charset=utf-8";
                writer = new Utf8JsonWriter(httpContext.Response.Body);
                writer.WriteStartObject();
                writer.WriteStartArray("columns");
                writer.WriteEndArray();
                writer.WriteStartArray("rows");
                writer.WriteEndArray();
            }

            writer.WriteNumber("rowCount", rowCount);
            writer.WriteEndObject();
            await writer.FlushAsync(ct);
            await httpContext.Response.Body.FlushAsync(ct);
        }
        catch (SecurityException secEx)
        {
            // SEC M-10: Only curated policy messages (WebSqlPolicyException) are returned to the client
            logger.LogWarning(secEx, "WebSQL Security Violation. TraceId={TraceId}", httpContext.TraceIdentifier);
            if (!httpContext.Response.HasStarted)
            {
                httpContext.Response.StatusCode = StatusCodes.Status403Forbidden;
                httpContext.Response.ContentType = "application/json; charset=utf-8";
                await httpContext.Response.WriteAsJsonAsync(new
                {
                    error = "Forbidden",
                    message = secEx is WebSqlPolicyException ? secEx.Message : GenericForbiddenMessage,
                    traceId = httpContext.TraceIdentifier
                }, ct);
            }
        }
        catch (ArgumentException argEx)
        {
            logger.LogWarning(argEx, "WebSQL Bad Request. TraceId={TraceId}", httpContext.TraceIdentifier);
            if (!httpContext.Response.HasStarted)
            {
                httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
                httpContext.Response.ContentType = "application/json; charset=utf-8";
                await httpContext.Response.WriteAsJsonAsync(new
                {
                    error = "BadRequest",
                    message = GenericBadRequestMessage,
                    traceId = httpContext.TraceIdentifier
                }, ct);
            }
        }
        catch (Exception ex)
        {
            // SEC M-10: Never echo exception/database messages (schema, table or server names) to the client
            logger.LogError(ex, "WebSQL Execution Failed. TraceId={TraceId}", httpContext.TraceIdentifier);
            if (!httpContext.Response.HasStarted)
            {
                httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;
                httpContext.Response.ContentType = "application/json; charset=utf-8";
                await httpContext.Response.WriteAsJsonAsync(new
                {
                    error = "InternalServerError",
                    message = GenericServerErrorMessage,
                    traceId = httpContext.TraceIdentifier
                }, ct);
            }
        }
        finally
        {
            if (writer != null)
            {
                await writer.DisposeAsync();
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
