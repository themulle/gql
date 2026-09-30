namespace GqlGateway.Application.Caching.Services;

using System;
using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GqlGateway.Application.Caching.Interfaces;
using GqlGateway.Domain.Model;
using Microsoft.Extensions.Logging;

public sealed class ClientTierResolver : IClientTierResolver
{
    private readonly ILogger<ClientTierResolver> _logger;
    private readonly ConcurrentDictionary<string, (ClientTier Tier, DateTimeOffset Expiry)> _apiKeyCache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ClientTier> _registeredKeys = new(StringComparer.Ordinal);

    private const int MaxCacheSize = 10_000;

    public ClientTierResolver(ILogger<ClientTierResolver> logger)
    {
        _logger = logger;
    }

    public void RegisterApiKey(string apiKey, ClientTier tier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        _registeredKeys[apiKey.Trim()] = tier;
    }

    public Task<ClientQuotaContext> ResolveAsync(
        ClaimsPrincipal? principal,
        string? apiKey,
        string? clientIp,
        CancellationToken ct = default)
    {
        // 1. Check registered API Key
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            var cleanKey = apiKey.Trim();
            if (_registeredKeys.TryGetValue(cleanKey, out var registeredTier))
            {
                var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(cleanKey));
                var hexHash = Convert.ToHexString(hashBytes).ToLowerInvariant();
                var subjectId = "key_" + hexHash[..16];

                EnsureCacheCapacity();
                _apiKeyCache[subjectId] = (registeredTier, DateTimeOffset.UtcNow.AddMinutes(10));
                return Task.FromResult(new ClientQuotaContext(subjectId, registeredTier, ClientQuotaPolicy.ForTier(registeredTier)));
            }
        }

        // 2. Check JWT Claims from Principal (Authenticated user has precedence over unregistered API keys)
        if (principal?.Identity?.IsAuthenticated == true)
        {
            var tierClaim = principal.FindFirst("tier")?.Value ??
                            principal.FindFirst("client_tier")?.Value ??
                            principal.FindFirst("urn:gqlgateway:tier")?.Value;

            var subjectId = principal.FindFirst("client_id")?.Value ??
                            principal.FindFirst("azp")?.Value ??
                            principal.FindFirst("appid")?.Value ??
                            principal.FindFirst(ClaimTypes.NameIdentifier)?.Value ??
                            principal.FindFirst("sub")?.Value ??
                            principal.Identity.Name ??
                            "authenticated_user";

            if (!string.IsNullOrWhiteSpace(tierClaim) && Enum.TryParse<ClientTier>(tierClaim, true, out var parsedTier))
            {
                return Task.FromResult(new ClientQuotaContext(subjectId, parsedTier, ClientQuotaPolicy.ForTier(parsedTier)));
            }

            // Default authenticated user to Standard tier if no explicit tier claim is present
            return Task.FromResult(new ClientQuotaContext(subjectId, ClientTier.Standard, ClientQuotaPolicy.ForTier(ClientTier.Standard)));
        }

        // 3. Fallback: Unregistered API Key for Anonymous Client (always Free tier, deterministic subjectId, bounded cache)
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            var cleanKey = apiKey.Trim();
            var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(cleanKey));
            var hexHash = Convert.ToHexString(hashBytes).ToLowerInvariant();
            var subjectId = "key_" + hexHash[..16];

            EnsureCacheCapacity();
            _apiKeyCache[subjectId] = (ClientTier.Free, DateTimeOffset.UtcNow.AddMinutes(10));
            return Task.FromResult(new ClientQuotaContext(subjectId, ClientTier.Free, ClientQuotaPolicy.ForTier(ClientTier.Free)));
        }

        // 4. Fallback: Pure Anonymous / Free tier with IP
        var ip = !string.IsNullOrWhiteSpace(clientIp) ? clientIp : "anonymous";
        var freeSubject = $"anon_{ip}";
        return Task.FromResult(new ClientQuotaContext(freeSubject, ClientTier.Free, ClientQuotaPolicy.ForTier(ClientTier.Free)));
    }

    private void EnsureCacheCapacity()
    {
        if (_apiKeyCache.Count >= MaxCacheSize)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var kvp in _apiKeyCache)
            {
                if (kvp.Value.Expiry <= now)
                {
                    _apiKeyCache.TryRemove(kvp.Key, out _);
                }
            }

            // If still over capacity after removing expired entries, clear half
            if (_apiKeyCache.Count >= MaxCacheSize)
            {
                _apiKeyCache.Clear();
            }
        }
    }
}
