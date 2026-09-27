namespace GqlGateway.Infrastructure.Itsm;

using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GqlGateway.Application.Interfaces;
using GqlGateway.Domain.Common;
using GqlGateway.Domain.Diagnostics;
using GqlGateway.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public sealed class ItsmStatusChangeDto
{
    public string TicketId { get; set; } = string.Empty;
    public string InstanceId { get; set; } = string.Empty;
    public string Action { get; set; } = "APPROVE"; // "APPROVE", "REJECT"
    public string? Reason { get; set; }
}

public sealed class ItsmWebhookHandler(
    IKeyVaultSecretProvider secretProvider,
    IConsentApprovalRepository governanceRepo,
    IOptions<GatewayOptions> options,
    ILogger<ItsmWebhookHandler> logger,
    IEventBus? eventBus = null) : IItsmWebhookHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly ItsmOptions _itsmOptions = options.Value.Itsm;

    public async Task<bool> HandleStatusChangeAsync(
        string rawPayload,
        string hmacSignature,
        DateTimeOffset timestamp,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawPayload);
        ArgumentException.ThrowIfNullOrWhiteSpace(hmacSignature);

        // 1. Replay-Schutz: 5 Minuten Gültigkeitsfenster
        var diff = DateTimeOffset.UtcNow - timestamp;
        if (diff > TimeSpan.FromMinutes(5) || diff < TimeSpan.FromMinutes(-5))
        {
            logger.LogWarning("Webhook abgelehnt: Timestamp außerhalb des 5-Minuten-Gültigkeitsfensters.");
            return false;
        }

        // 2. Secret-Bezug aus dediziertem Key-Vault-Pfad (kein HMAC_SECRET Fallback!)
        byte[] secretKey;
        try
        {
            secretKey = secretProvider.GetSecretBytes("itsm:webhook-secret");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Fehler beim Laden des Webhook-Secrets 'itsm:webhook-secret'.");
            return false;
        }

        byte[] computedHashWithTimestamp = HMACSHA256.HashData(secretKey, Encoding.UTF8.GetBytes($"t={timestamp:O}.v1={rawPayload}"));
        byte[] computedHashRaw = HMACSHA256.HashData(secretKey, Encoding.UTF8.GetBytes(rawPayload));
        byte[] providedHash;
        try
        {
            providedHash = Convert.FromHexString(hmacSignature);
        }
        catch (FormatException)
        {
            logger.LogWarning("Webhook abgelehnt: Ungültiges Hex-Format der HMAC-SHA256-Signatur.");
            return false;
        }

        // 3. Timing-sicherer Signaturvergleich (Timestamp-gebunden oder Roh-Payload)
        bool signatureValid = CryptographicOperations.FixedTimeEquals(computedHashWithTimestamp, providedHash) ||
                              CryptographicOperations.FixedTimeEquals(computedHashRaw, providedHash);
        if (!signatureValid)
        {
            logger.LogWarning("Webhook abgelehnt: Ungültige HMAC-SHA256-Signatur.");
            return false;
        }

        // Payload parsen
        ItsmStatusChangeDto? payload;
        try
        {
            payload = JsonSerializer.Deserialize<ItsmStatusChangeDto>(rawPayload, JsonOptions);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Webhook abgelehnt: Ungültiges JSON-Payload.");
            return false;
        }

        if (payload == null || string.IsNullOrWhiteSpace(payload.TicketId))
        {
            logger.LogWarning("Webhook abgelehnt: TicketId fehlt.");
            return false;
        }

        var request = await governanceRepo.GetConsentRequestByTicketIdAsync(payload.TicketId, ct).ConfigureAwait(false);
        if (request == null)
        {
            logger.LogWarning("Webhook verworfen: Unbekannte TicketId '{TicketId}'", payload.TicketId);
            return false;
        }

        // 4. Strikte Tenant-Bindungsprüfung
        var expectedTenant = _itsmOptions.GetTenantForInstance(payload.InstanceId);
        if (expectedTenant == null || request.TenantId != expectedTenant.Value)
        {
            GatewayDiagnostics.CrossTenantMismatchCounter.Add(1);
            logger.LogError(
                "CROSS_TENANT_WEBHOOK_MISMATCH: Ticket {TicketId} gehört zu Tenant {ReqTenant}, Callback kam von {CbTenant}",
                payload.TicketId, request.TenantId, expectedTenant?.Value ?? "UNKNOWN_INSTANCE");
            return false; // Streng verweigern!
        }

        // 5. Idempotente Bearbeitung (nur PENDING_EXTERNAL_APPROVAL darf bearbeitet werden)
        if (!string.Equals(request.Status, "PENDING_EXTERNAL_APPROVAL", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogInformation("Webhook ignoriert: Request befindet sich bereits im Status '{Status}'", request.Status);
            return true;
        }

        if (string.Equals(payload.Action, "REJECT", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogInformation("Consent Request {RequestId} via ITSM Ticket {TicketId} abgelehnt.", request.Id, payload.TicketId);
            await governanceRepo.RejectConsentRequestAsync(request.Id, new Sid("ITSM_SYSTEM"), payload.Reason ?? "Rejected via ITSM webhook", ct).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(payload.Action, "APPROVE", StringComparison.OrdinalIgnoreCase))
        {
            await governanceRepo.ActivateConsentAsync(request.Id, ct).ConfigureAwait(false);

            if (eventBus != null)
            {
                await eventBus.PublishAsync($"governance:policy-epoch-increment:{request.TenantId.Value}", request.TenantId.Value, ct).ConfigureAwait(false);
            }

            return true;
        }

        logger.LogWarning("Webhook ignoriert: Unbekannte Action '{Action}'", payload.Action);
        return false;
    }
}
