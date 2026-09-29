namespace GqlGateway.Application.OData.Services;

using System;
using System.Collections.Concurrent;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GqlGateway.Application.OData.Interfaces;

/// <summary>
/// High-performance in-memory cache manager storing pre-serialized OpenAPI UTF-8 byte payloads (F-API-03).
/// </summary>
public sealed class OpenApiCacheManager : IOpenApiCacheManager
{
    private readonly ConcurrentDictionary<string, byte[]> _cache = new(StringComparer.OrdinalIgnoreCase);

    public async Task<byte[]> GetOrAddAsync(
        string? domainScope,
        bool isYaml,
        Func<CancellationToken, Task<string>> factory,
        CancellationToken ct = default)
    {
        var key = BuildKey(domainScope, isYaml);
        if (_cache.TryGetValue(key, out var cachedBytes))
        {
            return cachedBytes;
        }

        var contentString = await factory(ct).ConfigureAwait(false);
        var bytes = Encoding.UTF8.GetBytes(contentString);
        _cache[key] = bytes;
        return bytes;
    }

    public void InvalidateCache()
    {
        _cache.Clear();
    }

    private static string BuildKey(string? domainScope, bool isYaml)
    {
        var format = isYaml ? "yaml" : "json";
        if (string.IsNullOrWhiteSpace(domainScope))
        {
            return $"global_{format}";
        }

        var sanitized = new string(domainScope.Where(c => char.IsLetterOrDigit(c) || c == '_' || c == '-').ToArray());
        return $"{sanitized.ToLowerInvariant()}_{format}";
    }
}
