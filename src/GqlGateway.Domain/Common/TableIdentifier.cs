using System;

namespace GqlGateway.Domain.Common;

public readonly record struct TableIdentifier(string Domain, string Schema, string TableName) : IEquatable<TableIdentifier>
{
    public override string ToString() => $"{Domain}.{Schema}.{TableName}";

    public string ToQualifiedName() => $"{Schema}.{TableName}";

    public bool Equals(TableIdentifier other) =>
        string.Equals(Domain, other.Domain, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Schema, other.Schema, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(TableName, other.TableName, StringComparison.OrdinalIgnoreCase);

    public override int GetHashCode() =>
        HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(Domain ?? string.Empty),
            StringComparer.OrdinalIgnoreCase.GetHashCode(Schema ?? string.Empty),
            StringComparer.OrdinalIgnoreCase.GetHashCode(TableName ?? string.Empty));

    public static TableIdentifier Parse(string s)
    {
        if (TryParse(s, out var id)) return id;
        throw new FormatException($"Invalid TableIdentifier format: '{s}'. Expected 'domain.schema.table' or 'schema.table'.");
    }

    public static bool TryParse(string? s, out TableIdentifier id)
    {
        id = default;
        if (string.IsNullOrWhiteSpace(s)) return false;
        var parts = s.Split('.');
        if (parts.Length == 3 && !string.IsNullOrWhiteSpace(parts[0]) && !string.IsNullOrWhiteSpace(parts[1]) && !string.IsNullOrWhiteSpace(parts[2]))
        {
            id = new TableIdentifier(parts[0].Trim(), parts[1].Trim(), parts[2].Trim());
            return true;
        }
        if (parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[0]) && !string.IsNullOrWhiteSpace(parts[1]))
        {
            id = new TableIdentifier("default", parts[0].Trim(), parts[1].Trim());
            return true;
        }
        return false;
    }
}
