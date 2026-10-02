namespace GqlGateway.Api.Endpoints;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using GqlGateway.Api.Serialization;
using GqlGateway.Application.Serialization;
using GqlGateway.Domain.Common;
using GqlGateway.Domain.Model;
using GqlGateway.Domain.Options;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public static partial class ExportEndpoints
{
    [GeneratedRegex(@"^[a-zA-Z0-9_]+$", RegexOptions.Compiled)]
    private static partial Regex SafeIdentifierRegex();

    public static IEndpointRouteBuilder MapExportEndpoints(this IEndpointRouteBuilder app)
    {
        // F-DATA-01: Hierarchical Parquet Egress
        app.MapGet("/api/export/parquet/{domain}/{table}", async (
            string domain,
            string table,
            string? columns,
            int? limit,
            bool? flatten,
            HttpContext context,
            IParquetExportService parquetService,
            IOptions<GatewayOptions> options) =>
        {
            if (!options.Value.ParquetEgress.Enabled)
            {
                return Results.NotFound(new { error = "Parquet egress is disabled." });
            }

            // VULN-02: Validate domain & table identifiers
            if (!SafeIdentifierRegex().IsMatch(domain) || !SafeIdentifierRegex().IsMatch(table))
            {
                return Results.BadRequest(new { error = "Invalid domain or table identifier. Only alphanumeric and underscore characters are allowed." });
            }

            var colList = string.IsNullOrWhiteSpace(columns)
                ? (IReadOnlyList<string>)Array.Empty<string>()
                : columns.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            var exportRequest = new ParquetExportRequest(
                Table: new TableIdentifier(domain, "public", table),
                Columns: colList,
                Limit: limit ?? options.Value.ParquetEgress.MaxRowsPerFile,
                FlattenNested: flatten ?? options.Value.ParquetEgress.FlattenNestedStructures
            );

            // Direct endpoint invocation without an executing query pipeline returns a typed Parquet skeleton/schema file (zero rows).
            // Governed data exports (RLS, masking, consent) are requested on the data channels themselves via
            // 'Accept: application/vnd.apache.parquet' (GraphQL, WebSQL, SQL endpoints, OData entity sets).
            var schemaSkeletonRows = new List<IReadOnlyDictionary<string, object?>>();

            var exportResult = await parquetService.ExportToParquetAsync(exportRequest, schemaSkeletonRows, context.RequestAborted);

            context.Response.Headers["X-Export-Truncated"] = exportResult.IsTruncated ? "true" : "false";
            context.Response.Headers["X-Row-Count"] = exportResult.RowCount.ToString();

            return Results.File(
                fileContents: exportResult.Data,
                contentType: exportResult.ContentType,
                fileDownloadName: exportResult.SuggestedFileName
            );
        })
        .WithMetadata(new ParquetOutputSupportedMetadata())
        .RequireAuthorization();

        return app;
    }
}
