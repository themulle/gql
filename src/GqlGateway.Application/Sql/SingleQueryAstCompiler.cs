namespace GqlGateway.Application.Sql;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using GqlGateway.Domain.Common;
using GqlGateway.Domain.Model;
using GqlGateway.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public sealed partial class SingleQueryAstCompiler : ISingleQueryAstCompiler
{
    [GeneratedRegex("^[a-zA-Z_][a-zA-Z0-9_]*$")]
    private static partial Regex SafeIdentifierRegex();

    private readonly int _maxDepth;
    private readonly List<DatabaseDialect>? _supportedDialects;
    private readonly ILogger<SingleQueryAstCompiler>? _logger;

    public SingleQueryAstCompiler(
        IOptions<GatewayOptions>? options = null,
        ILogger<SingleQueryAstCompiler>? logger = null)
    {
        var pushdownOpts = options?.Value.SingleQueryPushdown ?? new SingleQueryPushdownOptions();
        _maxDepth = pushdownOpts.MaxSubqueryDepth > 0 ? pushdownOpts.MaxSubqueryDepth : 5;
        _supportedDialects = pushdownOpts.SupportedDialects;
        _logger = logger;
    }

    public bool SupportsDialect(DatabaseDialect dialect)
    {
        if (_supportedDialects != null && _supportedDialects.Count > 0)
        {
            return _supportedDialects.Contains(dialect);
        }

        return dialect switch
        {
            DatabaseDialect.SqlServer or DatabaseDialect.PostgreSql or DatabaseDialect.Sqlite => true,
            _ => false
        };
    }

    public string CompileHierarchicalQuery(
        SqlAstNode rootNode,
        DatabaseDialect dialect,
        IReadOnlyDictionary<TableIdentifier, string?>? rlsPredicates = null)
    {
        ArgumentNullException.ThrowIfNull(rootNode);

        if (!SupportsDialect(dialect))
        {
            _logger?.LogWarning("Dialect '{Dialect}' does not support hierarchical JSON pushdown. Fallback to application-level batch stitching required.", dialect);
            throw new NotSupportedException($"Dialect '{dialect}' does not support single-query hierarchical JSON pushdown. Gateway must fall back to DataLoader / IChunkedQueryExecutor batching.");
        }

        var sb = new StringBuilder();
        CompileNode(rootNode, parentNode: null, dialect, rlsPredicates, depth: 1, sb);
        return sb.ToString();
    }

    private void CompileNode(
        SqlAstNode node,
        SqlAstNode? parentNode,
        DatabaseDialect dialect,
        IReadOnlyDictionary<TableIdentifier, string?>? rlsPredicates,
        int depth,
        StringBuilder sb)
    {
        if (depth > _maxDepth)
        {
            throw new InvalidOperationException($"Query depth limit ({_maxDepth}) exceeded at node '{node.Table.TableName}'. Potential circular query detected.");
        }

        ValidateIdentifier(node.Table.Schema);
        ValidateIdentifier(node.Table.TableName);
        ValidateIdentifier(node.Alias);

        var indent = new string(' ', depth * 2);

        sb.Append(indent).AppendLine("SELECT");

        // 1. Projected scalar columns with special type translation (geospatial, binary, timestamp)
        var selectItems = new List<string>(node.ProjectedColumns.Count + (node.Children?.Count ?? 0));
        if (parentNode != null && dialect == DatabaseDialect.PostgreSql)
        {
            foreach (var col in node.ProjectedColumns) ValidateIdentifier(col);
            var pairs = node.ProjectedColumns.Select(c => $"'{c}', {BuildColumnExpression(c, node, dialect)}");
            var buildObject = $"json_build_object({string.Join(", ", pairs)})";
            selectItems.Add($"{indent}  json_agg({buildObject})");
        }
        else if (parentNode != null && dialect == DatabaseDialect.Sqlite)
        {
            foreach (var col in node.ProjectedColumns) ValidateIdentifier(col);
            var pairs = node.ProjectedColumns.Select(c => $"'{c}', {BuildColumnExpression(c, node, dialect)}");
            var buildObject = $"json_object({string.Join(", ", pairs)})";
            selectItems.Add($"{indent}  json_group_array({buildObject})");
        }
        else
        {
            foreach (var col in node.ProjectedColumns)
            {
                ValidateIdentifier(col);
                var colExpr = $"{BuildColumnExpression(col, node, dialect)} AS {QuoteIdentifier(col, dialect)}";
                selectItems.Add($"{indent}  {colExpr}");
            }
        }

        // 2. Nested Child Subqueries
        if (node.Children != null && node.Children.Count > 0)
        {
            foreach (var child in node.Children)
            {
                var childSubquery = CompileChildSubquery(child, parentNode: node, dialect, rlsPredicates, depth + 1);
                selectItems.Add(childSubquery);
            }
        }

        sb.AppendLine(string.Join(",\n", selectItems));

        // 3. FROM Clause
        var fromTable = $"{QuoteIdentifier(node.Table.Schema, dialect)}.{QuoteIdentifier(node.Table.TableName, dialect)} {QuoteIdentifier(node.Alias, dialect)}";
        sb.Append(indent).Append("FROM ").AppendLine(fromTable);

        // 4. WHERE Clause: Join Predicates + User Filter + Mandatory RLS
        var whereClauses = new List<string>();

        if (parentNode != null && !string.IsNullOrWhiteSpace(node.ChildForeignKeyColumn) && !string.IsNullOrWhiteSpace(node.ParentForeignKeyColumn))
        {
            ValidateIdentifier(node.ChildForeignKeyColumn);
            ValidateIdentifier(node.ParentForeignKeyColumn);
            whereClauses.Add($"{QuoteIdentifier(node.Alias, dialect)}.{QuoteIdentifier(node.ChildForeignKeyColumn, dialect)} = {QuoteIdentifier(parentNode.Alias, dialect)}.{QuoteIdentifier(node.ParentForeignKeyColumn, dialect)}");
        }

        if (!string.IsNullOrWhiteSpace(node.WhereFilter))
        {
            whereClauses.Add($"({node.WhereFilter})");
        }

        // VULN-08: Multi-level RLS Enforcement - RLS is MANDATORY at every level
        if (rlsPredicates == null || !rlsPredicates.TryGetValue(node.Table, out var rlsFilter) || string.IsNullOrWhiteSpace(rlsFilter))
        {
            throw new InvalidOperationException($"Mandatory RLS predicate missing for table '{node.Table.TableName}' in single-query AST pushdown.");
        }
        whereClauses.Add($"({rlsFilter})");

        if (whereClauses.Count > 0)
        {
            sb.Append(indent).Append("WHERE ").AppendLine(string.Join(" AND ", whereClauses));
        }

        // 5. Dialect-specific JSON Aggregation for Child Nodes
        if (parentNode != null)
        {
            AppendChildJsonFormatting(node, dialect, indent, sb);
        }
    }

    private string CompileChildSubquery(
        SqlAstNode childNode,
        SqlAstNode parentNode,
        DatabaseDialect dialect,
        IReadOnlyDictionary<TableIdentifier, string?>? rlsPredicates,
        int depth)
    {
        ValidateIdentifier(childNode.Alias);
        var indent = new string(' ', depth * 2);
        var sb = new StringBuilder();

        sb.Append(indent).AppendLine("(");
        CompileNode(childNode, parentNode, dialect, rlsPredicates, depth, sb);
        sb.Append(indent).Append(") AS ").Append(QuoteIdentifier(childNode.Alias, dialect));

        return sb.ToString();
    }

    private static void AppendChildJsonFormatting(SqlAstNode node, DatabaseDialect dialect, string indent, StringBuilder sb)
    {
        switch (dialect)
        {
            case DatabaseDialect.SqlServer:
                sb.Append(indent).AppendLine("FOR JSON PATH");
                break;
            case DatabaseDialect.PostgreSql:
            case DatabaseDialect.Sqlite:
            default:
                break;
        }
    }

    private static string BuildColumnExpression(string col, SqlAstNode node, DatabaseDialect dialect)
    {
        var quotedCol = $"{QuoteIdentifier(node.Alias, dialect)}.{QuoteIdentifier(col, dialect)}";
        if (node.ColumnTypes == null || !node.ColumnTypes.TryGetValue(col, out var rawType) || string.IsNullOrWhiteSpace(rawType))
        {
            return quotedCol;
        }

        var normalizedType = rawType.Trim().ToLowerInvariant();

        // 1. Geospatial Types (geometry, geography, point, polygon, linestring)
        if (normalizedType is "geometry" or "geography" or "spatial" or "point" or "polygon" or "linestring" or "multipolygon" or "multipoint")
        {
            return dialect switch
            {
                DatabaseDialect.PostgreSql => $"ST_AsGeoJSON({quotedCol})",
                DatabaseDialect.SqlServer => $"{quotedCol}.STAsText()",
                DatabaseDialect.Sqlite => $"AsGeoJSON({quotedCol})",
                _ => quotedCol
            };
        }

        // 2. Binary Types (bytea, varbinary, binary, blob, image)
        if (normalizedType is "bytea" or "binary" or "varbinary" or "blob" or "image")
        {
            return dialect switch
            {
                DatabaseDialect.PostgreSql => $"encode({quotedCol}, 'base64')",
                DatabaseDialect.Sqlite => $"hex({quotedCol})",
                DatabaseDialect.SqlServer => quotedCol, // SQL Server FOR JSON PATH converts varbinary automatically to base64
                _ => quotedCol
            };
        }

        // 3. High-precision / timezone timestamps (timestamp, timestamptz, datetime2, datetimeoffset)
        if (normalizedType is "timestamptz" or "datetimeoffset" or "datetime2")
        {
            return dialect switch
            {
                DatabaseDialect.PostgreSql => $"to_char({quotedCol}, 'YYYY-MM-DD\"T\"HH24:MI:SS.US\"Z\"')",
                DatabaseDialect.SqlServer => $"CONVERT(VARCHAR(33), {quotedCol}, 126)",
                _ => quotedCol
            };
        }

        return quotedCol;
    }

    private static void ValidateIdentifier(string? identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier) || !SafeIdentifierRegex().IsMatch(identifier))
        {
            throw new ArgumentException($"Invalid SQL identifier '{identifier}'. Identifiers must contain only alphanumeric characters and underscores.");
        }
    }

    private static string QuoteIdentifier(string identifier, DatabaseDialect dialect) => dialect switch
    {
        DatabaseDialect.SqlServer => $"[{identifier}]",
        DatabaseDialect.PostgreSql or DatabaseDialect.Sqlite => $"\"{identifier}\"",
        _ => $"\"{identifier}\""
    };
}
