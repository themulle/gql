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

    public ClientTierResolver(ILogger<ClientTierResolver> logger)
    {
        _logger = logger;
    }

    public Task<ClientQuotaContext> ResolveAsync(
        ClaimsPrincipal? principal,
        string? apiKey,
        string? clientIp,
        CancellationToken ct = default)
    {
        // 1. Check API Key
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            var cleanKey = apiKey.Trim();
            var (subjectId, tier) = ResolveApiKey(cleanKey);
            return Task.FromResult(new ClientQuotaContext(subjectId, tier, ClientQuotaPolicy.ForTier(tier)));
        }

        // 2. Check JWT Claims from Principal
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

        // 3. Fallback: Anonymous / Free tier
        var ip = !string.IsNullOrWhiteSpace(clientIp) ? clientIp : "anonymous";
        var freeSubject = $"anon_{ip}";
        return Task.FromResult(new ClientQuotaContext(freeSubject, ClientTier.Free, ClientQuotaPolicy.ForTier(ClientTier.Free)));
    }

    private (string SubjectId, ClientTier Tier) ResolveApiKey(string apiKey)
    {
        // SEC-1: Cryptographically hash the API key to ensure a unique, deterministic SubjectId
        // and eliminate shared-identity collision and substring-based tier privilege escalation.
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(apiKey));
        var hexHash = Convert.ToHexString(hashBytes).ToLowerInvariant();
        var subjectId = "key_" + hexHash[..16];

        var now = DateTimeOffset.UtcNow;
        if (_apiKeyCache.TryGetValue(subjectId, out var cached) && cached.Expiry > now)
        {
            return (subjectId, cached.Tier);
        }

        // Unregistered / untrusted API keys default to Standard tier.
        // Enterprise or Internal tiers must never be granted via client-supplied substring keywords.
        var tier = ClientTier.Standard;
        _apiKeyCache[subjectId] = (tier, now.AddMinutes(10));
        return (subjectId, tier);
    }
}
