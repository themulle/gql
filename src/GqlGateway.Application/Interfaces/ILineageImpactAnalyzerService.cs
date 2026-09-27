namespace GqlGateway.Application.Interfaces;

using System;
using System.Threading;
using System.Threading.Tasks;
using GqlGateway.Domain.Common;
using GqlGateway.Domain.Model;

public interface ILineageImpactAnalyzerService
{
    /// <summary>
    /// Traversiert den Abhängigkeitsgraphen zyklensicher (iterativ mit visited-Set).
    /// Maskiert ownerEmail bei unzureichender Berechtigung. SLA: 10.000 Knoten in p99 <= 15 ms.
    /// </summary>
    Task<ConsentRevocationImpactReport> CalculateConsentRevocationImpactAsync(
        TenantId tenant,
        Guid consentId,
        CallerSecurityContext callerContext,
        CancellationToken ct = default);
}
