using System.Text.RegularExpressions;

namespace GqlGateway.Domain.Common;

public enum DatabaseDialect
{
    PostgreSql = 1,
    SqlServer = 2,
    Sqlite = 3,
    Databricks = 4,
    Oracle = 5
}

public static class DatabaseDialectExtensions
{
    private static readonly Regex SafeIdentifierRegex =
        new(@"^[a-zA-Z_][a-zA-Z0-9_]*$", RegexOptions.Compiled);

    public static void ValidateIdentifier(string id, string paramName = "Identifier")
    {
        if (string.IsNullOrWhiteSpace(id) || !SafeIdentifierRegex.IsMatch(id))
        {
            throw new ArgumentException($"Ungültiger Bezeichner '{id}'. Erlaubt sind nur alphanumerische Zeichen und Unterstriche, beginnend mit einem Buchstaben oder Unterstrich.", paramName);
        }
    }

    public static string QuoteIdentifier(this DatabaseDialect dialect, string identifier)
    {
        ValidateIdentifier(identifier);
        return dialect switch
        {
            DatabaseDialect.SqlServer => $"[{identifier}]",
            DatabaseDialect.Databricks => $"`{identifier}`",
            DatabaseDialect.Oracle or DatabaseDialect.PostgreSql or DatabaseDialect.Sqlite => $"\"{identifier}\"",
            _ => $"\"{identifier}\""
        };
    }

    public static string QuoteQualifiedColumn(this DatabaseDialect dialect, string qualifiedColumn)
    {
        if (string.IsNullOrWhiteSpace(qualifiedColumn))
        {
            throw new ArgumentException("Qualified column cannot be empty.", nameof(qualifiedColumn));
        }

        var parts = qualifiedColumn.Split('.');
        foreach (var part in parts)
        {
            ValidateIdentifier(part);
        }

        return string.Join(".", parts.Select(p => dialect.QuoteIdentifier(p)));
    }

    public static string FormatTableIdentifier(this DatabaseDialect dialect, TableIdentifier table)
    {
        ValidateIdentifier(table.Schema, "Schema");
        ValidateIdentifier(table.TableName, "TableName");

        var schemaQuoted = dialect.QuoteIdentifier(table.Schema);
        var tableQuoted = dialect.QuoteIdentifier(table.TableName);
        return $"{schemaQuoted}.{tableQuoted}";
    }

    public static DatabaseDialect ParseDialect(string? sourceType)
    {
        if (string.IsNullOrWhiteSpace(sourceType))
        {
            return DatabaseDialect.PostgreSql;
        }

        return sourceType.Trim().ToLowerInvariant() switch
        {
            "mssql" or "sqlserver" or "sql_server" or "microsoft sql server" => DatabaseDialect.SqlServer,
            "sqlite" or "sqlite3" => DatabaseDialect.Sqlite,
            "postgres" or "postgresql" or "pgsql" or "npgsql" => DatabaseDialect.PostgreSql,
            "databricks" or "spark" or "sparksql" => DatabaseDialect.Databricks,
            "oracle" or "oracledb" or "odp" => DatabaseDialect.Oracle,
            _ => Enum.TryParse<DatabaseDialect>(sourceType, true, out var d) ? d : DatabaseDialect.PostgreSql
        };
    }
}
