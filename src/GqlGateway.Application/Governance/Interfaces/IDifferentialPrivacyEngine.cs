namespace GqlGateway.Application.Governance.Interfaces;

using System.Threading;
using System.Threading.Tasks;
using GqlGateway.Domain.Model;

public interface IDifferentialPrivacyEngine
{
    ValueTask<PrivacyBudget> GetBudgetAsync(string clientId, CancellationToken cancellationToken = default);
    ValueTask ResetBudgetAsync(string clientId, CancellationToken cancellationToken = default);
    ValueTask<DifferentialPrivacyPerturbationResult> PerturbAsync(DifferentialPrivacyPerturbationRequest request, CancellationToken cancellationToken = default);
}
