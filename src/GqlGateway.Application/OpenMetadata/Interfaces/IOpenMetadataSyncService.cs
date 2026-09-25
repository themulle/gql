using GqlGateway.Application.OpenMetadata.Models;

namespace GqlGateway.Application.OpenMetadata.Interfaces;

public interface IOpenMetadataSyncService
{
    Task<OpenMetadataSyncResult> SyncPermissionsAsync(bool dryRun = false, CancellationToken ct = default);
    Task<bool> HandleWebhookEventAsync(string eventPayload, string? signatureHeader = null, CancellationToken ct = default);
}
