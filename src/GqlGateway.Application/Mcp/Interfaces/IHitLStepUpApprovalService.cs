namespace GqlGateway.Application.Mcp.Interfaces;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GqlGateway.Domain.Common;
using GqlGateway.Domain.Model;

public interface IHitLStepUpApprovalService
{
    Task<HitLApprovalResult> RequestStepUpApprovalAsync(
        string toolName,
        string tenantId,
        string requesterSid,
        TableIdentifier targetTable,
        string? justification = null,
        CancellationToken ct = default);

    HitLApprovalResult ApproveStepUpRequest(string approvalId, string approverSid);

    HitLApprovalResult RejectStepUpRequest(string approvalId, string approverSid, string? reason = null);

    HitLApprovalTicket? GetTicket(string approvalId);

    IReadOnlyList<HitLApprovalTicket> GetPendingTickets(string? tenantId = null);
}
