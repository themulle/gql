namespace GqlGateway.Application.OData.Interfaces;

using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Generates dynamic OpenAPI 3.1 specifications from registered table metadata and contracts (F-API-03).
/// </summary>
public interface IDynamicOpenApiGenerator
{
    /// <summary>
    /// Generates an OpenAPI 3.1 specification formatted as JSON.
    /// </summary>
    /// <param name="domainScope">Optional domain filter. If null or empty, includes all domains.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<string> GenerateOpenApiJsonAsync(string? domainScope = null, CancellationToken ct = default);

    /// <summary>
    /// Generates an OpenAPI 3.1 specification formatted as YAML.
    /// </summary>
    /// <param name="domainScope">Optional domain filter. If null or empty, includes all domains.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<string> GenerateOpenApiYamlAsync(string? domainScope = null, CancellationToken ct = default);
}
