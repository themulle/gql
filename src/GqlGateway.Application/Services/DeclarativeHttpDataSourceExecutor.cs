using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GqlGateway.Application.Interfaces;
using GqlGateway.Domain.Common;
using GqlGateway.Domain.Model;
using Microsoft.Extensions.Logging;

namespace GqlGateway.Application.Services;

public sealed class DeclarativeHttpDataSourceExecutor : IDataSourceExecutor
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<DeclarativeHttpDataSourceExecutor> _logger;

    public DataSourceType SupportedType => DataSourceType.HttpDeclarative;

    public DeclarativeHttpDataSourceExecutor(
        IHttpClientFactory httpClientFactory,
        ILogger<DeclarativeHttpDataSourceExecutor> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
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
        var client = _httpClientFactory.CreateClient();
        using var timeoutCts = descriptor.Timeout > TimeSpan.Zero
            ? CancellationTokenSource.CreateLinkedTokenSource(ct)
            : null;
        if (timeoutCts != null)
        {
            timeoutCts.CancelAfter(descriptor.Timeout);
        }
        var effectiveCt = timeoutCts?.Token ?? ct;

        var url = BuildUrl(descriptor, arguments, context.Principal);
        var method = new HttpMethod(descriptor.Method ?? "GET");

        using var request = new HttpRequestMessage(method, url);
        ApplyHeadersAndAuth(request, descriptor, context);

        _logger.LogDebug("Executing Declarative HTTP {Method} {Url} for {Table}", method, url, context.Metadata.Identifier);

        using var response = await client.SendAsync(request, effectiveCt);
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
        var client = _httpClientFactory.CreateClient();
        using var timeoutCts = descriptor.Timeout > TimeSpan.Zero
            ? CancellationTokenSource.CreateLinkedTokenSource(ct)
            : null;
        if (timeoutCts != null)
        {
            timeoutCts.CancelAfter(descriptor.Timeout);
        }
        var effectiveCt = timeoutCts?.Token ?? ct;

        var url = BuildUrl(descriptor, context.Arguments, context.Principal);
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        ApplyHeadersAndAuth(request, descriptor, context);

        var jsonBody = JsonSerializer.Serialize(keys);
        request.Content = new StringContent(jsonBody, System.Text.Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request, effectiveCt);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(effectiveCt);
        using var jsonDoc = await JsonDocument.ParseAsync(stream, cancellationToken: effectiveCt);

        return ExtractRowsFromJson(jsonDoc.RootElement, descriptor.JsonRootPath);
    }

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

    private static string BuildUrl(
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

    private static void ApplyHeadersAndAuth(
        HttpRequestMessage request,
        HttpEndpointDescriptor descriptor,
        DataSourceExecutionContext context)
    {
        // 1. Forward configured headers from caller
        if (context.HttpContext != null && descriptor.ForwardHeaders.Count > 0)
        {
            foreach (var (targetHeader, sourceHeader) in descriptor.ForwardHeaders)
            {
                if (context.HttpContext.Request.Headers.TryGetValue(sourceHeader, out var vals) && vals.Count > 0)
                {
                    request.Headers.TryAddWithoutValidation(targetHeader, vals.ToArray());
                }
            }
        }

        // 2. Tenant ID Header Pushdown
        if (!string.IsNullOrWhiteSpace(descriptor.TenantIdHeaderName))
        {
            var tenantClaim = context.Principal.FindFirst("tenant_id")?.Value
                              ?? context.Principal.FindFirst("tid")?.Value
                              ?? context.Principal.FindFirst("tenant")?.Value;
            if (!string.IsNullOrWhiteSpace(tenantClaim))
            {
                request.Headers.TryAddWithoutValidation(descriptor.TenantIdHeaderName, tenantClaim);
            }
        }

        // 3. User Identity Header Pushdown (X-User-Sid)
        var userSid = context.Principal.GetUserSid();
        if (userSid != null)
        {
            request.Headers.TryAddWithoutValidation("X-User-Sid", userSid.Value.Value);
        }

        // 4. Authentication Mode
        switch (descriptor.AuthMode)
        {
            case HttpAuthMode.ForwardBearerToken:
                if (context.HttpContext != null &&
                    context.HttpContext.Request.Headers.TryGetValue("Authorization", out var authHeader) &&
                    !string.IsNullOrWhiteSpace(authHeader))
                {
                    var authStr = authHeader.ToString();
                    if (authStr.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                    {
                        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authStr["Bearer ".Length..].Trim());
                    }
                    else
                    {
                        request.Headers.TryAddWithoutValidation("Authorization", authStr);
                    }
                }
                break;

            case HttpAuthMode.StaticApiKey:
                if (!string.IsNullOrWhiteSpace(descriptor.ApiKeyHeaderName) &&
                    !string.IsNullOrWhiteSpace(descriptor.ApiKeySecretName))
                {
                    request.Headers.TryAddWithoutValidation(descriptor.ApiKeyHeaderName, descriptor.ApiKeySecretName);
                }
                break;

            case HttpAuthMode.ClientCredentials:
                if (!string.IsNullOrWhiteSpace(descriptor.ApiKeySecretName))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", descriptor.ApiKeySecretName);
                }
                break;

            case HttpAuthMode.None:
            default:
                break;
        }
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
