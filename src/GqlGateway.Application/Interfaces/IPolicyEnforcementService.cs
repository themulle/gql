namespace GqlGateway.Application.Interfaces;

using System.Threading;
using System.Threading.Tasks;
using GqlGateway.Domain.Common;
using GqlGateway.Domain.Interfaces;
using GqlGateway.Domain.Model;

public interface IPolicyEnforcementService
{
    /// <summary>
    /// Evaluates Casbin ABAC policy rules against the security evaluation context.
    /// Hot-path: Returns ValueTask for zero heap allocations on in-memory hits.
    /// Execution SLA: p99 <= 0.5 ms, p50 <= 0.1 ms with 50,000 active rules.
    /// </summary>
    ValueTask<TableAccessDecision> EvaluatePolicyAsync(
        SecurityEvaluationContext context,
        CancellationToken ct = default);

    /// <summary>
    /// Checks whether active Casbin ABAC policies are registered for the given tenant.
    /// </summary>
    bool HasPolicies(TenantId tenant);

    /// <summary>
    /// Synchronizes updated policies from Redis event bus invalidations (<= 50 ms).
    /// </summary>
    Task ReloadPoliciesAsync(TenantId tenant, CancellationToken ct = default);
}
