using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using GqlGateway.Application.OpenMetadata.Interfaces;
using GqlGateway.Application.OpenMetadata.Models;
using GqlGateway.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GqlGateway.Infrastructure.OpenMetadata;

public sealed class OpenMetadataClient : IOpenMetadataClient
{
    private readonly HttpClient _httpClient;
    private readonly IOptions<GatewayOptions> _options;
    private readonly ILogger<OpenMetadataClient> _logger;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private sealed record PagedResponse<T>(
        [property: JsonPropertyName("data")] List<T>? Data,
        [property: JsonPropertyName("paging")] PagingInfo? Paging);

    private sealed record PagingInfo(
        [property: JsonPropertyName("total")] int Total,
        [property: JsonPropertyName("after")] string? After);

    public OpenMetadataClient(
        HttpClient httpClient,
        IOptions<GatewayOptions> options,
        ILogger<OpenMetadataClient> logger)
    {
        _httpClient = httpClient;
        _options = options;
        _logger = logger;

        var omOptions = options.Value.OpenMetadata;
        if (!string.IsNullOrWhiteSpace(omOptions.ServerUrl) && _httpClient.BaseAddress == null)
        {
            var serverUrl = omOptions.ServerUrl.TrimEnd('/') + "/";
            _httpClient.BaseAddress = new Uri(serverUrl);
        }

        if (!string.IsNullOrWhiteSpace(omOptions.AuthToken) &&
            _httpClient.DefaultRequestHeaders.Authorization == null)
        {
            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", omOptions.AuthToken);
        }
    }

    public async Task<IReadOnlyList<OpenMetadataTable>> GetTablesAsync(string? service = null, CancellationToken ct = default)
    {
        var relativeUrl = "tables?limit=1000&fields=columns,tags,owners,database,databaseSchema,service";
        var tables = await GetPagedEntitiesAsync<OpenMetadataTable>(relativeUrl, ct);

        if (!string.IsNullOrWhiteSpace(service))
        {
            return tables.Where(t =>
                string.Equals(t.Service?.Name, service, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(t.Service?.FullyQualifiedName, service, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        return tables;
    }

    public async Task<OpenMetadataTable?> GetTableByFqnAsync(string fqn, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fqn);

        var relativeUrl = $"tables/name/{Uri.EscapeDataString(fqn)}?fields=columns,tags,owners,database,databaseSchema,service";
        using var response = await _httpClient.GetAsync(relativeUrl, ct);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync<OpenMetadataTable>(stream, JsonOptions, ct);
    }

    public Task<IReadOnlyList<OpenMetadataPolicy>> GetPoliciesAsync(CancellationToken ct = default) =>
        GetPagedEntitiesAsync<OpenMetadataPolicy>("policies?limit=1000&fields=rules,location", ct);

    public Task<IReadOnlyList<OpenMetadataRole>> GetRolesAsync(CancellationToken ct = default) =>
        GetPagedEntitiesAsync<OpenMetadataRole>("roles?limit=1000&fields=policies,users,teams", ct);

    public Task<IReadOnlyList<OpenMetadataTeam>> GetTeamsAsync(CancellationToken ct = default) =>
        GetPagedEntitiesAsync<OpenMetadataTeam>("teams?limit=1000&fields=defaultRoles,policies,users", ct);

    public Task<IReadOnlyList<OpenMetadataUser>> GetUsersAsync(CancellationToken ct = default) =>
        GetPagedEntitiesAsync<OpenMetadataUser>("users?limit=1000&fields=roles,teams", ct);

    private async Task<IReadOnlyList<T>> GetPagedEntitiesAsync<T>(string relativeUrl, CancellationToken ct)
    {
        try
        {
            using var response = await _httpClient.GetAsync(relativeUrl, ct);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var paged = await JsonSerializer.DeserializeAsync<PagedResponse<T>>(stream, JsonOptions, ct);
            return paged?.Data ?? (IReadOnlyList<T>)Array.Empty<T>();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to retrieve OpenMetadata entities from endpoint: {Url}", relativeUrl);
            throw;
        }
    }
}
