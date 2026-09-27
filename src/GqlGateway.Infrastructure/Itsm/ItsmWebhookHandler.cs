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
    public string System { get; set; } = "ITSM";
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

    public Task<bool> HandleStatusChangeAsync(
        string rawPayload,
        string hmacSignature,
        DateTimeOffset timestamp,
        CancellationToken ct = default)
        => HandleStatusChangeAsync(rawPayload, hmacSignature, timestamp, null, ct);

    public async Task<bool> HandleStatusChangeAsync(
        string rawPayload,
        string hmacSignature,
        DateTimeOffset timestamp,
        string? headerInstanceId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawPayload);

        bool bypassSignature = options.Value.IsWebhookSignatureBypassed;
        if (!bypassSignature && string.IsNullOrWhiteSpace(hmacSignature))
        {
            throw new ArgumentException("HMAC signature must be provided unless danger_bypass_webhook_signature_validation is enabled.", nameof(hmacSignature));
        }

        // 1. Replay-Schutz: 5 Minuten Gültigkeitsfenster (umgehbar via warn_ignore_webhook_timestamp_tolerance)
        bool ignoreTimestampTolerance = options.Value.IsWebhookTimestampToleranceIgnored;
        if (!ignoreTimestampTolerance)
        {
            var diff = DateTimeOffset.UtcNow - timestamp;
            if (diff > TimeSpan.FromMinutes(5) || diff < TimeSpan.FromMinutes(-5))
            {
                logger.LogWarning("Webhook abgelehnt: Timestamp außerhalb des 5-Minuten-Gültigkeitsfensters.");
                return false;
            }
        }

        // 2. Secret-Bezug & Signaturvergleich (umgehbar via danger_bypass_webhook_signature_validation)
        if (!bypassSignature)
        {
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

            var cleanSig = hmacSignature.Trim();
            if (cleanSig.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase))
            {
                cleanSig = cleanSig["sha256=".Length..];
            }

            byte[] computedHashWithTimestamp = HMACSHA256.HashData(secretKey, Encoding.UTF8.GetBytes($"t={timestamp:O}.v1={rawPayload}"));
            byte[] computedHashRaw = HMACSHA256.HashData(secretKey, Encoding.UTF8.GetBytes(rawPayload));
            byte[] providedHash;
            try
            {
                providedHash = Convert.FromHexString(cleanSig);
            }
            catch (FormatException)
            {
                logger.LogWarning("Webhook abgelehnt: Ungültiges Hex-Format der HMAC-SHA256-Signatur.");
                return false;
            }

            // 3. Timing-sicherer Signaturvergleich (Timestamp-gebunden oder Roh-Payload mit Header-Timestamp-Validierung)
            bool signatureValid = CryptographicOperations.FixedTimeEquals(computedHashWithTimestamp, providedHash) ||
                                  CryptographicOperations.FixedTimeEquals(computedHashRaw, providedHash);
            if (!signatureValid)
            {
                logger.LogWarning("Webhook abgelehnt: Ungültige HMAC-SHA256-Signatur.");
                return false;
            }
        }
        else
        {
            logger.LogWarning("[INSECURE GETTING STARTED] Bypassing ITSM webhook HMAC-SHA256 signature verification.");
        }

        // 4. Payload parsen (unterstützt kanonisches DTO, natives ServiceNow- und natives Jira-Format)
        var payload = ParsePayload(rawPayload, headerInstanceId, logger);
        if (payload == null || string.IsNullOrWhiteSpace(payload.TicketId))
        {
            logger.LogWarning("Webhook abgelehnt: TicketId konnte nicht ermittelt werden.");
            return false;
        }

        var request = await governanceRepo.GetConsentRequestByTicketIdAsync(payload.TicketId, ct).ConfigureAwait(false);
        if (request == null)
        {
            logger.LogWarning("Webhook verworfen: Unbekannte TicketId '{TicketId}'", payload.TicketId);
            return false;
        }

        // 5. Strikte Tenant-Bindungsprüfung (umgehbar via warn_fallback_default_tenant_for_webhooks)
        var expectedTenant = _itsmOptions.GetTenantForInstance(payload.InstanceId);
        if (expectedTenant == null || request.TenantId != expectedTenant.Value)
        {
            if (options.Value.IsWebhookTenantFallbackAllowed)
            {
                logger.LogWarning(
                    "[INSECURE GETTING STARTED] Bypassing cross-tenant mismatch for ticket {TicketId}. Request tenant: {ReqTenant}, callback instance: {InstanceId}",
                    payload.TicketId, request.TenantId, payload.InstanceId);
            }
            else
            {
                GatewayDiagnostics.CrossTenantMismatchCounter.Add(1);
                logger.LogError(
                    "CROSS_TENANT_WEBHOOK_MISMATCH: Ticket {TicketId} gehört zu Tenant {ReqTenant}, Callback kam von {CbTenant}",
                    payload.TicketId, request.TenantId, expectedTenant?.Value ?? "UNKNOWN_INSTANCE");
                return false; // Streng verweigern!
            }
        }

        // 6. Idempotente Bearbeitung (nur PENDING_EXTERNAL_APPROVAL darf bearbeitet werden)
        if (!string.Equals(request.Status, "PENDING_EXTERNAL_APPROVAL", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogInformation("Webhook ignoriert: Request befindet sich bereits im Status '{Status}'", request.Status);
            return true;
        }

        if (string.Equals(payload.Action, "REJECT", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogInformation("Consent Request {RequestId} via ITSM Ticket {TicketId} ({System}) abgelehnt.", request.Id, payload.TicketId, payload.System);
            await governanceRepo.RejectConsentRequestAsync(
                request.Id,
                new Sid($"ITSM_{payload.System.ToUpperInvariant()}"),
                payload.Reason ?? $"Rejected via {payload.System} webhook",
                ct).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(payload.Action, "APPROVE", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogInformation("Consent Request {RequestId} via ITSM Ticket {TicketId} ({System}) genehmigt. Aktiviere Consent...", request.Id, payload.TicketId, payload.System);
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

    private static ItsmStatusChangeDto? ParsePayload(string rawPayload, string? headerInstanceId, ILogger logger)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawPayload);
            var root = doc.RootElement;

            string ticketId = string.Empty;
            string instanceId = headerInstanceId ?? string.Empty;
            string action = "APPROVE";
            string? reason = null;
            string detectedSystem = "ITSM";

            // A. Kanonische DTO-Felder
            if (root.TryGetProperty("TicketId", out var tProp) || root.TryGetProperty("ticketId", out tProp) || root.TryGetProperty("ticket_id", out tProp))
            {
                ticketId = tProp.GetString() ?? string.Empty;
            }

            if (string.IsNullOrWhiteSpace(instanceId))
            {
                if (root.TryGetProperty("InstanceId", out var iProp) || root.TryGetProperty("instanceId", out iProp) || root.TryGetProperty("instance_id", out iProp))
                {
                    instanceId = iProp.GetString() ?? string.Empty;
                }
            }

            if (root.TryGetProperty("Action", out var aProp) || root.TryGetProperty("action", out aProp))
            {
                action = aProp.GetString() ?? "APPROVE";
            }

            if (root.TryGetProperty("Reason", out var rProp) || root.TryGetProperty("reason", out rProp))
            {
                reason = rProp.GetString();
            }

            // B. Natives ServiceNow-Payload Format (number, sys_id, approval, state, close_notes)
            if (string.IsNullOrWhiteSpace(ticketId))
            {
                if (root.TryGetProperty("number", out var numProp))
                {
                    ticketId = numProp.GetString() ?? string.Empty;
                    detectedSystem = "ServiceNow";
                }
                else if (root.TryGetProperty("sys_id", out var sysProp))
                {
                    ticketId = sysProp.GetString() ?? string.Empty;
                    detectedSystem = "ServiceNow";
                }

                if (detectedSystem == "ServiceNow")
                {
                    if (string.IsNullOrWhiteSpace(instanceId))
                    {
                        if (root.TryGetProperty("instance_name", out var inProp) || root.TryGetProperty("instance_id", out inProp))
                        {
                            instanceId = inProp.GetString() ?? string.Empty;
                        }
                    }

                    if (root.TryGetProperty("approval", out var appProp))
                    {
                        var app = appProp.GetString() ?? string.Empty;
                        if (string.Equals(app, "rejected", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(app, "not_approved", StringComparison.OrdinalIgnoreCase))
                        {
                            action = "REJECT";
                        }
                        else if (string.Equals(app, "approved", StringComparison.OrdinalIgnoreCase))
                        {
                            action = "APPROVE";
                        }
                    }
                    else if (root.TryGetProperty("state", out var stateProp))
                    {
                        var stateStr = stateProp.GetString() ?? stateProp.ToString();
                        if (stateStr == "4" || stateStr == "7" ||
                            string.Equals(stateStr, "rejected", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(stateStr, "closed_incomplete", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(stateStr, "cancelled", StringComparison.OrdinalIgnoreCase))
                        {
                            action = "REJECT";
                        }
                        else if (stateStr == "3" ||
                                 string.Equals(stateStr, "approved", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(stateStr, "closed_complete", StringComparison.OrdinalIgnoreCase))
                        {
                            action = "APPROVE";
                        }
                    }

                    if (reason == null)
                    {
                        if (root.TryGetProperty("close_notes", out var cnProp))
                        {
                            reason = cnProp.GetString();
                        }
                        else if (root.TryGetProperty("work_notes", out var wnProp))
                        {
                            reason = wnProp.GetString();
                        }
                        else if (root.TryGetProperty("comments", out var cProp))
                        {
                            reason = cProp.GetString();
                        }
                    }
                }
            }

            // C. Natives Jira-Payload Format (issue.key, issue.fields.status.name, resolution)
            if (string.IsNullOrWhiteSpace(ticketId) && root.TryGetProperty("issue", out var issueProp))
            {
                detectedSystem = "Jira";
                if (issueProp.TryGetProperty("key", out var keyProp))
                {
                    ticketId = keyProp.GetString() ?? string.Empty;
                }

                if (issueProp.TryGetProperty("fields", out var fieldsProp))
                {
                    if (fieldsProp.TryGetProperty("status", out var statusProp) &&
                        statusProp.TryGetProperty("name", out var statusNameProp))
                    {
                        var statusName = statusNameProp.GetString() ?? string.Empty;
                        if (statusName.Equals("Rejected", StringComparison.OrdinalIgnoreCase) ||
                            statusName.Equals("Declined", StringComparison.OrdinalIgnoreCase) ||
                            statusName.Equals("Cancelled", StringComparison.OrdinalIgnoreCase) ||
                            statusName.Contains("Won't", StringComparison.OrdinalIgnoreCase))
                        {
                            action = "REJECT";
                        }
                        else if (statusName.Equals("Approved", StringComparison.OrdinalIgnoreCase) ||
                                 statusName.Equals("Done", StringComparison.OrdinalIgnoreCase) ||
                                 statusName.Equals("Resolved", StringComparison.OrdinalIgnoreCase) ||
                                 statusName.Equals("Authorized", StringComparison.OrdinalIgnoreCase))
                        {
                            action = "APPROVE";
                        }
                    }

                    if (reason == null && fieldsProp.TryGetProperty("resolution", out var resProp) &&
                        resProp.TryGetProperty("name", out var resNameProp))
                    {
                        reason = resNameProp.GetString();
                    }
                }

                if (string.IsNullOrWhiteSpace(instanceId))
                {
                    if (root.TryGetProperty("baseUrl", out var baseProp))
                    {
                        instanceId = baseProp.GetString() ?? string.Empty;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(ticketId))
            {
                logger.LogWarning("Webhook abgelehnt: TicketId konnte weder aus DTO noch aus ServiceNow- oder Jira-Struktur ermittelt werden.");
                return null;
            }

            return new ItsmStatusChangeDto
            {
                TicketId = ticketId,
                InstanceId = instanceId,
                Action = action,
                Reason = reason,
                System = detectedSystem
            };
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Webhook abgelehnt: Ungültiges JSON-Payload.");
            return null;
        }
    }
}
