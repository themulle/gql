namespace GqlGateway.Application.Governance.Interfaces;

using System.Threading;
using System.Threading.Tasks;
using GqlGateway.Domain.Model;

public interface IPolicySimulationService
{
    Task<PolicySimulationResult> SimulateAsync(PolicySimulationRequest request, CancellationToken cancellationToken = default);
}
