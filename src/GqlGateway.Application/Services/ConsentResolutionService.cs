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

        // Rule 3: Column Access Levels (Maximum over A, unless hard DENY from D)
        var allReferencedColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in applicable)
        {
            foreach (var cr in c.ColumnRules)
            {
                allReferencedColumns.Add(cr.ColumnName);
            }
        }

        var columnAccess = new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase);

        foreach (var column in allReferencedColumns)
        {
            if (hardDeniedColumns.Contains(column))
            {
                columnAccess[column] = ColumnAccessLevel.Deny;
                continue;
            }

            // Calculate maximum over A
            var maxLevel = ColumnAccessLevel.Deny;
            var isExplicitlyGrantedOrUnconstrained = false;

            foreach (var allowConsent in aConsents)
            {
                // Consent without column rule delivers CLEAR for all columns
                if (allowConsent.ColumnRules.Count == 0)
                {
                    maxLevel = ColumnAccessLevel.Clear;
                    isExplicitlyGrantedOrUnconstrained = true;
                    break;
                }

                var rule = allowConsent.ColumnRules.FirstOrDefault(r => string.Equals(r.ColumnName, column, StringComparison.OrdinalIgnoreCase));
                if (rule != null)
                {
                    isExplicitlyGrantedOrUnconstrained = true;
                    if (rule.AccessLevel > maxLevel)
                    {
                        maxLevel = rule.AccessLevel;
                    }
                }
            }

            // If no consent granted access to this column (and no unconstrained consent exists), default deny under Zero Trust
            if (!isExplicitlyGrantedOrUnconstrained)
            {
                maxLevel = ColumnAccessLevel.Deny;
            }

            columnAccess[column] = maxLevel;
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
