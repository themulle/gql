using System.Collections.Generic;
using System.Linq;
using GqlGateway.Application.Interfaces;
using GqlGateway.Domain.Common;
using GqlGateway.Domain.Interfaces;
using GqlGateway.Domain.Model;

namespace GqlGateway.Application.Services;

public sealed class ConsentResolutionService : IConsentResolutionService
{
    private readonly IRowFilterSqlBuilder _sqlBuilder;

    public ConsentResolutionService(IRowFilterSqlBuilder? sqlBuilder = null)
    {
        _sqlBuilder = sqlBuilder ?? new RowFilterSqlBuilder();
    }

    public TableAccessDecision ResolveAccess(
        Sid userSid,
        IReadOnlySet<Sid> subjectGroupSids,
        IReadOnlySet<string> userRoles,
        TableIdentifier table,
        IReadOnlyList<Consent> activeConsents,
        DatabaseDialect dialect = DatabaseDialect.SqlServer)
    {
        var now = DateTimeOffset.UtcNow;

        // 1. Identify applicable active consents for Subject Set S
        var applicable = activeConsents
            .Where(c => c.IsActive(now) && c.TableIdentifier == table && IsSubjectMatch(c, userSid, subjectGroupSids, userRoles))
            .ToList();

        // Separate into A (ALLOW) and D (DENY)
        var aConsents = applicable.Where(c => c.Effect == ConsentEffect.Allow).ToList();
        var dConsents = applicable.Where(c => c.Effect == ConsentEffect.Deny).ToList();

        // Rule 1: Hard Table DENY
        // A deny consent with no column rules is considered a table-level DENY
        var hasTableDeny = dConsents.Any(c => c.ColumnRules.Count == 0 && c.RowFilters.Count == 0);
        if (hasTableDeny)
        {
            return TableAccessDecision.Denied(table, "Hard DENY: Active table-level DENY applies to subject.");
        }

        // Rule 2: Zero Trust - If A is empty, deny access
        if (aConsents.Count == 0)
        {
            return TableAccessDecision.Denied(table, "Zero Trust: No active ALLOW consent granted to subject.");
        }

        // Check for Column Hard-DENYs in D
        var hardDeniedColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in dConsents)
        {
            foreach (var colRule in c.ColumnRules)
            {
                if (colRule.AccessLevel == ColumnAccessLevel.Deny)
                {
                    hardDeniedColumns.Add(colRule.ColumnName);
                }
            }
        }

        // Rule 3: Column Access Levels (Bounded by row-filter scope to prevent consent-blending privilege escalation)
        var allReferencedColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in applicable)
        {
            foreach (var cr in c.ColumnRules)
            {
                allReferencedColumns.Add(cr.ColumnName);
            }
        }

        var columnAccess = new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase);
        var unconstrainedConsents = aConsents.Where(c => c.RowFilters.Count == 0).ToList();
        bool hasUnconstrained = unconstrainedConsents.Count > 0;

        foreach (var column in allReferencedColumns)
        {
            if (hardDeniedColumns.Contains(column))
            {
                columnAccess[column] = ColumnAccessLevel.Deny;
                continue;
            }

            if (hasUnconstrained)
            {
                // When unconstrained consents exist, table rows are unfiltered.
                // Columns can ONLY be elevated to what unconstrained consents grant, preventing
                // row-constrained grants (e.g. region='EU' -> Clear) from leaking cleartext on all rows!
                var unconstrainedMax = ColumnAccessLevel.Deny;
                var hasUnconstrainedGrant = false;

                foreach (var uc in unconstrainedConsents)
                {
                    if (uc.ColumnRules.Count == 0)
                    {
                        unconstrainedMax = ColumnAccessLevel.Clear;
                        hasUnconstrainedGrant = true;
                        break;
                    }

                    var r = uc.ColumnRules.FirstOrDefault(cr => string.Equals(cr.ColumnName, column, StringComparison.OrdinalIgnoreCase));
                    if (r != null)
                    {
                        hasUnconstrainedGrant = true;
                        if (r.AccessLevel > unconstrainedMax) unconstrainedMax = r.AccessLevel;
                    }
                }

                if (hasUnconstrainedGrant)
                {
                    columnAccess[column] = unconstrainedMax;
                }
                else
                {
                    // Column was ONLY granted under row-constrained consents, but table access is unconstrained.
                    // Under Zero Trust, column cannot be exposed globally without row constraint -> Deny globally
                    columnAccess[column] = ColumnAccessLevel.Deny;
                }
            }
            else
            {
                // All applicable allow consents are row-constrained.
                // If consents have diverging row filters with different access levels, choose the safest (minimum).
                var grantingConsents = new List<(Consent Consent, ColumnAccessLevel Level)>();
                foreach (var ac in aConsents)
                {
                    if (ac.ColumnRules.Count == 0)
                    {
                        grantingConsents.Add((ac, ColumnAccessLevel.Clear));
                    }
                    else
                    {
                        var r = ac.ColumnRules.FirstOrDefault(cr => string.Equals(cr.ColumnName, column, StringComparison.OrdinalIgnoreCase));
                        if (r != null)
                        {
                            grantingConsents.Add((ac, r.AccessLevel));
                        }
                    }
                }

                if (grantingConsents.Count == 0)
                {
                    columnAccess[column] = ColumnAccessLevel.Deny;
                }
                else
                {
                    var distinctFilters = grantingConsents
                        .Select(g => _sqlBuilder.BuildCombinedRowFilter(new[] { g.Consent }, Array.Empty<Consent>(), dialect))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Count();

                    if (distinctFilters <= 1)
                    {
                        columnAccess[column] = grantingConsents.Max(g => g.Level);
                    }
                    else
                    {
                        columnAccess[column] = grantingConsents.Min(g => g.Level);
                    }
                }
            }
        }

        // Rule 4: Row Predicate resolution delegated to dedicated IRowFilterSqlBuilder
        string? rowFilterSql = _sqlBuilder.BuildCombinedRowFilter(aConsents, dConsents, dialect);
        bool hasUnconstrainedColumnAllow = aConsents.Any(c => c.ColumnRules.Count == 0);

        return TableAccessDecision.Allowed(table, columnAccess, rowFilterSql, hasUnconstrainedColumnAllow);
    }

    private static bool IsSubjectMatch(
        Consent consent,
        Sid userSid,
        IReadOnlySet<Sid> subjectGroupSids,
        IReadOnlySet<string> userRoles)
    {
        switch (consent.GranteeType)
        {
            case GranteeType.User:
            case GranteeType.ServicePrincipal:
                return consent.GranteeSid.HasValue && consent.GranteeSid.Value == userSid;

            case GranteeType.Group:
                return consent.GranteeSid.HasValue && subjectGroupSids.Contains(consent.GranteeSid.Value);

            case GranteeType.Role:
                if (!string.IsNullOrEmpty(consent.RoleName) && userRoles.Contains(consent.RoleName))
                {
                    return true;
                }
                if (consent.RoleId.HasValue && userRoles.Contains(consent.RoleId.Value.ToString()))
                {
                    return true;
                }
                return false;

            default:
                return false;
        }
    }
}
