namespace GqlGateway.Application.Streaming.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using GqlGateway.Application.Interfaces;
using GqlGateway.Application.Streaming.Interfaces;
using GqlGateway.Domain.Common;
using GqlGateway.Domain.Interfaces;
using GqlGateway.Domain.Model;
using Microsoft.Extensions.Logging;

public sealed class StreamRlsPolicyEnforcer : IStreamRlsPolicyEnforcer
{
    private static readonly MaskingRule DefaultRedactRule = new() { RuleType = "REDACT", Replacement = "[REDACTED]" };

    private readonly IPolicyEnforcementService _policyEnforcementService;
    private readonly ITableMetadataRepository _metadataRepository;
    private readonly IColumnMaskingProvider _maskingProvider;
    private readonly IEpochValidationService _epochValidationService;
    private readonly ILogger<StreamRlsPolicyEnforcer> _logger;

    public StreamRlsPolicyEnforcer(
        IPolicyEnforcementService policyEnforcementService,
        ITableMetadataRepository metadataRepository,
        IColumnMaskingProvider maskingProvider,
        IEpochValidationService epochValidationService,
        ILogger<StreamRlsPolicyEnforcer> logger)
    {
        _policyEnforcementService = policyEnforcementService ?? throw new ArgumentNullException(nameof(policyEnforcementService));
        _metadataRepository = metadataRepository ?? throw new ArgumentNullException(nameof(metadataRepository));
        _maskingProvider = maskingProvider ?? throw new ArgumentNullException(nameof(maskingProvider));
        _epochValidationService = epochValidationService ?? throw new ArgumentNullException(nameof(epochValidationService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async ValueTask<StreamSecurityDecision> EvaluateAndMaskAsync(
        CdcEvent cdcEvent,
        ClaimsPrincipal subscriber,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(cdcEvent);
        ArgumentNullException.ThrowIfNull(subscriber);

        // 1. Tenant Isolation Check
        var subscriberTenant = subscriber.FindFirst("tenant_id")?.Value
            ?? subscriber.FindFirst("tenant")?.Value
            ?? subscriber.FindFirst("tid")?.Value
            ?? TenantId.LegacySingleTenant.Value;

        if (string.IsNullOrWhiteSpace(cdcEvent.TenantId) ||
            !string.Equals(cdcEvent.TenantId, subscriberTenant, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug(
                "Streaming event '{EventId}' dropped due to tenant mismatch (Event: {EventTenant}, Subscriber: {SubTenant})",
                cdcEvent.EventId, cdcEvent.TenantId, subscriberTenant);
            return StreamSecurityDecision.Denied("Tenant mismatch");
        }

        // 2. Extract Raw Payload based on Operation
        var rawPayload = cdcEvent.Operation == CdcOperation.Delete
            ? (cdcEvent.Before ?? cdcEvent.After)
            : (cdcEvent.After ?? cdcEvent.Before);

        if (rawPayload == null || rawPayload.Count == 0)
        {
            return StreamSecurityDecision.Denied("Empty event payload");
        }

        // 3. User Identity Extraction
        var userSidStr = subscriber.FindFirst(ClaimTypes.PrimarySid)?.Value
            ?? subscriber.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? subscriber.FindFirst("sub")?.Value
            ?? "S-1-5-ANONYMOUS";

        var userSid = new Sid(userSidStr);
        var groupSids = subscriber.FindAll(ClaimTypes.GroupSid)
            .Select(c => new Sid(c.Value))
            .ToList();

        var requestedColumns = rawPayload.Keys.ToList();

        // 4. Casbin ABAC Policy Enforcement
        var evalContext = new SecurityEvaluationContext(
            UserSid: userSid,
            GroupSids: groupSids,
            Tenant: new TenantId(subscriberTenant),
            TargetTable: cdcEvent.Table,
            RequestedColumns: requestedColumns,
            ClientIp: IPAddress.Loopback,
            Timestamp: DateTimeOffset.UtcNow,
            PurposeId: null
        );

        TableAccessDecision accessDecision;
        try
        {
            accessDecision = await _policyEnforcementService.EvaluatePolicyAsync(evalContext, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to evaluate streaming ABAC policy for event '{EventId}' - failing closed", cdcEvent.EventId);
            return StreamSecurityDecision.Denied("Policy evaluation error");
        }

        if (!accessDecision.IsAllowed)
        {
            _logger.LogDebug(
                "Streaming event '{EventId}' on table '{Table}' denied for subscriber '{UserSid}' by ABAC policy",
                cdcEvent.EventId, cdcEvent.Table.ToQualifiedName(), userSid.Value);
            return StreamSecurityDecision.Denied("Access denied by ABAC policy");
        }

        // 4b. In-Stream Row-Level Security Filtering (Fail-Closed)
        if (!string.IsNullOrWhiteSpace(accessDecision.CombinedRowFilterSql))
        {
            if (!MatchesStreamingRowFilter(rawPayload, accessDecision.CombinedRowFilterSql))
            {
                _logger.LogDebug(
                    "Streaming event '{EventId}' filtered out by in-stream row filter '{Filter}' for subscriber '{UserSid}'",
                    cdcEvent.EventId, accessDecision.CombinedRowFilterSql, userSid.Value);
                return StreamSecurityDecision.Denied("Filtered by row-level security");
            }
        }

        // 5. In-Stream Column Masking and Redaction
        var metadata = await _metadataRepository.GetTableMetadataAsync(cdcEvent.Table, ct);
        var maskedResult = new Dictionary<string, object?>(rawPayload.Count, StringComparer.OrdinalIgnoreCase);

        foreach (var (columnName, rawValue) in rawPayload)
        {
            var accessLevel = accessDecision.GetColumnAccess(columnName);

            switch (accessLevel)
            {
                case ColumnAccessLevel.Deny:
                    // Completely strip column from streaming payload (zero leakage)
                    continue;

                case ColumnAccessLevel.Mask:
                    if (rawValue != null)
                    {
                        var rule = (metadata != null && metadata.ColumnMaskingRules.TryGetValue(columnName, out var r))
                            ? r
                            : DefaultRedactRule;
                        maskedResult[columnName] = _maskingProvider.MaskValue(columnName, rawValue, rule);
                    }
                    else
                    {
                        maskedResult[columnName] = null;
                    }
                    break;

                case ColumnAccessLevel.Clear:
                default:
                    maskedResult[columnName] = rawValue;
                    break;
            }
        }

        return StreamSecurityDecision.Allowed(maskedResult);
    }

    private static bool MatchesStreamingRowFilter(IReadOnlyDictionary<string, object?> payload, string filterSql)
    {
        return StreamingRowFilterAstEvaluator.Matches(payload, filterSql);
    }
}
