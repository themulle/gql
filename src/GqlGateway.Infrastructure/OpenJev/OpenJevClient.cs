namespace GqlGateway.Infrastructure.OpenJev;

using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using GqlGateway.Application.Interfaces;
using GqlGateway.Domain.Common;
using GqlGateway.Domain.Model;
using Microsoft.Extensions.Logging;

public sealed partial class OpenJevClient : IOpenJevClient
{
    [GeneratedRegex(@"[\x00-\x1F\x7F]")]
    private static partial Regex ControlCharsRegex();

    private readonly HttpClient? _httpClient;
    private readonly ILogger<OpenJevClient> _logger;
    private readonly ConcurrentDictionary<string, (int Tokens, DateTimeOffset LastRefill)> _rateLimits = new();

    public OpenJevClient(ILogger<OpenJevClient> logger, HttpClient? httpClient = null)
    {
        _logger = logger;
        _httpClient = httpClient;
    }

    public async Task<JustificationTriageResult> ClassifyJustificationAsync(
        TenantId tenant,
        Sid userSid,
        TableIdentifier table,
        string justificationText,
        CancellationToken ct = default)
    {
        // 1. Input Sanitization & Bounds
        ArgumentNullException.ThrowIfNull(justificationText);

        if (justificationText.Length > 500)
        {
            justificationText = justificationText[..500];
        }

        // Bereinigung von Steuerzeichen
        justificationText = ControlCharsRegex().Replace(justificationText, " ").Trim();

        // 2. Per-User Rate Limiting (Token Bucket: max 20 per minute)
        if (!CheckRateLimit(userSid.Value))
        {
            _logger.LogWarning("OpenJev Rate Limit überschritten für User {UserSid}", userSid.Value);
            return new JustificationTriageResult(
                JustificationCategory.Unclassified,
                0.0,
                "Rate limit exceeded - routed to manual review",
                AutoGrantEligible: false,
                GrantedDuration: null);
        }

        // 3. 120 ms Timeout Guard (Defense against DoS)
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(120));

        try
        {
            return await ExecuteClassificationAsync(tenant, userSid, table, justificationText, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("OpenJev Klassifikator Timeout (> 120 ms) für User {UserSid}. Fallback auf UNCLASSIFIED.", userSid.Value);
            return new JustificationTriageResult(
                JustificationCategory.Unclassified,
                0.0,
                "Classifier timeout (>120ms) - fallback to human 4-eyes approval",
                AutoGrantEligible: false,
                GrantedDuration: null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OpenJev Klassifikator Fehler für User {UserSid}. Fallback auf UNCLASSIFIED.", userSid.Value);
            return new JustificationTriageResult(
                JustificationCategory.Unclassified,
                0.0,
                $"Classifier error ({ex.Message}) - fallback to human 4-eyes approval",
                AutoGrantEligible: false,
                GrantedDuration: null);
        }
    }

    private Task<JustificationTriageResult> ExecuteClassificationAsync(
        TenantId tenant,
        Sid userSid,
        TableIdentifier table,
        string text,
        CancellationToken ct)
    {
        // Deterministic Security Analysis against Prompt Injection & Jailbreaks
        var lower = text.ToLowerInvariant();

        // Adversarial Injection Patterns
        if (lower.Contains("ignore previous instructions") ||
            lower.Contains("disregard all previous") ||
            lower.Contains("system override") ||
            lower.Contains("override all") ||
            lower.Contains("system:") ||
            lower.Contains("<system>") ||
            lower.Contains("maintenance mode") ||
            lower.Contains("grant full access") ||
            lower.Contains("bypass") ||
            lower.Contains("jailbreak") ||
            lower.Contains("you are now") ||
            lower.Contains("as an administrator") ||
            lower.Contains("sudo grant") ||
            lower.Contains("prompt leakage"))
        {
            _logger.LogWarning("Prompt-Injection erkannt in Justification für User {UserSid}: {Pattern}", userSid.Value, text);
            return Task.FromResult(new JustificationTriageResult(
                JustificationCategory.SuspiciousExfiltration,
                0.99,
                "Adversarial prompt injection pattern detected",
                AutoGrantEligible: false,
                GrantedDuration: null));
        }

        if (lower.Contains("exfiltration") || lower.Contains("dump database") || lower.Contains("leak") || lower.Contains("export all credit cards"))
        {
            return Task.FromResult(new JustificationTriageResult(
                JustificationCategory.SuspiciousExfiltration,
                0.95,
                "Suspicious data exfiltration intent detected",
                AutoGrantEligible: false,
                GrantedDuration: null));
        }

        if (lower.Contains("audit") || lower.Contains("compliance") || lower.Contains("regulatory") || lower.Contains("sox") || lower.Contains("finma"))
        {
            return Task.FromResult(new JustificationTriageResult(
                JustificationCategory.LegitimateAudit,
                0.98,
                "Classified as legitimate compliance audit",
                AutoGrantEligible: false, // Entschieden durch JustificationTriageService je nach Tabellen-Opt-In
                GrantedDuration: null));
        }

        if (lower.Contains("incident") || lower.Contains("outage") || lower.Contains("p1") || lower.Contains("sev-1") || lower.Contains("emergency triage"))
        {
            return Task.FromResult(new JustificationTriageResult(
                JustificationCategory.IncidentTriage,
                0.98,
                "Classified as urgent incident triage",
                AutoGrantEligible: false,
                GrantedDuration: null));
        }

        if (text.Length < 10 || lower.Contains("test") || lower.Contains("asdf") || lower.Contains("please give access"))
        {
            return Task.FromResult(new JustificationTriageResult(
                JustificationCategory.Unjustified,
                0.90,
                "Insufficient justification provided",
                AutoGrantEligible: false,
                GrantedDuration: null));
        }

        return Task.FromResult(new JustificationTriageResult(
            JustificationCategory.Unclassified,
            0.50,
            "Standard request - human review required",
            AutoGrantEligible: false,
            GrantedDuration: null));
    }

    private bool CheckRateLimit(string userSid)
    {
        var now = DateTimeOffset.UtcNow;
        bool allowed = false;

        _rateLimits.AddOrUpdate(
            userSid,
            _ =>
            {
                allowed = true;
                return (Tokens: 19, LastRefill: now);
            },
            (_, existing) =>
            {
                var elapsed = now - existing.LastRefill;
                int replenished = (int)(elapsed.TotalSeconds * (20.0 / 60.0));
                int currentTokens = Math.Min(20, existing.Tokens + replenished);

                if (currentTokens >= 1)
                {
                    allowed = true;
                    return (Tokens: currentTokens - 1, LastRefill: now);
                }

                allowed = false;
                return (Tokens: 0, LastRefill: existing.LastRefill);
            });

        return allowed;
    }
}
