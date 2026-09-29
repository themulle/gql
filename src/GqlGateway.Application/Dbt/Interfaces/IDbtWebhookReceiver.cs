namespace GqlGateway.Application.Dbt.Interfaces;

using System.Threading;
using System.Threading.Tasks;
using GqlGateway.Domain.Model;

public interface IDbtWebhookReceiver
{
    bool ValidateSignature(string payload, string? signatureHeader, string secret);
    Task<DbtWebhookProcessingResult> ProcessWebhookAsync(string payload, string? signatureHeader, CancellationToken ct = default);
}
