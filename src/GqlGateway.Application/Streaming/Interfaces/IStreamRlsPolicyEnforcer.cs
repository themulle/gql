namespace GqlGateway.Application.Streaming.Interfaces;

using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using GqlGateway.Domain.Model;

public interface IStreamRlsPolicyEnforcer
{
    ValueTask<StreamSecurityDecision> EvaluateAndMaskAsync(
        CdcEvent cdcEvent,
        ClaimsPrincipal subscriber,
        CancellationToken ct = default);
}
