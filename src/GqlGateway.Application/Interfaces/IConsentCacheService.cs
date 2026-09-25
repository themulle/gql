namespace GqlGateway.Application.Interfaces;

public interface IConsentCacheService
{
    Task<TableAccessDecision?> GetCachedDecisionAsync(Sid userSid, TableIdentifier table, CancellationToken ct = default);
    Task SetCachedDecisionAsync(Sid userSid, TableIdentifier table, TableAccessDecision decision, TimeSpan ttl, CancellationToken ct = default);
    Task EvictTableDecisionsAsync(TableIdentifier table, CancellationToken ct = default);
    Task ClearL1CacheAsync(CancellationToken ct = default);
}
