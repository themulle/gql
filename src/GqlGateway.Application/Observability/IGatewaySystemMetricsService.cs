namespace GqlGateway.Application.Observability;

using System.Threading;
using System.Threading.Tasks;
using GqlGateway.Domain.Model;

public interface IGatewaySystemMetricsService
{
    Task<GatewaySystemMetrics> CollectSystemMetricsAsync(CancellationToken cancellationToken = default);
}
