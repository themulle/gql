namespace GqlGateway.Application.Sql;

using System;
using System.Text.RegularExpressions;
using Antlr4.Runtime.Misc;
using TrinoSqlEngine;

/// <summary>
/// Centralized security validation for SQL fragments, filters, predicates, and RLS expressions.
/// Enforces Zero-Trust principles using AST parsing to prevent SQL injection, query stacking, comment breakouts,
/// and RLS bypasses in compiled AST and runtime data source queries.
/// </summary>
public static partial class SqlSecurityValidator
{
    private static readonly FastSqlEngine Engine = new() { MaxQueryLength = 4096 };

    [GeneratedRegex(@"@([a-zA-Z0-9_]+)")]
    private static partial Regex ParameterTokenRegex();

    /// <summary>
    /// Validates a SQL predicate or filter expression to ensure it is free from SQL injection,
    /// statement terminators, comment breakouts, and unbalanced syntax using grammar-level AST parsing.
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

        if (predicate.Contains(';'))
        {
            throw new ArgumentException($"SQL predicate in '{fieldName}' contains prohibited statement terminator ';'.", fieldName);
        }

        if (predicate.Contains("--") || predicate.Contains("/*") || predicate.Contains("*/"))
        {
            throw new ArgumentException($"SQL predicate in '{fieldName}' contains prohibited comment sequence.", fieldName);
        }

        if (predicate.Contains("@@"))
        {
            throw new ArgumentException($"SQL predicate in '{fieldName}' contains prohibited SQL token '@@'.", fieldName);
        }

        // Fast normalization of query parameters (@p0, @tenant_id) to valid identifiers for AST verification
        string normalized = ParameterTokenRegex().Replace(predicate, "__param_$1");

        try
        {
            var (tree, tokens) = Engine.ParseExpression(normalized.AsMemory());
            if (tree == null || tree.expression() == null)
            {
                throw new ArgumentException($"SQL predicate in '{fieldName}' has invalid expression syntax.", fieldName);
            }
        }
        catch (ParseCanceledException ex)
        {
            throw new ArgumentException($"SQL predicate in '{fieldName}' contains invalid or unsafe SQL syntax: {ex.Message}", fieldName, ex);
        }
        catch (Exception ex) when (ex is not ArgumentException)
        {
            throw new ArgumentException($"SQL predicate in '{fieldName}' could not be parsed: {ex.Message}", fieldName, ex);
        }
    }
}
