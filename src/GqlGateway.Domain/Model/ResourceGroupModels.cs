namespace GqlGateway.Domain.Model;

using System;
using System.Collections.Generic;

public enum ResourceGroupTier
{
    Interactive = 1,
    AutonomousAgents = 2,
    BulkAnalytics = 3
}

public sealed record ResourceGroupTierConfig(
    ResourceGroupTier Tier,
    int MaxConcurrency,
    int MaxQueueDepth,
    TimeSpan Timeout
)
{
    public static ResourceGroupTierConfig DefaultFor(ResourceGroupTier tier) => tier switch
    {
        ResourceGroupTier.Interactive => new(tier, MaxConcurrency: 50, MaxQueueDepth: 20, Timeout: TimeSpan.FromSeconds(5)),
        ResourceGroupTier.AutonomousAgents => new(tier, MaxConcurrency: 10, MaxQueueDepth: 50, Timeout: TimeSpan.FromSeconds(15)),
        ResourceGroupTier.BulkAnalytics => new(tier, MaxConcurrency: 5, MaxQueueDepth: 100, Timeout: TimeSpan.FromSeconds(60)),
        _ => throw new ArgumentOutOfRangeException(nameof(tier))
    };
}

public sealed record ResourceGroupTierMetrics(
    ResourceGroupTier Tier,
    int ActiveConcurrency,
    int MaxConcurrency,
    int QueuedRequests,
    int MaxQueueDepth,
    long TotalAcquired,
    long TotalRejectedQueueFull,
    long TotalRejectedTimeout
);

public sealed record ResourceGroupMetrics(
    DateTimeOffset Timestamp,
    IReadOnlyList<ResourceGroupTierMetrics> Tiers
);
