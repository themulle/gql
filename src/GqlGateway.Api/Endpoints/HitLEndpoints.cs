namespace GqlGateway.Api.Endpoints;

using System;
using System.Security.Claims;
using GqlGateway.Application.Mcp.Interfaces;
using GqlGateway.Domain.Options;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

public static class HitLEndpoints
{
    public sealed record ApproveTicketRequest(string ApprovalId);
    public sealed record RejectTicketRequest(string ApprovalId, string? Reason);

    public static IEndpointRouteBuilder MapHitLEndpoints(this IEndpointRouteBuilder app)
    {
        // F-AI-05: HitL Step-Up Approval Endpoints
        app.MapGet("/api/governance/hitl/pending", (
            string? tenantId,
            IHitLStepUpApprovalService hitlService,
            IOptions<GatewayOptions> options) =>
        {
            if (!options.Value.HitLStepUp.Enabled)
            {
                return Results.NotFound(new { error = "HitL step-up approval is disabled." });
            }

            var tickets = hitlService.GetPendingTickets(tenantId);
            return Results.Ok(tickets);
        }).RequireAuthorization();

        app.MapPost("/api/governance/hitl/approve", (
            ApproveTicketRequest request,
            HttpContext context,
            IHitLStepUpApprovalService hitlService,
            IOptions<GatewayOptions> options) =>
        {
            if (!options.Value.HitLStepUp.Enabled)
            {
                return Results.NotFound(new { error = "HitL step-up approval is disabled." });
            }

            var approverSid = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                ?? context.User.Identity?.Name
                ?? "anonymous";

            var result = hitlService.ApproveStepUpRequest(request.ApprovalId, approverSid);
            return result.IsApproved ? Results.Ok(result) : Results.BadRequest(result);
        }).RequireAuthorization();

        app.MapPost("/api/governance/hitl/reject", (
            RejectTicketRequest request,
            HttpContext context,
            IHitLStepUpApprovalService hitlService,
            IOptions<GatewayOptions> options) =>
        {
            if (!options.Value.HitLStepUp.Enabled)
            {
                return Results.NotFound(new { error = "HitL step-up approval is disabled." });
            }

            var approverSid = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                ?? context.User.Identity?.Name
                ?? "anonymous";

            var result = hitlService.RejectStepUpRequest(request.ApprovalId, approverSid, request.Reason);
            return Results.Ok(result);
        }).RequireAuthorization();

        return app;
    }
}
