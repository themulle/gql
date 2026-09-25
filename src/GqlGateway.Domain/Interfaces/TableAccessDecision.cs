namespace GqlGateway.Domain.Interfaces;

public record TableAccessDecision(
    TableIdentifier Table,
    bool IsAllowed,
    IReadOnlyDictionary<string, ColumnAccessLevel> ColumnAccess,
    string? CombinedRowFilterSql,
    IReadOnlyList<string> DeniedReasons,
    bool HasUnconstrainedColumnAllow = false
)
{
    public ColumnAccessLevel GetColumnAccess(string columnName)
    {
        if (ColumnAccess.TryGetValue(columnName, out var lvl))
        {
            return lvl;
        }
        return HasUnconstrainedColumnAllow ? ColumnAccessLevel.Clear : ColumnAccessLevel.Deny;
    }

    public static TableAccessDecision Denied(TableIdentifier table, params string[] reasons) =>
        new(table, false, new Dictionary<string, ColumnAccessLevel>(), null, reasons, false);

    public static TableAccessDecision Allowed(
        TableIdentifier table,
        IReadOnlyDictionary<string, ColumnAccessLevel> columnAccess,
        string? rowFilterSql = null,
        bool hasUnconstrainedColumnAllow = false) =>
        new(table, true, columnAccess, rowFilterSql, Array.Empty<string>(), hasUnconstrainedColumnAllow);
}
