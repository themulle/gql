namespace GqlGateway.Api.Middleware;

using System;
using System.Threading.Tasks;
using GqlGateway.Domain.Common;
using Microsoft.AspNetCore.Http;

public sealed class TenantResolutionMiddleware(RequestDelegate next)
{
    public const string TenantIdItemKey = "TenantId";
    public const string TenantHeaderName = "X-Tenant-ID";
    public const string TenantHeaderNameAlt = "X-Tenant-Id";

    public async Task InvokeAsync(HttpContext context)
    {
        string? rawTenant = null;

        if (context.Request.Headers.TryGetValue(TenantHeaderName, out var headerVal) && !string.IsNullOrWhiteSpace(headerVal))
        {
            rawTenant = headerVal.ToString();
        }
        else if (context.Request.Headers.TryGetValue(TenantHeaderNameAlt, out var headerValAlt) && !string.IsNullOrWhiteSpace(headerValAlt))
        {
            rawTenant = headerValAlt.ToString();
        }
        else if (context.User.Identity?.IsAuthenticated == true)
        {
            rawTenant = context.User.FindFirst("tenant")?.Value
                ?? context.User.FindFirst("tid")?.Value
                ?? context.User.FindFirst("tenant_id")?.Value;
        }

        TenantId tenantId;
        if (!string.IsNullOrWhiteSpace(rawTenant))
        {
            try
            {
                tenantId = new TenantId(rawTenant);
            }
            catch (ArgumentException ex)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync($"{{\"errors\":[{{\"message\":\"{ex.Message}\",\"code\":\"INVALID_TENANT_ID\"}}]}}");
                return;
            }
        }
        else
        {
            tenantId = TenantId.LegacySingleTenant;
        }

        context.Items[TenantIdItemKey] = tenantId;
        await next(context);
    }
}
