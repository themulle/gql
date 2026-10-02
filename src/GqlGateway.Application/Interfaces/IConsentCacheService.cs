using System.Security.Cryptography;
using System.Text;
using GqlGateway.Domain.Common;
using GqlGateway.Domain.Interfaces;

namespace GqlGateway.Application.Interfaces;

public interface IConsentCacheService
{
    Task<TableAccessDecision?> GetCachedDecisionAsync(Sid userSid, TableIdentifier table, string? contextHash = null, CancellationToken ct = default)
        => GetCachedDecisionAsync(TenantId.LegacySingleTenant, userSid, table, contextHash, ct);

    Task<TableAccessDecision?> GetCachedDecisionAsync(TenantId tenant, Sid userSid, TableIdentifier table, string? contextHash = null, CancellationToken ct = default);

    Task SetCachedDecisionAsync(Sid userSid, TableIdentifier table, TableAccessDecision decision, TimeSpan ttl, string? contextHash = null, CancellationToken ct = default)
        => SetCachedDecisionAsync(TenantId.LegacySingleTenant, userSid, table, decision, ttl, contextHash, ct);

    Task SetCachedDecisionAsync(TenantId tenant, Sid userSid, TableIdentifier table, TableAccessDecision decision, TimeSpan ttl, string? contextHash = null, CancellationToken ct = default);

    Task EvictTableDecisionsAsync(TableIdentifier table, CancellationToken ct = default);
    Task ClearL1CacheAsync(CancellationToken ct = default);

    public static string ComputeSubjectContextHash(IReadOnlySet<Sid>? groupSids, IReadOnlySet<string>? roles)
    {
        var groups = groupSids != null && groupSids.Count > 0
            ? string.Join(";", groupSids.Select(s => s.Value.ToUpperInvariant()).OrderBy(s => s, StringComparer.Ordinal))
            : string.Empty;
        var r = roles != null && roles.Count > 0
            ? string.Join(";", roles.Select(x => x.ToUpperInvariant()).OrderBy(x => x, StringComparer.Ordinal))
            : string.Empty;
        if (string.IsNullOrEmpty(groups) && string.IsNullOrEmpty(r)) return "default";
        var bytes = Encoding.UTF8.GetBytes($"{groups}|{r}");
        return Convert.ToHexString(SHA256.HashData(bytes))[..16];
    }
}

