namespace GqlGateway.Application.ResourceGroups;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GqlGateway.Domain.Model;
using GqlGateway.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public sealed class ResourceGroupManager : IResourceGroupManager, IDisposable
{
    private readonly ILogger<ResourceGroupManager> _logger;
    private readonly Dictionary<ResourceGroupTier, TierState> _tiers;
    private readonly bool _enabled;
    private bool _disposed;

    public ResourceGroupManager(IOptions<GatewayOptions> options, ILogger<ResourceGroupManager> logger)
    {
        _logger = logger;
        var rgOpts = options.Value.ResourceGroups ?? new ResourceGroupsOptions();
        _enabled = rgOpts.Enabled;

        _tiers = new Dictionary<ResourceGroupTier, TierState>
        {
            [ResourceGroupTier.Interactive] = new TierState(
                ResourceGroupTier.Interactive,
                rgOpts.Interactive.MaxConcurrency,
                rgOpts.Interactive.MaxQueueDepth,
                TimeSpan.FromSeconds(rgOpts.Interactive.TimeoutSeconds)),
            [ResourceGroupTier.AutonomousAgents] = new TierState(
                ResourceGroupTier.AutonomousAgents,
                rgOpts.AutonomousAgents.MaxConcurrency,
                rgOpts.AutonomousAgents.MaxQueueDepth,
                TimeSpan.FromSeconds(rgOpts.AutonomousAgents.TimeoutSeconds)),
            [ResourceGroupTier.BulkAnalytics] = new TierState(
                ResourceGroupTier.BulkAnalytics,
                rgOpts.BulkAnalytics.MaxConcurrency,
                rgOpts.BulkAnalytics.MaxQueueDepth,
                TimeSpan.FromSeconds(rgOpts.BulkAnalytics.TimeoutSeconds))
        };
    }

    public async ValueTask<ResourceGroupLeaseResult> TryAcquireLeaseAsync(
        ResourceGroupTier tier,
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        if (!_enabled)
        {
            return ResourceGroupLeaseResult.Acquired(tier, EmptyLease.Instance);
        }

        if (!_tiers.TryGetValue(tier, out var state))
        {
            state = _tiers[ResourceGroupTier.Interactive];
        }

        return await state.TryAcquireAsync(_logger, tenantId, cancellationToken).ConfigureAwait(false);
    }

    public ResourceGroupMetrics GetMetrics()
    {
        var list = new List<ResourceGroupTierMetrics>(_tiers.Count);
        foreach (var kvp in _tiers)
        {
            list.Add(kvp.Value.GetMetrics());
        }

        return new ResourceGroupMetrics(DateTimeOffset.UtcNow, list);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var state in _tiers.Values)
        {
            state.Dispose();
        }
    }

    private sealed class EmptyLease : IAsyncDisposable
    {
        public static readonly EmptyLease Instance = new();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TierState : IDisposable
    {
        public readonly ResourceGroupTier Tier;
        public readonly int MaxConcurrency;
        public readonly int MaxQueueDepth;
        public readonly TimeSpan Timeout;
        private readonly SemaphoreSlim _semaphore;

        private int _queuedRequests;
        private long _totalAcquired;
        private long _totalRejectedQueueFull;
        private long _totalRejectedTimeout;

        public TierState(ResourceGroupTier tier, int maxConcurrency, int maxQueueDepth, TimeSpan timeout)
        {
            Tier = tier;
            MaxConcurrency = Math.Max(1, maxConcurrency);
            MaxQueueDepth = Math.Max(0, maxQueueDepth);
            Timeout = timeout > TimeSpan.Zero ? timeout : TimeSpan.FromSeconds(5);
            _semaphore = new SemaphoreSlim(MaxConcurrency, MaxConcurrency);
        }

        public async ValueTask<ResourceGroupLeaseResult> TryAcquireAsync(
            ILogger logger,
            string tenantId,
            CancellationToken ct)
        {
            // Fast path: Immediate acquisition without waiting (only if no requests are already queued to avoid barging)
            if (Volatile.Read(ref _queuedRequests) == 0 && _semaphore.Wait(0, CancellationToken.None))
            {
                Interlocked.Increment(ref _totalAcquired);
                return ResourceGroupLeaseResult.Acquired(Tier, new LeaseScope(_semaphore));
            }

            // Slow path: Request needs to be queued
            int currentQueue = Interlocked.Increment(ref _queuedRequests);
            if (currentQueue > MaxQueueDepth)
            {
                Interlocked.Decrement(ref _queuedRequests);
                Interlocked.Increment(ref _totalRejectedQueueFull);
                logger.LogWarning(
                    "Resource group {Tier} rejected request from tenant {TenantId}: queue full ({CurrentQueue}/{MaxQueue})",
                    Tier, tenantId, currentQueue - 1, MaxQueueDepth);
                return ResourceGroupLeaseResult.QueueFull(Tier);
            }

            try
            {
                bool entered = await _semaphore.WaitAsync(Timeout, ct).ConfigureAwait(false);
                if (!entered)
                {
                    Interlocked.Increment(ref _totalRejectedTimeout);
                    logger.LogWarning(
                        "Resource group {Tier} request from tenant {TenantId} timed out after {TimeoutMs}ms",
                        Tier, tenantId, Timeout.TotalMilliseconds);
                    return ResourceGroupLeaseResult.TimedOut(Tier);
                }

                Interlocked.Increment(ref _totalAcquired);
                return ResourceGroupLeaseResult.Acquired(Tier, new LeaseScope(_semaphore));
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref _totalRejectedTimeout);
                throw;
            }
            finally
            {
                Interlocked.Decrement(ref _queuedRequests);
            }
        }

        public ResourceGroupTierMetrics GetMetrics()
        {
            int active = Math.Clamp(MaxConcurrency - _semaphore.CurrentCount, 0, MaxConcurrency);
            return new ResourceGroupTierMetrics(
                Tier,
                ActiveConcurrency: active,
                MaxConcurrency: MaxConcurrency,
                QueuedRequests: Math.Max(0, Volatile.Read(ref _queuedRequests)),
                MaxQueueDepth: MaxQueueDepth,
                TotalAcquired: Volatile.Read(ref _totalAcquired),
                TotalRejectedQueueFull: Volatile.Read(ref _totalRejectedQueueFull),
                TotalRejectedTimeout: Volatile.Read(ref _totalRejectedTimeout)
            );
        }

        public void Dispose()
        {
            _semaphore.Dispose();
        }
    }

    private sealed class LeaseScope : IAsyncDisposable
    {
        private SemaphoreSlim? _semaphore;

        public LeaseScope(SemaphoreSlim semaphore)
        {
            _semaphore = semaphore;
        }

        public ValueTask DisposeAsync()
        {
            var sem = Interlocked.Exchange(ref _semaphore, null);
            if (sem != null)
            {
                try
                {
                    sem.Release();
                }
                catch (ObjectDisposedException)
                {
                }
            }
            return ValueTask.CompletedTask;
        }
    }
}
