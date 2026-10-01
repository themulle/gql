namespace GqlGateway.Api.Endpoints;

using System;
using System.Linq;
using System.Security.Claims;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using GqlGateway.Api.Middleware;
using GqlGateway.Application.Mcp.Interfaces;
using GqlGateway.Application.Mcp.Services;
using GqlGateway.Domain.Common;
using GqlGateway.Domain.Model;
using GqlGateway.Domain.Options;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

public static class McpEndpoints
{
    public static IEndpointRouteBuilder MapMcpEndpoints(this IEndpointRouteBuilder app, GatewayOptions gatewayOptions)
    {
        if (!gatewayOptions.Mcp.Enabled)
        {
            return app;
        }

        var mcpBasePath = string.IsNullOrWhiteSpace(gatewayOptions.Mcp.EndpointPath)
            ? "/mcp"
            : gatewayOptions.Mcp.EndpointPath.TrimEnd('/');

        // 1. SSE Connection Handshake
        var sseEndpoint = app.MapGet($"{mcpBasePath}/sse", async (
            IMcpProtocolHandler mcpHandler,
            IMcpSessionStore sessionStore,
            HttpContext context) =>
        {
            var principal = context.User;
            var isAuthenticated = principal.Identity?.IsAuthenticated == true;

            if (!isAuthenticated && !gatewayOptions.IsMcpAuthBypassed)
            {
                return Results.Unauthorized();
            }

            var principalId = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value
                ?? principal.Identity?.Name
                ?? (gatewayOptions.IsMcpAuthBypassed ? "anonymous-ai-agent" : "unknown-agent");

            string tenantId;
            if (context.Items.TryGetValue(TenantResolutionMiddleware.TenantIdItemKey, out var itemTenant) && itemTenant is TenantId tId)
            {
                tenantId = tId.Value;
            }
            else
            {
                tenantId = principal.GetTenantId().Value;
            }

            var userSid = principal.GetUserSid()?.Value;
            var roles = principal.GetUserRoles().ToList();
            var groupSids = principal.GetGroupSids().Select(s => s.Value).ToList();

            var session = mcpHandler.CreateSession(principalId, tenantId, userSid, roles, groupSids);

            sessionStore.RegisterSseSender(session.SessionId, async (evt, data) =>
            {
                var cleanEvt = string.IsNullOrWhiteSpace(evt) ? "message" : Regex.Replace(evt, @"[\r\n]", string.Empty);
                var normalizedData = (data ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
                var lines = normalizedData.Split('\n');
                var sb = new System.Text.StringBuilder();
                sb.Append("event: ").Append(cleanEvt).Append('\n');
                foreach (var line in lines)
                {
                    sb.Append("data: ").Append(line).Append('\n');
                }
                sb.Append('\n');
                await context.Response.WriteAsync(sb.ToString(), context.RequestAborted).ConfigureAwait(false);
                await context.Response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);
            });

            context.Response.Headers.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache";
            context.Response.Headers.Connection = "keep-alive";

            var messageUri = $"{mcpBasePath}/message?sessionId={session.SessionId}";
            await context.Response.WriteAsync($"event: endpoint\r\ndata: {messageUri}\r\n\r\n", context.RequestAborted).ConfigureAwait(false);
            await context.Response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);

            try
            {
                while (!context.RequestAborted.IsCancellationRequested)
                {
                    await Task.Delay(15000, context.RequestAborted).ConfigureAwait(false);
                    await context.Response.WriteAsync(": ping\r\n\r\n", context.RequestAborted).ConfigureAwait(false);
                    await context.Response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Normal client disconnect
            }
            finally
            {
                mcpHandler.RemoveSession(session.SessionId);
            }

            return Results.Empty;
        });

        if (!gatewayOptions.IsMcpAuthBypassed)
        {
            sseEndpoint.RequireAuthorization();
        }

        // 2. JSON-RPC Message Receiver
        var messageEndpoint = app.MapPost($"{mcpBasePath}/message", async (
            IMcpProtocolHandler mcpHandler,
            HttpContext context) =>
        {
            var sessionId = context.Request.Query["sessionId"].FirstOrDefault()
                ?? context.Request.Headers["X-MCP-Session-Id"].FirstOrDefault();

            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return Results.BadRequest(new { error = "Missing 'sessionId' query parameter or 'X-MCP-Session-Id' header." });
            }

            var session = mcpHandler.GetSession(sessionId);
            if (session == null)
            {
                return Results.NotFound(new { error = $"Invalid or expired MCP session '{sessionId}'." });
            }

            if (!gatewayOptions.IsMcpAuthBypassed)
            {
                var callerId = context.User.FindFirst("client_id")?.Value
                    ?? context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                    ?? context.User.FindFirst("sub")?.Value
                    ?? context.User.FindFirst("appid")?.Value
                    ?? context.User.Identity?.Name;

                if (string.IsNullOrWhiteSpace(callerId) ||
                    !string.Equals(session.ServicePrincipalId, callerId, StringComparison.OrdinalIgnoreCase))
                {
                    return Results.StatusCode(StatusCodes.Status403Forbidden);
                }
            }

            using var reader = new System.IO.StreamReader(context.Request.Body);
            var payload = await reader.ReadToEndAsync(context.RequestAborted).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(payload))
            {
                return Results.BadRequest(new { error = "Empty JSON-RPC payload." });
            }

            var responseJson = await mcpHandler.HandleMessageAsync(sessionId, payload, context.RequestAborted).ConfigureAwait(false);

            return Results.Content(responseJson, "application/json; charset=utf-8");
        });

        if (!gatewayOptions.IsMcpAuthBypassed)
        {
            messageEndpoint.RequireAuthorization();
        }

        // 2b. Streamable HTTP Transport (MCP 2024-11-05 Specification)
        var streamableHttpEndpoint = app.MapPost(mcpBasePath, async (
            IMcpProtocolHandler mcpHandler,
            HttpContext context) =>
        {
            var sessionId = context.Request.Headers["X-MCP-Session-Id"].FirstOrDefault()
                ?? context.Request.Headers["Mcp-Session-Id"].FirstOrDefault()
                ?? context.Request.Query["sessionId"].FirstOrDefault();

            McpSessionContext? session = null;
            if (!string.IsNullOrWhiteSpace(sessionId))
            {
                session = mcpHandler.GetSession(sessionId);
                if (session != null && !gatewayOptions.IsMcpAuthBypassed)
                {
                    var callerId = context.User.FindFirst("client_id")?.Value
                        ?? context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                        ?? context.User.FindFirst("sub")?.Value
                        ?? context.User.FindFirst("appid")?.Value
                        ?? context.User.Identity?.Name;

                    if (string.IsNullOrWhiteSpace(callerId) ||
                        !string.Equals(session.ServicePrincipalId, callerId, StringComparison.OrdinalIgnoreCase))
                    {
                        return Results.StatusCode(StatusCodes.Status403Forbidden);
                    }
                }
            }

            if (session == null)
            {
                var principal = context.User;
                var principalId = principal.FindFirst("client_id")?.Value
                    ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value
                    ?? principal.FindFirst("sub")?.Value
                    ?? principal.FindFirst("appid")?.Value
                    ?? principal.Identity?.Name
                    ?? (gatewayOptions.IsMcpAuthBypassed ? "anonymous-ai-agent" : "cli-developer");

                string tenantId;
                if (context.Items.TryGetValue(TenantResolutionMiddleware.TenantIdItemKey, out var itemTenant) && itemTenant is TenantId tId)
                {
                    tenantId = tId.Value;
                }
                else
                {
                    tenantId = principal.GetTenantId().Value;
                }

                var userSid = principal.GetUserSid()?.Value;
                var roles = principal.GetUserRoles().ToList();
                var groupSids = principal.GetGroupSids().Select(s => s.Value).ToList();

                session = mcpHandler.CreateSession(principalId, tenantId, userSid, roles, groupSids);
            }

            using var reader = new System.IO.StreamReader(context.Request.Body);
            var payload = await reader.ReadToEndAsync(context.RequestAborted).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(payload))
            {
                return Results.BadRequest(new { error = "Empty JSON-RPC payload." });
            }

            context.Response.Headers["X-MCP-Session-Id"] = session.SessionId;
            context.Response.Headers["Mcp-Session-Id"] = session.SessionId;

            var responseJson = await mcpHandler.HandleMessageAsync(session.SessionId, payload, context.RequestAborted).ConfigureAwait(false);
            return Results.Content(responseJson, "application/json; charset=utf-8");
        });

        if (!gatewayOptions.IsMcpAuthBypassed)
        {
            streamableHttpEndpoint.RequireAuthorization();
        }

        // 3. Session Teardown
        var sessionEndpoint = app.MapDelete($"{mcpBasePath}/session/{{id}}", (
            string id,
            IMcpProtocolHandler mcpHandler,
            HttpContext context) =>
        {
            var session = mcpHandler.GetSession(id);
            if (session == null)
            {
                return Results.NotFound(new { error = $"Session '{id}' not found." });
            }

            if (!gatewayOptions.IsMcpAuthBypassed)
            {
                var callerId = context.User.FindFirst("client_id")?.Value
                    ?? context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                    ?? context.User.FindFirst("sub")?.Value
                    ?? context.User.FindFirst("appid")?.Value
                    ?? context.User.Identity?.Name;

                if (string.IsNullOrWhiteSpace(callerId) ||
                    !string.Equals(session.ServicePrincipalId, callerId, StringComparison.OrdinalIgnoreCase))
                {
                    return Results.StatusCode(StatusCodes.Status403Forbidden);
                }
            }

            var removed = mcpHandler.RemoveSession(id);
            return removed ? Results.NoContent() : Results.NotFound();
        });

        if (!gatewayOptions.IsMcpAuthBypassed)
        {
            sessionEndpoint.RequireAuthorization();
        }

        return app;
    }
}
