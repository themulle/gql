namespace GqlGateway.Infrastructure.DataCatalog;

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GqlGateway.Application.DataCatalog.Interfaces;
using GqlGateway.Application.DataCatalog.Models;
using GqlGateway.Application.Services;
using GqlGateway.Domain.Common;
using GqlGateway.Domain.Model;
using GqlGateway.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public sealed class OpenMetadataDataCatalogClient : IDataCatalogClient
{
    private readonly HttpClient _httpClient;
    private readonly IOptions<GatewayOptions> _options;
    private readonly ILogger<OpenMetadataDataCatalogClient> _logger;

    public DataCatalogProviderType ProviderType => DataCatalogProviderType.OpenMetadata;

    public OpenMetadataDataCatalogClient(
        HttpClient httpClient,
        IOptions<GatewayOptions> options,
        ILogger<OpenMetadataDataCatalogClient> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<IReadOnlyList<CatalogTableAsset>> GetTablesAsync(string? filter = null, CancellationToken ct = default)
    {
        var openMetaOpts = _options.Value.OpenMetadata;
        var serverUrl = string.IsNullOrWhiteSpace(openMetaOpts.ServerUrl) ? "https://openmetadata.corp.internal/api/v1" : openMetaOpts.ServerUrl.TrimEnd('/');
        DeclarativeHttpDataSourceExecutor.ValidateUrl(new Uri(serverUrl));

        var endpoint = $"{serverUrl}/tables?fields=columns,tags,owner,customProperties&limit=100";
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);

        if (!string.IsNullOrWhiteSpace(openMetaOpts.AuthToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", openMetaOpts.AuthToken);
        }

        using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("OpenMetadata tables query returned status {StatusCode}: {Reason}", response.StatusCode, response.ReasonPhrase);
            return [];
        }

        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return ParseOpenMetadataResponse(json);
    }

    public async Task<CatalogTableAsset?> GetTableAsync(TableIdentifier table, CancellationToken ct = default)
    {
        var tables = await GetTablesAsync(table.TableName, ct).ConfigureAwait(false);
        return tables.FirstOrDefault(t => t.Identifier.Equals(table));
    }

    private static IReadOnlyList<CatalogTableAsset> ParseOpenMetadataResponse(string json)
    {
        var result = new List<CatalogTableAsset>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                return result;
            }

            foreach (var item in data.EnumerateArray())
            {
                var name = item.TryGetProperty("name", out var n) ? n.GetString() ?? "unknown" : "unknown";
                var id = item.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;

                var columns = new List<CatalogColumnAsset>();
                if (item.TryGetProperty("columns", out var cols) && cols.ValueKind == JsonValueKind.Array)
                {
                    foreach (var col in cols.EnumerateArray())
                    {
                        var colName = col.TryGetProperty("name", out var cn) ? cn.GetString() ?? "col" : "col";
                        var dataType = col.TryGetProperty("dataType", out var dt) ? dt.GetString() ?? "varchar" : "varchar";
                        var tags = new List<string>();
                        if (col.TryGetProperty("tags", out var tList) && tList.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var tag in tList.EnumerateArray())
                            {
                                if (tag.TryGetProperty("tagFQN", out var tFqn) && tFqn.GetString() is { } s)
                                {
                                    tags.Add(s);
                                }
                            }
                        }

                        columns.Add(new CatalogColumnAsset
                        {
                            ColumnName = colName,
                            DataType = dataType,
                            Tags = tags
                        });
                    }
                }

                var tableId = new TableIdentifier("openmetadata", "default", name);
                result.Add(new CatalogTableAsset
                {
                    Identifier = tableId,
                    DisplayName = name,
                    ExternalAssetId = id,
                    Columns = columns,
                    SourceType = "OpenMetadata"
                });
            }
        }
        catch
        {
            // Resilient fallback on parsing error
        }

        return result;
    }
}
