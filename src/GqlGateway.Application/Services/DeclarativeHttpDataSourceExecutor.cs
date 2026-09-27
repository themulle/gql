using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GqlGateway.Application.Interfaces;
using GqlGateway.Domain.Common;
using GqlGateway.Domain.Model;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

[assembly: InternalsVisibleTo("GqlGateway.Tests.Unit")]

namespace GqlGateway.Application.Services;

public sealed class DeclarativeHttpDataSourceExecutor : IDataSourceExecutor
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<DeclarativeHttpDataSourceExecutor> _logger;
    private readonly IKeyVaultSecretProvider? _secretProvider;
    private readonly IHostEnvironment? _environment;

    private static readonly HashSet<string> DisallowedForwardHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization", "Cookie", "Set-Cookie", "Host", "Proxy-Authorization",
        "Proxy-Authenticate", "X-Forwarded-For", "X-Forwarded-Host", "X-Forwarded-Proto",
        "X-User-Sid", "X-Tenant-Id", "X-Tenant-ID", "Forwarded", "X-Original-URL",
        "X-Rewrite-URL", "X-Real-IP", "X-Gateway-Identity"
    };

    public const string HttpClientName = "DeclarativeHttp";
    public DataSourceType SupportedType => DataSourceType.HttpDeclarative;

    public DeclarativeHttpDataSourceExecutor(
        IHttpClientFactory httpClientFactory,
        ILogger<DeclarativeHttpDataSourceExecutor> logger,
        IKeyVaultSecretProvider? secretProvider = null,
        IHostEnvironment? environment = null)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _secretProvider = secretProvider;
        _environment = environment;
    }

    public async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ExecuteAsync(
        DataSourceExecutionContext context,
        CancellationToken ct = default)
    {
        var descriptor = context.Metadata.HttpEndpoint;
        if (descriptor == null)
        {
            throw new InvalidOperationException(
                $"Tabelle '{context.Metadata.Identifier}' ist als HttpDeclarative konfiguriert, besitzt aber keinen HttpEndpointDescriptor.");
        }

        // Check if this execution is a batch request (e.g. an "ids" or "keys" array parameter)
        if (descriptor.BatchType != HttpBatchType.None &&
            !string.IsNullOrWhiteSpace(descriptor.BatchParamName) &&
            context.Arguments.TryGetValue(descriptor.BatchParamName, out var batchArgVal) &&
            batchArgVal is IEnumerable<object> batchKeys &&
            batchArgVal is not string)
        {
            var keysList = batchKeys.Select(k => k.ToString() ?? string.Empty).Where(s => !string.IsNullOrEmpty(s)).ToList();
            if (keysList.Count > 0)
            {
                return await ExecuteBatchAsync(descriptor, context, keysList, ct);
            }
        }

        return await ExecuteSingleRequestAsync(descriptor, context, context.Arguments, ct);
    }

    public async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ExecuteBatchAsync(
        HttpEndpointDescriptor descriptor,
        DataSourceExecutionContext context,
        IReadOnlyList<string> keys,
        CancellationToken ct = default)
    {
        switch (descriptor.BatchType)
        {
            case HttpBatchType.QueryParameterList:
            {
                // Bulk via query param list, e.g. ?ids=1,2,3
                var paramName = descriptor.BatchParamName ?? "ids";
                var joinedKeys = string.Join(",", keys);
                var batchArgs = new Dictionary<string, object?>(context.Arguments)
                {
                    [paramName] = joinedKeys
                };
                return await ExecuteSingleRequestAsync(descriptor, context, batchArgs, ct);
            }

            case HttpBatchType.JsonBodyArray:
            {
                // Bulk via POST body with JSON array
                return await ExecuteJsonArrayBatchAsync(descriptor, context, keys, ct);
            }

            case HttpBatchType.ParallelSingleRequests:
            default:
            {
                // Throttled parallel single requests
                return await ExecuteThrottledParallelRequestsAsync(descriptor, context, keys, ct);
            }
        }
    }

    private async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ExecuteSingleRequestAsync(
        HttpEndpointDescriptor descriptor,
        DataSourceExecutionContext context,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);
        using var timeoutCts = descriptor.Timeout > TimeSpan.Zero
            ? CancellationTokenSource.CreateLinkedTokenSource(ct)
            : null;
        if (timeoutCts != null)
        {
            timeoutCts.CancelAfter(descriptor.Timeout);
        }
        var effectiveCt = timeoutCts?.Token ?? ct;

        var url = BuildUrl(descriptor, arguments, context.Principal);
        await ValidateDestinationUrl(url, effectiveCt);
        var method = new HttpMethod(descriptor.Method ?? "GET");

        using var request = new HttpRequestMessage(method, url);
        ApplyHeadersAndAuth(request, descriptor, context);

        _logger.LogDebug("Executing Declarative HTTP {Method} {Url} for {Table}", method, url, context.Metadata.Identifier);

        using var response = await SendWithRedirectProtectionAsync(client, request, descriptor, context, effectiveCt);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(effectiveCt);
        using var jsonDoc = await JsonDocument.ParseAsync(stream, cancellationToken: effectiveCt);

        return ExtractRowsFromJson(jsonDoc.RootElement, descriptor.JsonRootPath);
    }

    private async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ExecuteJsonArrayBatchAsync(
        HttpEndpointDescriptor descriptor,
        DataSourceExecutionContext context,
        IReadOnlyList<string> keys,
        CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);
        using var timeoutCts = descriptor.Timeout > TimeSpan.Zero
            ? CancellationTokenSource.CreateLinkedTokenSource(ct)
            : null;
        if (timeoutCts != null)
        {
            timeoutCts.CancelAfter(descriptor.Timeout);
        }
        var effectiveCt = timeoutCts?.Token ?? ct;

        var url = BuildUrl(descriptor, context.Arguments, context.Principal);
        await ValidateDestinationUrl(url, effectiveCt);
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        ApplyHeadersAndAuth(request, descriptor, context);

        var jsonBody = JsonSerializer.Serialize(keys);
        request.Content = new StringContent(jsonBody, System.Text.Encoding.UTF8, "application/json");

        using var response = await SendWithRedirectProtectionAsync(client, request, descriptor, context, effectiveCt);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(effectiveCt);
        using var jsonDoc = await JsonDocument.ParseAsync(stream, cancellationToken: effectiveCt);

        return ExtractRowsFromJson(jsonDoc.RootElement, descriptor.JsonRootPath);
    }

    private async Task<HttpResponseMessage> SendWithRedirectProtectionAsync(
        HttpClient client,
        HttpRequestMessage initialRequest,
        HttpEndpointDescriptor descriptor,
        DataSourceExecutionContext context,
        CancellationToken ct)
    {
        var currentRequest = initialRequest;
        var currentUrl = initialRequest.RequestUri?.ToString() ?? string.Empty;
        const int maxRedirects = 3;
        int redirectCount = 0;

        while (true)
        {
            var response = await client.SendAsync(currentRequest, ct);

            if (IsRedirectStatusCode(response.StatusCode) && response.Headers.Location != null)
            {
                if (redirectCount >= maxRedirects)
                {
                    response.Dispose();
                    throw new SecurityException($"Too many HTTP redirects (exceeded limit of {maxRedirects}).");
                }

                redirectCount++;
                var targetUri = response.Headers.Location.IsAbsoluteUri
                    ? response.Headers.Location
                    : new Uri(new Uri(currentUrl), response.Headers.Location);

                var targetUrl = targetUri.ToString();
                await ValidateDestinationUrl(targetUrl, ct);

                _logger.LogInformation("Following validated HTTP redirect #{Hop} from {Source} to {Target}",
                    redirectCount, currentUrl, targetUrl);

                response.Dispose();

                var newMethod = response.StatusCode == HttpStatusCode.SeeOther ? HttpMethod.Get : currentRequest.Method;
                var newRequest = new HttpRequestMessage(newMethod, targetUrl);
                ApplyHeadersAndAuth(newRequest, descriptor, context);

                currentRequest = newRequest;
                currentUrl = targetUrl;
                continue;
            }

            return response;
        }
    }

    private static bool IsRedirectStatusCode(HttpStatusCode code) =>
        code is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
             or HttpStatusCode.TemporaryRedirect or (HttpStatusCode)308;

    private async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ExecuteThrottledParallelRequestsAsync(
        HttpEndpointDescriptor descriptor,
        DataSourceExecutionContext context,
        IReadOnlyList<string> keys,
        CancellationToken ct)
    {
        var maxConcurrency = Math.Max(1, descriptor.MaxConcurrentRequests);
        using var throttle = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        var paramName = descriptor.PrimaryKeyField ?? "id";

        var allRows = new ConcurrentBag<IReadOnlyDictionary<string, object?>>();

        var tasks = keys.Select(async key =>
        {
            await throttle.WaitAsync(ct);
            try
            {
                var singleArgs = new Dictionary<string, object?>(context.Arguments)
                {
                    [paramName] = key
                };
                var rows = await ExecuteSingleRequestAsync(descriptor, context, singleArgs, ct);
                foreach (var r in rows)
                {
                    allRows.Add(r);
                }
            }
            finally
            {
                throttle.Release();
            }
        });

        await Task.WhenAll(tasks);
        return allRows.ToList();
    }

    private string BuildUrl(
        HttpEndpointDescriptor descriptor,
        IReadOnlyDictionary<string, object?> arguments,
        ClaimsPrincipal principal)
    {
        var baseUrl = descriptor.BaseUrl.TrimEnd('/');
        var pathTemplate = descriptor.PathTemplate.StartsWith('/')
            ? descriptor.PathTemplate
            : "/" + descriptor.PathTemplate;

        var usedArgs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Expand {param} path placeholders
        var expandedPath = pathTemplate;
        foreach (var (k, v) in arguments)
        {
            var placeholder = "{" + k + "}";
            if (expandedPath.Contains(placeholder, StringComparison.OrdinalIgnoreCase))
            {
                expandedPath = expandedPath.Replace(placeholder, Uri.EscapeDataString(v?.ToString() ?? string.Empty), StringComparison.OrdinalIgnoreCase);
                usedArgs.Add(k);
            }
        }

        // 2. Query parameters for remaining arguments
        var queryParams = new List<string>();
        foreach (var (k, v) in arguments)
        {
            if (!usedArgs.Contains(k) && v != null && v is not IEnumerable<object>)
            {
                queryParams.Add($"{Uri.EscapeDataString(k)}={Uri.EscapeDataString(v.ToString() ?? string.Empty)}");
            }
        }

        // 3. Tenant ID Pushdown as query parameter
        if (!string.IsNullOrWhiteSpace(descriptor.TenantIdQueryParam))
        {
            var tenantClaim = principal.FindFirst("tenant_id")?.Value
                              ?? principal.FindFirst("tid")?.Value
                              ?? principal.FindFirst("tenant")?.Value;
            if (!string.IsNullOrWhiteSpace(tenantClaim))
            {
                queryParams.Add($"{Uri.EscapeDataString(descriptor.TenantIdQueryParam)}={Uri.EscapeDataString(tenantClaim)}");
            }
        }

        var fullUrl = baseUrl + expandedPath;
        if (queryParams.Count > 0)
        {
            var separator = fullUrl.Contains('?') ? "&" : "?";
            fullUrl += separator + string.Join("&", queryParams);
        }

        return fullUrl;
    }

    internal async Task ValidateDestinationUrl(string fullUrl, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(fullUrl, UriKind.Absolute, out var uri))
        {
            throw new SecurityException($"Invalid destination URL: '{fullUrl}'.");
        }

        bool isDev = _environment?.IsDevelopment() ?? false;
        if (!isDev && !string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
        {
            throw new SecurityException($"Insecure HTTP scheme '{uri.Scheme}' not permitted for outbound data sources in non-development environments.");
        }

        var host = uri.Host.TrimEnd('.').ToLowerInvariant();

        // 1. Explicitly forbidden cloud metadata and cluster internal service hosts
        if (IsForbiddenMetadataHost(host))
        {
            throw new SecurityException($"Outbound access to cloud/cluster metadata service '{host}' is strictly forbidden.");
        }

        // 2. In non-dev, validate IP addresses (against Loopback, LinkLocal, RFC 1918, IPv6 equivalents)
        if (!isDev)
        {
            if (host == "localhost" || host == "127.0.0.1" || host == "::1" || host == "169.254.169.254")
            {
                throw new SecurityException($"Outbound access to private/loopback/metadata address '{host}' is strictly forbidden.");
            }

            IPAddress[] addresses;
            if (IPAddress.TryParse(host, out var directIp))
            {
                addresses = [directIp];
            }
            else
            {
                try
                {
                    addresses = await Dns.GetHostAddressesAsync(host, ct);
                }
                catch (SocketException ex)
                {
                    _logger.LogWarning(ex, "SSRF validation: Could not resolve host '{Host}' via DNS.", host);
                    addresses = [];
                }
            }

            foreach (var ip in addresses)
            {
                if (IsRestrictedIp(ip))
                {
                    throw new SecurityException($"Outbound access to private/loopback/restricted address '{ip}' is strictly forbidden.");
                }
            }
        }
    }

    public static void ValidateUrl(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        var host = uri.Host.TrimEnd('.').ToLowerInvariant();
        if (IsForbiddenMetadataHost(host))
        {
            throw new SecurityException($"Outbound access to cloud/cluster metadata service '{host}' is strictly forbidden.");
        }

        if (host == "localhost" || host == "127.0.0.1" || host == "::1" || host == "169.254.169.254")
        {
            throw new SecurityException($"Outbound access to private/loopback/metadata address '{host}' is strictly forbidden.");
        }

        if (IPAddress.TryParse(host, out var directIp) && IsRestrictedIp(directIp))
        {
            throw new SecurityException($"Outbound access to restricted IP address '{directIp}' is strictly forbidden.");
        }
    }

    public static bool IsForbiddenMetadataHost(string host)
    {
        var h = host.TrimEnd('.').ToLowerInvariant();
        return h == "metadata.google.internal" ||
               h.EndsWith(".metadata.google.internal", StringComparison.OrdinalIgnoreCase) ||
               h == "kubernetes.default.svc" ||
               h.EndsWith(".kubernetes.default.svc", StringComparison.OrdinalIgnoreCase) ||
               h.StartsWith("kubernetes.default.svc.", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsRestrictedIp(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        if (IPAddress.IsLoopback(ip))
        {
            return true;
        }

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = ip.GetAddressBytes();
            // RFC 1918: 10.0.0.0/8
            if (bytes[0] == 10) return true;
            // RFC 1918: 172.16.0.0/12 (172.16.0.0 - 172.31.255.255)
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
            // RFC 1918: 192.168.0.0/16
            if (bytes[0] == 192 && bytes[1] == 168) return true;
            // LinkLocal: 169.254.0.0/16
            if (bytes[0] == 169 && bytes[1] == 254) return true;
            // Current network: 0.0.0.0/8
            if (bytes[0] == 0) return true;
            // Broadcast: 255.255.255.255
            if (bytes[0] == 255 && bytes[1] == 255 && bytes[2] == 255 && bytes[3] == 255) return true;
        }
        else if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast)
            {
                return true;
            }

            var bytes = ip.GetAddressBytes();
            // Unique Local Address (ULA) fc00::/7 (RFC 4193: fc00:: to fdff::)
            if ((bytes[0] & 0xFE) == 0xFC)
            {
                return true;
            }

            // Unspecified address ::
            if (ip.Equals(IPAddress.IPv6Any))
            {
                return true;
            }
        }

        return false;
    }

    private void ApplyHeadersAndAuth(
        HttpRequestMessage request,
        HttpEndpointDescriptor descriptor,
        DataSourceExecutionContext context)
    {
        // 1. Forward configured headers from caller (enforcing security denylist)
        if (context.RequestHeaders != null && descriptor.ForwardHeaders.Count > 0)
        {
            foreach (var (targetHeader, sourceHeader) in descriptor.ForwardHeaders)
            {
                if (DisallowedForwardHeaders.Contains(targetHeader) || DisallowedForwardHeaders.Contains(sourceHeader))
                {
                    _logger.LogWarning("Security: Blocked forwarding of sensitive header '{Header}' downstream.", targetHeader);
                    continue;
                }

                if (context.RequestHeaders.TryGetValue(sourceHeader, out var vals) && vals.Length > 0)
                {
                    request.Headers.TryAddWithoutValidation(targetHeader, vals);
                }
            }
        }

        // 2. Tenant ID Header Pushdown - strip any forwarded value first
        if (!string.IsNullOrWhiteSpace(descriptor.TenantIdHeaderName))
        {
            request.Headers.Remove(descriptor.TenantIdHeaderName);
            var tenantClaim = context.Principal.FindFirst("tenant_id")?.Value
                              ?? context.Principal.FindFirst("tid")?.Value
                              ?? context.Principal.FindFirst("tenant")?.Value;
            if (!string.IsNullOrWhiteSpace(tenantClaim))
            {
                request.Headers.TryAddWithoutValidation(descriptor.TenantIdHeaderName, tenantClaim);
            }
        }

        // 3. User Identity Header Pushdown (X-User-Sid) - strip any forwarded value first
        request.Headers.Remove("X-User-Sid");
        var userSid = context.Principal.GetUserSid();
        if (userSid != null)
        {
            request.Headers.TryAddWithoutValidation("X-User-Sid", userSid.Value.Value);
        }

        // 4. Authentication Mode
        request.Headers.Remove("Authorization");
        switch (descriptor.AuthMode)
        {
            case HttpAuthMode.ForwardBearerToken:
                if (context.RequestHeaders != null &&
                    context.RequestHeaders.TryGetValue("Authorization", out var authVals) &&
                    authVals.Length > 0 &&
                    !string.IsNullOrWhiteSpace(authVals[0]))
                {
                    var authStr = authVals[0];
                    if (authStr.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                    {
                        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", authStr["Bearer ".Length..].Trim());
                    }
                    else
                    {
                        _logger.LogWarning("Security: Rejected forwarding non-Bearer Authorization header to downstream HTTP data source.");
                    }
                }
                break;

            case HttpAuthMode.StaticApiKey:
                if (!string.IsNullOrWhiteSpace(descriptor.ApiKeyHeaderName) &&
                    !string.IsNullOrWhiteSpace(descriptor.ApiKeySecretName))
                {
                    var resolvedKey = ResolveSecretValue(descriptor.ApiKeySecretName);
                    request.Headers.TryAddWithoutValidation(descriptor.ApiKeyHeaderName, resolvedKey);
                }
                break;

            case HttpAuthMode.ClientCredentials:
                if (!string.IsNullOrWhiteSpace(descriptor.ApiKeySecretName))
                {
                    var resolvedToken = ResolveSecretValue(descriptor.ApiKeySecretName);
                    request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", resolvedToken);
                }
                break;

            case HttpAuthMode.None:
            default:
                break;
        }
    }

    private string ResolveSecretValue(string secretRefOrValue)
    {
        if (_secretProvider != null)
        {
            try
            {
                var secretBytes = _secretProvider.GetSecretBytes(secretRefOrValue);
                if (secretBytes != null && secretBytes.Length > 0)
                {
                    return System.Text.Encoding.UTF8.GetString(secretBytes);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Secret reference could not be resolved by provider. Using literal value as fallback.");
            }
        }
        return secretRefOrValue;
    }

    public static IReadOnlyList<IReadOnlyDictionary<string, object?>> ExtractRowsFromJson(
        JsonElement root,
        string? jsonRootPath)
    {
        var targetElement = root;

        if (!string.IsNullOrWhiteSpace(jsonRootPath))
        {
            var segments = jsonRootPath.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var segment in segments)
            {
                if (targetElement.ValueKind == JsonValueKind.Object && targetElement.TryGetProperty(segment, out var prop))
                {
                    targetElement = prop;
                }
                else
                {
                    return Array.Empty<IReadOnlyDictionary<string, object?>>();
                }
            }
        }

        var results = new List<IReadOnlyDictionary<string, object?>>();

        if (targetElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in targetElement.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object)
                {
                    results.Add(FlattenJsonObject(item));
                }
            }
        }
        else if (targetElement.ValueKind == JsonValueKind.Object)
        {
            results.Add(FlattenJsonObject(targetElement));
        }

        return results;
    }

    private static IReadOnlyDictionary<string, object?> FlattenJsonObject(JsonElement obj)
    {
        var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        foreach (var prop in obj.EnumerateObject())
        {
            dict[prop.Name] = ConvertJsonElement(prop.Value);
        }

        return dict;
    }

    private static object? ConvertJsonElement(JsonElement elem)
    {
        return elem.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => elem.GetString(),
            JsonValueKind.Number => elem.TryGetInt64(out var l) ? l : elem.GetDouble(),
            _ => elem.GetRawText()
        };
    }
}
