using System;
using System.Collections.Concurrent;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using GqlGateway.Application.Interfaces;
using GqlGateway.Application.OpenMetadata.Interfaces;
using GqlGateway.Domain.Common;
using GqlGateway.Domain.Model;
using HotChocolate;
using Microsoft.AspNetCore.Http;

namespace GqlGateway.GraphQL.Types;

public sealed class OpenMetadataSyncPayload
{
    public bool Success { get; init; }
    public int SyncedTables { get; init; }
    public int SyncedConsents { get; init; }
    public int SyncedMaskingRules { get; init; }
    public List<string> Warnings { get; init; } = [];
}

public sealed class ConsentRequestPayload
{
    public Guid RequestId { get; init; }
    public string Status { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
}

public sealed class Mutation
{
    private sealed record IdempotencyEntry(ConsentRequestPayload Payload, DateTimeOffset CreatedAt);
    private static readonly ConcurrentDictionary<string, IdempotencyEntry> IdempotencyStore = new();

    private static Sid GetAuthenticatedUserSid(IHttpContextAccessor httpContextAccessor)
    {
        var httpContext = httpContextAccessor?.HttpContext;
        if (httpContext?.User?.Identity?.IsAuthenticated != true)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("UNAUTHORIZED")
                .SetMessage("Authentifizierung erforderlich.")
                .Build());
        }

        var userSid = httpContext.User.GetUserSid();
        if (userSid == null)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("UNAUTHORIZED")
                .SetMessage("Keine gültige Benutzer-SID im Authentifizierungstoken vorhanden.")
                .Build());
        }

        return userSid.Value;
    }

    private static bool TryGetIdempotent(Sid userSid, string operation, string? idempotencyKey, out ConsentRequestPayload payload)
    {
        payload = default!;
        if (string.IsNullOrEmpty(idempotencyKey)) return false;
        var compositeKey = $"{userSid.Value}:{operation}:{idempotencyKey}";
        if (IdempotencyStore.TryGetValue(compositeKey, out var entry))
        {
            payload = entry.Payload;
            return true;
        }
        return false;
    }

    private static void StoreIdempotent(Sid userSid, string operation, string? idempotencyKey, ConsentRequestPayload payload)
    {
        if (string.IsNullOrEmpty(idempotencyKey)) return;

        if (IdempotencyStore.Count > 5000)
        {
            var cutoff = DateTimeOffset.UtcNow.AddHours(-24);
            foreach (var kvp in IdempotencyStore)
            {
                if (kvp.Value.CreatedAt < cutoff)
                {
                    IdempotencyStore.TryRemove(kvp.Key, out _);
                }
            }

            if (IdempotencyStore.Count > 5000)
            {
                var oldestKeys = IdempotencyStore
                    .OrderBy(kvp => kvp.Value.CreatedAt)
                    .Take(IdempotencyStore.Count - 4000)
                    .Select(kvp => kvp.Key)
                    .ToList();
                foreach (var k in oldestKeys)
                {
                    IdempotencyStore.TryRemove(k, out _);
                }
            }
        }

        var compositeKey = $"{userSid.Value}:{operation}:{idempotencyKey}";
        IdempotencyStore[compositeKey] = new IdempotencyEntry(payload, DateTimeOffset.UtcNow);
    }

    public Task<ConsentRequestPayload> RequestTableAccessAsync(
        string domain,
        string schema,
        string tableName,
        string justification,
        int durationDays,
        string? idempotencyKey,
        [Service] IGovernanceRepository repository,
        [Service] IHttpContextAccessor httpContextAccessor,
        CancellationToken ct = default)
        => RequestTableAccessAsync(domain, schema, tableName, justification, durationDays, idempotencyKey, repository, repository, httpContextAccessor, ct);

    public async Task<ConsentRequestPayload> RequestTableAccessAsync(
        string domain,
        string schema,
        string tableName,
        string justification,
        int durationDays,
        string? idempotencyKey,
        [Service] ITableMetadataRepository metadataRepository = default!,
        [Service] IConsentApprovalRepository approvalRepository = default!,
        [Service] IHttpContextAccessor httpContextAccessor = default!,
        CancellationToken ct = default)
    {
        var userSid = GetAuthenticatedUserSid(httpContextAccessor);

        if (TryGetIdempotent(userSid, "RequestTableAccess", idempotencyKey, out var existing))
        {
            return existing;
        }

        var tableId = new TableIdentifier(domain, schema, tableName);
        var meta = await metadataRepository.GetTableMetadataAsync(tableId, ct);
        if (meta == null)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("NOT_FOUND")
                .SetMessage($"Table '{tableId}' does not exist.")
                .Build());
        }

        var request = new ConsentRequest
        {
            TableId = meta.Table.Id,
            TableIdentifier = tableId,
            RequesterSid = userSid,
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = userSid.Value,
            BusinessJustification = justification,
            RequestedValidTo = DateTimeOffset.UtcNow.AddDays(Math.Clamp(durationDays, 1, 365))
        };

        var created = await approvalRepository.CreateConsentRequestAsync(request, ct);
        var payload = new ConsentRequestPayload
        {
            RequestId = created.Id,
            Status = created.Status,
            Message = "Consent request submitted successfully."
        };

        StoreIdempotent(userSid, "RequestTableAccess", idempotencyKey, payload);
        return payload;
    }

    public Task<ConsentRequestPayload> ApproveConsentRequestAsync(
        Guid requestId,
        string? idempotencyKey,
        [Service] IGovernanceRepository repository,
        [Service] IHttpContextAccessor httpContextAccessor,
        CancellationToken ct = default)
        => ApproveConsentRequestAsync(requestId, idempotencyKey, repository, repository, repository, httpContextAccessor, ct);

    public async Task<ConsentRequestPayload> ApproveConsentRequestAsync(
        Guid requestId,
        string? idempotencyKey,
        [Service] IConsentApprovalRepository approvalRepository = default!,
        [Service] IDataOwnershipRepository ownershipRepository = default!,
        [Service] IConsentRepository consentRepository = default!,
        [Service] IHttpContextAccessor httpContextAccessor = default!,
        CancellationToken ct = default)
    {
        var approverSid = GetAuthenticatedUserSid(httpContextAccessor);

        if (TryGetIdempotent(approverSid, "ApproveConsentRequest", idempotencyKey, out var existing))
        {
            return existing;
        }

        var principal = httpContextAccessor?.HttpContext?.User;

        // Verify request existence and approver authorization (F-AUTH-05)
        var req = await approvalRepository.GetConsentRequestAsync(requestId, ct);
        if (req == null)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("NOT_FOUND")
                .SetMessage($"Consent-Antrag '{requestId}' wurde nicht gefunden.")
                .Build());
        }

        var roles = principal?.FindAll(ClaimTypes.Role).Select(r => r.Value).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new();
        bool isPrivilegedAdmin = roles.Contains("GovernanceAdmin") || roles.Contains("ClusterAdmin");

        if (!isPrivilegedAdmin)
        {
            var isAuthorized = await ownershipRepository.IsAuthorizedApproverForTableAsync(req.TableIdentifier, approverSid, ct);
            if (!isAuthorized)
            {
                throw new GraphQLException(ErrorBuilder.New()
                    .SetCode("FORBIDDEN")
                    .SetMessage($"Benutzer '{approverSid}' ist weder Data Owner noch delegierter Genehmiger für '{req.TableIdentifier}' und besitzt keine Genehmiger-Rolle.")
                    .Build());
            }
        }

        var approved = await approvalRepository.ApproveConsentRequestStepAsync(requestId, approverSid, ct);

        // Automatically create active consent ONLY upon final full approval (Fix 2.4: honor RequestedGranteeType)
        if (string.Equals(approved.Status, "APPROVED", StringComparison.OrdinalIgnoreCase))
        {
            var isRole = approved.RequestedGranteeType == GranteeType.Role;
            var consent = new Consent
            {
                TableId = approved.TableId,
                TableIdentifier = approved.TableIdentifier,
                ConsentRequestId = approved.Id,
                Effect = ConsentEffect.Allow,
                GranteeType = approved.RequestedGranteeType,
                GranteeSid = isRole ? (Sid?)null : new Sid(approved.RequestedGranteeRef),
                RoleName = isRole ? approved.RequestedGranteeRef : null,
                ValidFrom = DateTimeOffset.UtcNow,
                ValidTo = approved.RequestedValidTo
            };
            await consentRepository.CreateConsentAsync(consent, ct);
        }

        var message = string.Equals(approved.Status, "APPROVED", StringComparison.OrdinalIgnoreCase)
            ? "Consent request approved and active consent created."
            : $"Consent request approved step completed. Current status: {approved.Status}.";

        var payload = new ConsentRequestPayload
        {
            RequestId = approved.Id,
            Status = approved.Status,
            Message = message
        };

        StoreIdempotent(approverSid, "ApproveConsentRequest", idempotencyKey, payload);
        return payload;
    }

    public Task<ConsentRequestPayload> RejectConsentRequestAsync(
        Guid requestId,
        string reason,
        string? idempotencyKey,
        [Service] IGovernanceRepository repository,
        [Service] IHttpContextAccessor httpContextAccessor,
        CancellationToken ct = default)
        => RejectConsentRequestAsync(requestId, reason, idempotencyKey, repository, repository, httpContextAccessor, ct);

    public async Task<ConsentRequestPayload> RejectConsentRequestAsync(
        Guid requestId,
        string reason,
        string? idempotencyKey,
        [Service] IConsentApprovalRepository approvalRepository = default!,
        [Service] IDataOwnershipRepository ownershipRepository = default!,
        [Service] IHttpContextAccessor httpContextAccessor = default!,
        CancellationToken ct = default)
    {
        var approverSid = GetAuthenticatedUserSid(httpContextAccessor);

        if (TryGetIdempotent(approverSid, "RejectConsentRequest", idempotencyKey, out var existing))
        {
            return existing;
        }

        var principal = httpContextAccessor?.HttpContext?.User;

        var req = await approvalRepository.GetConsentRequestAsync(requestId, ct);
        if (req == null)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("NOT_FOUND")
                .SetMessage($"Consent-Antrag '{requestId}' wurde nicht gefunden.")
                .Build());
        }

        var roles = principal?.FindAll(ClaimTypes.Role).Select(r => r.Value).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new();
        bool isPrivilegedAdmin = roles.Contains("GovernanceAdmin") || roles.Contains("ClusterAdmin");

        if (!isPrivilegedAdmin)
        {
            var isAuthorized = await ownershipRepository.IsAuthorizedApproverForTableAsync(req.TableIdentifier, approverSid, ct);
            if (!isAuthorized)
            {
                throw new GraphQLException(ErrorBuilder.New()
                    .SetCode("FORBIDDEN")
                    .SetMessage($"Benutzer '{approverSid}' ist weder Data Owner noch delegierter Genehmiger für '{req.TableIdentifier}' und besitzt keine Genehmiger-Rolle.")
                    .Build());
            }
        }

        var rejected = await approvalRepository.RejectConsentRequestAsync(requestId, approverSid, reason, ct);
        var payload = new ConsentRequestPayload
        {
            RequestId = rejected.Id,
            Status = rejected.Status,
            Message = $"Consent request rejected: {reason}"
        };

        StoreIdempotent(approverSid, "RejectConsentRequest", idempotencyKey, payload);
        return payload;
    }

    public Task<bool> RevokeConsentAsync(
        Guid consentId,
        string reason,
        [Service] IGovernanceRepository repository,
        [Service] IHttpContextAccessor httpContextAccessor,
        CancellationToken ct = default)
        => RevokeConsentAsync(consentId, reason, repository, repository, httpContextAccessor, ct);

    public async Task<bool> RevokeConsentAsync(
        Guid consentId,
        string reason,
        [Service] IConsentRepository consentRepository = default!,
        [Service] IDataOwnershipRepository ownershipRepository = default!,
        [Service] IHttpContextAccessor httpContextAccessor = default!,
        CancellationToken ct = default)
    {
        var revokerSid = GetAuthenticatedUserSid(httpContextAccessor);
        var consent = await consentRepository.GetConsentByIdAsync(consentId, ct);
        if (consent == null)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("NOT_FOUND")
                .SetMessage($"Consent '{consentId}' wurde nicht gefunden.")
                .Build());
        }

        var principal = httpContextAccessor?.HttpContext?.User;
        var roles = principal?.FindAll(ClaimTypes.Role).Select(r => r.Value).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new();
        bool isPrivilegedAdmin = roles.Contains("GovernanceAdmin") || roles.Contains("ClusterAdmin");

        bool isGranteeSelf = consent.GranteeType == GranteeType.User &&
                             consent.GranteeSid.HasValue &&
                             consent.GranteeSid.Value == revokerSid;

        bool isOwnerOrDelegate = false;
        if (!isPrivilegedAdmin && !isGranteeSelf)
        {
            isOwnerOrDelegate = await ownershipRepository.IsAuthorizedApproverForTableAsync(consent.TableIdentifier, revokerSid, ct);
        }

        if (!isPrivilegedAdmin && !isGranteeSelf && !isOwnerOrDelegate)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("FORBIDDEN")
                .SetMessage($"Benutzer '{revokerSid}' ist weder GovernanceAdmin, Data Owner oder delegierter Genehmiger für '{consent.TableIdentifier}', noch der Begünstigte selbst.")
                .Build());
        }

        await consentRepository.RevokeConsentAsync(consentId, revokerSid, reason, ct);
        return true;
    }

    public async Task<bool> ReloadSchemaAsync(
        [Service] IEventBus eventBus = default!,
        [Service] IHttpContextAccessor httpContextAccessor = default!,
        CancellationToken ct = default)
    {
        var principal = httpContextAccessor?.HttpContext?.User;
        if (principal?.Identity?.IsAuthenticated != true)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("UNAUTHORIZED")
                .SetMessage("Authentifizierung erforderlich.")
                .Build());
        }

        var roles = principal.FindAll(ClaimTypes.Role).Select(r => r.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!roles.Contains("GovernanceAdmin") && !roles.Contains("ClusterAdmin"))
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("FORBIDDEN")
                .SetMessage("Nur Governance- oder Cluster-Administratoren dürfen das Schema neu laden.")
                .Build());
        }

        await eventBus.PublishAsync("schema:reload", DateTimeOffset.UtcNow.ToString("O"), ct);
        return true;
    }

    public async Task<OpenMetadataSyncPayload> SyncOpenMetadataAsync(
        bool dryRun = false,
        [Service] IOpenMetadataSyncService syncService = default!,
        [Service] IHttpContextAccessor httpContextAccessor = default!,
        CancellationToken ct = default)
    {
        var principal = httpContextAccessor?.HttpContext?.User;
        if (principal?.Identity?.IsAuthenticated != true)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("UNAUTHORIZED")
                .SetMessage("Authentifizierung erforderlich.")
                .Build());
        }

        var roles = principal.FindAll(ClaimTypes.Role).Select(r => r.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!roles.Contains("GovernanceAdmin") && !roles.Contains("ClusterAdmin"))
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("FORBIDDEN")
                .SetMessage("Nur Governance- oder Cluster-Administratoren dürfen OpenMetadata synchronisieren.")
                .Build());
        }

        var result = await syncService.SyncPermissionsAsync(dryRun, ct);
        return new OpenMetadataSyncPayload
        {
            Success = result.Success,
            SyncedTables = result.SyncedTables,
            SyncedConsents = result.SyncedConsents,
            SyncedMaskingRules = result.SyncedMaskingRules,
            Warnings = result.Warnings.ToList()
        };
    }
}
