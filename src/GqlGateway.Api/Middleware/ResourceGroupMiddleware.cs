namespace GqlGateway.Api.Middleware;

using System;
using System.Threading.Tasks;
using GqlGateway.Application.ResourceGroups;
using GqlGateway.Domain.Model;
using GqlGateway.Domain.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public sealed class ResourceGroupMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IResourceGroupManager _resourceGroupManager;
    private readonly ILogger<ResourceGroupMiddleware> _logger;
    private readonly bool _enabled;

    public ResourceGroupMiddleware(
        RequestDelegate next,
        IResourceGroupManager resourceGroupManager,
        IOptions<GatewayOptions> options,
        ILogger<ResourceGroupMiddleware> logger)
    {
        _next = next;
        _resourceGroupManager = resourceGroupManager;
        _logger = logger;
        _enabled = options.Value.ResourceGroups?.Enabled ?? true;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!_enabled)
        {
            await _next(context);
            return;
        }

        var path = context.Request.Path;

        // Skip system, metrics, and health endpoints so observability is never starved
        if (path.StartsWithSegments("/health") ||
            path.StartsWithSegments("/metrics") ||
            path.StartsWithSegments("/api/governance/system"))
        {
            await _next(context);
            return;
        }

        // Classify workload tier
        var tier = ClassifyWorkloadTier(context);

        // Resolve Tenant
        var tenantId = context.Items.TryGetValue("TenantId", out var tObj) && tObj is string tStr && !string.IsNullOrWhiteSpace(tStr)
            ? tStr
            : context.User.FindFirst("tenant_id")?.Value ?? "default";

        var leaseResult = await _resourceGroupManager.TryAcquireLeaseAsync(tier, tenantId, context.RequestAborted).ConfigureAwait(false);

        if (!leaseResult.Success)
        {
            context.Response.Headers["X-Resource-Group-Tier"] = tier.ToString();

            if (string.Equals(leaseResult.RejectionReason, "QueueFull", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                context.Response.Headers["Retry-After"] = "5";
                await context.Response.WriteAsJsonAsync(new
                {
                    errors = new[]
                    {
                        new
                        {
                            message = $"Resource group queue full for tier '{tier}'. Please retry later.",
                            extensions = new
                            {
                                code = "RESOURCE_GROUP_QUEUE_FULL",
                                tier = tier.ToString()
                            }
                        }
                    }
                }, cancellationToken: context.RequestAborted);
                return;
            }

            // Timed out waiting in queue
            context.Response.StatusCode = StatusCodes.Status504GatewayTimeout;
            await context.Response.WriteAsJsonAsync(new
            {
                errors = new[]
                {
                    new
                    {
                        message = $"Resource group lease acquisition timed out for tier '{tier}'.",
                        extensions = new
                        {
                            code = "RESOURCE_GROUP_TIMEOUT",
                            tier = tier.ToString()
                        }
                    }
                }
            }, cancellationToken: context.RequestAborted);
            return;
        }

        context.Response.Headers["X-Resource-Group-Tier"] = tier.ToString();

        if (leaseResult.Lease != null)
        {
            await using (leaseResult.Lease.ConfigureAwait(false))
            {
                await _next(context);
            }
        }
        else
        {
            await _next(context);
        }
    }

    private static ResourceGroupTier ClassifyWorkloadTier(HttpContext context)
    {
        // 1. Explicit Client Header
        if (context.Request.Headers.TryGetValue("X-Workload-Tier", out var headerVal))
        {
            var headerStr = headerVal.ToString().Trim();
            if (headerStr.Equals("AutonomousAgent", StringComparison.OrdinalIgnoreCase) ||
                headerStr.Equals("AutonomousAgents", StringComparison.OrdinalIgnoreCase) ||
                headerStr.Equals("Agent", StringComparison.OrdinalIgnoreCase))
            {
                return ResourceGroupTier.AutonomousAgents;
            }

            if (headerStr.Equals("BulkAnalytics", StringComparison.OrdinalIgnoreCase) ||
                headerStr.Equals("Analytics", StringComparison.OrdinalIgnoreCase) ||
                headerStr.Equals("Bulk", StringComparison.OrdinalIgnoreCase))
            {
                return ResourceGroupTier.BulkAnalytics;
            }

            if (headerStr.Equals("Interactive", StringComparison.OrdinalIgnoreCase))
            {
                return ResourceGroupTier.Interactive;
            }
        }

        // 2. Path-based classification
        var path = context.Request.Path;
        if (path.StartsWithSegments("/mcp"))
        {
            return ResourceGroupTier.AutonomousAgents;
        }

        if (path.StartsWithSegments("/odata"))
        {
            return ResourceGroupTier.BulkAnalytics;
        }

        // 3. Default to Interactive
        return ResourceGroupTier.Interactive;
    }
}
