namespace GqlGateway.Application.Sql;

using System;
using System.Security;

/// <summary>
/// Centralized security validation for SQL fragments, filters, predicates, and RLS expressions.
/// Enforces Zero-Trust principles to prevent SQL injection, query stacking, comment breakouts,
/// and RLS bypasses in compiled AST and runtime data source queries.
/// </summary>
public static class SqlSecurityValidator
{
    private static readonly string[] DangerousSqlTokens =
    [
        "--", "/*", "*/", ";", "@@",
        "DROP ", "ALTER ", "TRUNCATE ", "DELETE ", "INSERT ", "UPDATE ", "EXEC ", "EXECUTE ",
        "UNION ", "INTO ", "XP_", "SP_", "MERGE "
    ];

    /// <summary>
    /// Validates a SQL predicate or filter expression to ensure it is free from SQL injection tokens,
    /// statement terminators, comment breakouts, and unbalanced syntax.
    /// </summary>
    public static void ValidatePredicateSql(string? predicate, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(predicate))
        {
            return;
        }

        if (predicate.Length > 2000)
        {
            throw new ArgumentException($"SQL predicate in '{fieldName}' exceeds maximum allowed length of 2000 characters.", fieldName);
        }

        if (predicate.Contains('\0'))
        {
            throw new ArgumentException($"SQL predicate in '{fieldName}' contains prohibited null byte.", fieldName);
        }

        foreach (var token in DangerousSqlTokens)
        {
            if (predicate.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"SQL predicate in '{fieldName}' contains prohibited SQL token or comment sequence '{token.Trim()}'.", fieldName);
            }
        }

        // Verify balanced parentheses and single quotes (ignoring escaped quotes '')
        var inQuote = false;
        var parenDepth = 0;
        for (int i = 0; i < predicate.Length; i++)
        {
            var ch = predicate[i];
            if (ch == '\'')
            {
                if (inQuote && i + 1 < predicate.Length && predicate[i + 1] == '\'')
                {
                    i++; // skip escaped quote ''
                    continue;
                }
                inQuote = !inQuote;
            }
            else if (!inQuote)
            {
                if (ch == '(') parenDepth++;
                else if (ch == ')')
                {
                    parenDepth--;
                    if (parenDepth < 0)
                    {
                        throw new ArgumentException($"SQL predicate in '{fieldName}' contains unbalanced closing parenthesis.", fieldName);
                    }
                }
            }
        }

        if (inQuote)
        {
            throw new ArgumentException($"SQL predicate in '{fieldName}' contains unclosed string literal.", fieldName);
        }

        if (parenDepth != 0)
        {
            throw new ArgumentException($"SQL predicate in '{fieldName}' contains unbalanced parentheses.", fieldName);
        }
    }
}
