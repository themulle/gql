namespace GqlGateway.Domain.Model;

using System;
using System.Collections.Generic;

/// <summary>
/// Configuration and options for dynamic OpenAPI 3.1 document generation (F-API-03).
/// </summary>
public sealed record OpenApiDocumentOptions
{
    public string Title { get; init; } = "GqlGateway OData v4 Enterprise API";
    public string Version { get; init; } = "v1";
    public string Description { get; init; } = "High-performance OData v4 and Zero-Trust data exposure generated dynamically from table metadata and catalog contracts.";
    public string ServerUrl { get; init; } = "/odata/v4";
    public bool IncludeGovernanceMetadata { get; init; } = true;
}
