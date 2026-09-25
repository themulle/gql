using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using GqlGateway.Application.Interfaces;
using GqlGateway.Domain.Common;
using GqlGateway.Domain.Model;

namespace GqlGateway.Application.Services;

public sealed partial class RowFilterSqlBuilder : IRowFilterSqlBuilder
{
    [GeneratedRegex("^[a-zA-Z_][a-zA-Z0-9_]*$")]
    private static partial Regex SafeIdentifierRegex();

    private static readonly HashSet<string> AllowedFilterOperators = new(StringComparer.OrdinalIgnoreCase)
    {
        "EQ", "NEQ", "LT", "GT", "LTE", "GTE", "LIKE", "IN"
    };

    private readonly IRlsFilterGenerator _rlsFilterGenerator;

    public RowFilterSqlBuilder(IRlsFilterGenerator? rlsFilterGenerator = null)
    {
        _rlsFilterGenerator = rlsFilterGenerator ?? RlsFilterGenerator.Instance;
    }

    public string? BuildCombinedRowFilter(
        IReadOnlyList<Consent> aConsents,
        IReadOnlyList<Consent> dConsents,
        DatabaseDialect dialect = DatabaseDialect.SqlServer)
    {
        // Consents in A: combined with OR. If ANY allow consent has NO row filters, all rows are allowed.
        var hasUnconstrainedAllow = aConsents.Any(c => c.RowFilters.Count == 0);

        string? aSql = null;
        if (!hasUnconstrainedAllow)
        {
            var consentPredicates = new List<string>();
            foreach (var consent in aConsents)
            {
                var consentSql = BuildConsentFilterPredicate(consent.RowFilters, dialect);
                if (!string.IsNullOrEmpty(consentSql))
                {
                    consentPredicates.Add(consentSql);
                }
            }

            if (consentPredicates.Count > 0)
            {
                aSql = consentPredicates.Count == 1
                    ? consentPredicates[0]
                    : $"({string.Join(" OR ", consentPredicates)})";
            }
        }

        // Filters in D: combined with AND NOT (...)
        // Each DENY consent represents an independent exclusion: NOT ((D1) OR (D2) ...)
        var denyPredicates = new List<string>();
        foreach (var denyConsent in dConsents)
        {
            var dPredicate = BuildConsentFilterPredicate(denyConsent.RowFilters, dialect);
            if (!string.IsNullOrEmpty(dPredicate))
            {
                denyPredicates.Add(dPredicate);
            }
        }

        string? dSql = null;
        if (denyPredicates.Count > 0)
        {
            var combinedDeny = denyPredicates.Count == 1
                ? denyPredicates[0]
                : $"({string.Join(" OR ", denyPredicates)})";
            dSql = $"NOT ({combinedDeny})";
        }

        if (aSql != null && dSql != null)
        {
            return $"({aSql} AND {dSql})";
        }
        if (aSql != null)
        {
            return aSql;
        }
        if (dSql != null)
        {
            return dSql;
        }

        return null;
    }

    private string? BuildConsentFilterPredicate(IReadOnlyList<ConsentRowFilter> filters, DatabaseDialect dialect)
    {
        if (filters.Count == 0) return null;

        // Group filters by FilterGroup: within group AND, between groups OR
        var groups = filters.GroupBy(f => f.FilterGroup).ToList();
        var groupPredicates = new List<string>();

        foreach (var group in groups)
        {
            var groupConditions = group.Select(f => FormatCondition(f, dialect)).Where(s => !string.IsNullOrEmpty(s)).ToList();
            if (groupConditions.Count > 0)
            {
                var groupSql = groupConditions.Count == 1
                    ? groupConditions[0]
                    : $"({string.Join(" AND ", groupConditions)})";
                groupPredicates.Add(groupSql);
            }
        }

        if (groupPredicates.Count == 0) return null;

        return groupPredicates.Count == 1
            ? groupPredicates[0]
            : $"({string.Join(" OR ", groupPredicates)})";
    }

    public string FormatCondition(ConsentRowFilter filter, DatabaseDialect dialect = DatabaseDialect.SqlServer)
    {
        if (filter.FilterType == RowFilterType.SubqueryCorrelated)
        {
            return _rlsFilterGenerator.BuildCorrelatedSubquery(filter, dialect);
        }

        if (filter.FilterType == RowFilterType.CrossSourceSetFilter)
        {
            return _rlsFilterGenerator.BuildCrossSourceSetFilter(filter, 500, dialect);
        }

        var col = filter.ColumnName;
        if (!SafeIdentifierRegex().IsMatch(col))
        {
            throw new InvalidOperationException($"Ungültiger Spaltenname im Zeilenfilter: '{col}'. Potenzieller Injection-Angriff.");
        }

        var quotedCol = dialect.QuoteIdentifier(col);

        var op = filter.Operator.ToUpperInvariant();
        if (!AllowedFilterOperators.Contains(op))
        {
            throw new InvalidOperationException($"Nicht unterstützter Operator '{filter.Operator}' im Zeilenfilter.");
        }

        var rawVal = filter.ValueJson;

        if (op == "IN")
        {
            try
            {
                using var inDoc = JsonDocument.Parse(rawVal);
                if (inDoc.RootElement.ValueKind != JsonValueKind.Array)
                {
                    throw new InvalidOperationException("ValueJson für IN-Operator muss ein gültiges JSON-Array sein.");
                }

                var items = inDoc.RootElement.EnumerateArray()
                    .Select(elem => FormatLiteral(elem, dialect))
                    .ToList();

                if (items.Count == 0) return "1 = 0";
                return $"{quotedCol} IN ({string.Join(", ", items)})";
            }
            catch (JsonException)
            {
                throw new InvalidOperationException("ValueJson für IN-Operator muss ein gültiges JSON-Array sein.");
            }
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(rawVal);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"ValueJson im Zeilenfilter muss valides JSON sein: '{rawVal}'.", ex);
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind == JsonValueKind.Null)
            {
                return op switch
                {
                    "EQ" => $"{quotedCol} IS NULL",
                    "NEQ" => $"{quotedCol} IS NOT NULL",
                    _ => throw new InvalidOperationException($"Operator '{op}' kann nicht mit NULL verglichen werden.")
                };
            }

            var formattedValue = FormatLiteral(doc.RootElement, dialect);

            return op switch
            {
                "EQ" => $"{quotedCol} = {formattedValue}",
                "NEQ" => $"{quotedCol} <> {formattedValue}",
                "LT" => $"{quotedCol} < {formattedValue}",
                "GT" => $"{quotedCol} > {formattedValue}",
                "LTE" => $"{quotedCol} <= {formattedValue}",
                "GTE" => $"{quotedCol} >= {formattedValue}",
                "LIKE" => $"{quotedCol} LIKE {formattedValue}",
                _ => $"{quotedCol} = {formattedValue}"
            };
        }
    }

    private static string FormatLiteral(JsonElement elem, DatabaseDialect dialect)
    {
        return elem.ValueKind switch
        {
            JsonValueKind.String => $"'{elem.GetString()?.Replace("'", "''")}'",
            JsonValueKind.Number when (elem.TryGetInt64(out _) || elem.TryGetDecimal(out _)) => elem.GetRawText(),
            JsonValueKind.True => (dialect == DatabaseDialect.SqlServer || dialect == DatabaseDialect.Oracle) ? "1" : "TRUE",
            JsonValueKind.False => (dialect == DatabaseDialect.SqlServer || dialect == DatabaseDialect.Oracle) ? "0" : "FALSE",
            JsonValueKind.Null => "NULL",
            _ => throw new InvalidOperationException($"Nicht unterstützter Literal-Typ im Zeilenfilter: {elem.ValueKind}")
        };
    }
}
