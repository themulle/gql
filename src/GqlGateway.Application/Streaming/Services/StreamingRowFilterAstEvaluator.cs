namespace GqlGateway.Application.Streaming.Services;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Antlr4.Runtime;
using Antlr4.Runtime.Misc;
using TrinoSqlEngine;

/// <summary>
/// High-throughput AST-based evaluator for SQL Row-Level Security (RLS) predicates on in-memory streaming CDC events.
/// Replaces brittle regexes with full AST parsing supporting comparisons, IN, LIKE, IS NULL, BETWEEN, and nested boolean logic.
/// </summary>
public static partial class StreamingRowFilterAstEvaluator
{
    private static readonly FastSqlEngine Engine = new() { MaxQueryLength = 4096 };
    private static readonly ConcurrentDictionary<string, SqlBaseParser.StandaloneExpressionContext?> ExpressionCache = new(StringComparer.Ordinal);
    private const int MaxCacheSize = 1000;

    [GeneratedRegex(@"@([a-zA-Z0-9_]+)")]
    private static partial Regex ParameterTokenRegex();

    public static bool Matches(IReadOnlyDictionary<string, object?> payload, string? filterSql)
    {
        if (string.IsNullOrWhiteSpace(filterSql))
        {
            return true;
        }

        if (payload == null || payload.Count == 0)
        {
            return false;
        }

        var ast = GetOrParseAst(filterSql);
        if (ast == null)
        {
            // Unparseable or unsupported subquery filter -> fail-closed for zero-trust security
            return false;
        }

        try
        {
            return EvaluateBoolean(ast.expression(), payload);
        }
        catch
        {
            // Any evaluation error -> fail-closed
            return false;
        }
    }

    private static SqlBaseParser.StandaloneExpressionContext? GetOrParseAst(string filterSql)
    {
        if (ExpressionCache.TryGetValue(filterSql, out var cached))
        {
            return cached;
        }

        if (ExpressionCache.Count >= MaxCacheSize)
        {
            ExpressionCache.Clear();
        }

        SqlBaseParser.StandaloneExpressionContext? parsed = null;
        try
        {
            // Normalize parameters (@p0, @tenant) into valid identifiers for AST parser
            string normalized = ParameterTokenRegex().Replace(filterSql, "__param_$1");
            var (tree, _) = Engine.ParseExpression(normalized.AsMemory());
            parsed = tree;
        }
        catch
        {
            parsed = null;
        }

        ExpressionCache[filterSql] = parsed;
        return parsed;
    }

    private static bool EvaluateBoolean(RuleContext? ctx, IReadOnlyDictionary<string, object?> payload)
    {
        if (ctx == null) return false;

        return ctx switch
        {
            SqlBaseParser.ExpressionContext exprCtx =>
                EvaluateBoolean(exprCtx.booleanExpression(), payload),

            SqlBaseParser.AndContext andCtx =>
                EvaluateBoolean(andCtx.booleanExpression(0), payload) && EvaluateBoolean(andCtx.booleanExpression(1), payload),

            SqlBaseParser.OrContext orCtx =>
                EvaluateBoolean(orCtx.booleanExpression(0), payload) || EvaluateBoolean(orCtx.booleanExpression(1), payload),

            SqlBaseParser.LogicalNotContext notCtx =>
                !EvaluateBoolean(notCtx.booleanExpression(), payload),

            SqlBaseParser.ParenthesizedExpressionContext parenCtx =>
                EvaluateBoolean(parenCtx.expression(), payload),

            SqlBaseParser.PredicatedContext predCtx =>
                EvaluatePredicated(predCtx, payload),

            _ => EvaluateChildExpressions(ctx, payload)
        };
    }

    private static bool EvaluatePredicated(SqlBaseParser.PredicatedContext ctx, IReadOnlyDictionary<string, object?> payload)
    {
        var predicate = ctx.predicate();

        if (predicate == null)
        {
            if (ctx.valueExpression() is SqlBaseParser.ValueExpressionDefaultContext def &&
                def.primaryExpression() is SqlBaseParser.ParenthesizedExpressionContext paren)
            {
                return EvaluateBoolean(paren.expression(), payload);
            }

            var innerVal = ResolveValue(ctx.valueExpression(), payload);
            return innerVal is true || (innerVal is string s && bool.TryParse(s, out var b) && b);
        }

        var leftVal = ResolveValue(ctx.valueExpression(), payload);
        return predicate switch
        {
            SqlBaseParser.ComparisonContext comp =>
                EvaluateComparison(leftVal, comp.comparisonOperator(), ResolveValue(comp.right, payload)),

            SqlBaseParser.InListContext inList =>
                EvaluateInList(leftVal, inList, payload),

            SqlBaseParser.NullPredicateContext nullPred =>
                nullPred.NOT() != null ? leftVal != null : leftVal == null,

            SqlBaseParser.LikeContext likeCtx =>
                EvaluateLike(leftVal, likeCtx, payload),

            SqlBaseParser.BetweenContext betweenCtx =>
                EvaluateBetween(leftVal, betweenCtx, payload),

            _ => false
        };
    }

    private static bool EvaluateComparison(object? left, SqlBaseParser.ComparisonOperatorContext? op, object? right)
    {
        if (op == null) return false;

        string opText = op.GetText().Trim();

        // Null comparison semantics: NULL = NULL is UNKNOWN (false in WHERE)
        if (left == null || right == null)
        {
            return opText is "!=" or "<>" && (left != null || right != null);
        }

        // Numeric comparison
        if (TryConvertToDecimal(left, out decimal leftNum) && TryConvertToDecimal(right, out decimal rightNum))
        {
            int cmp = leftNum.CompareTo(rightNum);
            return opText switch
            {
                "=" or "==" => cmp == 0,
                "!=" or "<>" => cmp != 0,
                "<" => cmp < 0,
                "<=" => cmp <= 0,
                ">" => cmp > 0,
                ">=" => cmp >= 0,
                _ => false
            };
        }

        // String / default comparison
        string sLeft = left.ToString() ?? string.Empty;
        string sRight = right.ToString() ?? string.Empty;
        int strCmp = string.Compare(sLeft, sRight, StringComparison.OrdinalIgnoreCase);

        return opText switch
        {
            "=" or "==" => strCmp == 0,
            "!=" or "<>" => strCmp != 0,
            "<" => strCmp < 0,
            "<=" => strCmp <= 0,
            ">" => strCmp > 0,
            ">=" => strCmp >= 0,
            _ => false
        };
    }

    private static bool EvaluateInList(object? left, SqlBaseParser.InListContext inList, IReadOnlyDictionary<string, object?> payload)
    {
        if (left == null) return false;

        bool hasMatch = false;
        var expressions = inList.expression();
        if (expressions != null)
        {
            foreach (var expr in expressions)
            {
                var val = ResolveValue(expr, payload);
                if (EvaluateComparison(left, null, val) || string.Equals(left.ToString(), val?.ToString(), StringComparison.OrdinalIgnoreCase))
                {
                    hasMatch = true;
                    break;
                }
            }
        }

        return inList.NOT() != null ? !hasMatch : hasMatch;
    }

    private static bool EvaluateLike(object? left, SqlBaseParser.LikeContext likeCtx, IReadOnlyDictionary<string, object?> payload)
    {
        if (left == null) return false;

        var patternVal = ResolveValue(likeCtx.pattern, payload)?.ToString();
        if (patternVal == null) return false;

        var sb = new StringBuilder("^", patternVal.Length * 2 + 2);
        for (int i = 0; i < patternVal.Length; i++)
        {
            char c = patternVal[i];
            if (c == '%')
            {
                sb.Append(".*");
            }
            else if (c == '_')
            {
                sb.Append('.');
            }
            else
            {
                sb.Append(Regex.Escape(c.ToString()));
            }
        }
        sb.Append('$');

        bool isMatch = false;
        try
        {
            isMatch = Regex.IsMatch(left.ToString() ?? string.Empty, sb.ToString(), RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200));
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }

        return likeCtx.NOT() != null ? !isMatch : isMatch;
    }

    private static bool EvaluateBetween(object? left, SqlBaseParser.BetweenContext betweenCtx, IReadOnlyDictionary<string, object?> payload)
    {
        if (left == null) return false;

        var lowerVal = ResolveValue(betweenCtx.lower, payload);
        var upperVal = ResolveValue(betweenCtx.upper, payload);

        if (TryConvertToDecimal(left, out decimal valNum) &&
            TryConvertToDecimal(lowerVal, out decimal lowNum) &&
            TryConvertToDecimal(upperVal, out decimal upNum))
        {
            bool inRange = valNum >= lowNum && valNum <= upNum;
            return betweenCtx.NOT() != null ? !inRange : inRange;
        }

        return false;
    }

    private static object? ResolveValue(RuleContext? ctx, IReadOnlyDictionary<string, object?> payload)
    {
        if (ctx == null) return null;

        string text = ctx.GetText();

        // Check if string literal
        if (text.StartsWith('\'') && text.EndsWith('\'') && text.Length >= 2)
        {
            return text[1..^1].Replace("''", "'");
        }

        // Check if numeric literal
        if (decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal num))
        {
            return num;
        }

        // Check boolean literal
        if (bool.TryParse(text, out bool b))
        {
            return b;
        }

        if (string.Equals(text, "NULL", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // Identifier lookup: strip quotes if present ("col" or [col])
        string colName = SqlIdentifierHelper.NormalizeIdentifier(text);
        if (payload.TryGetValue(colName, out var payloadVal))
        {
            return payloadVal;
        }

        // If identifier has table prefix (e.g. c.email), strip prefix and lookup column
        int dotIdx = colName.LastIndexOf('.');
        if (dotIdx >= 0 && dotIdx + 1 < colName.Length)
        {
            string simpleCol = colName[(dotIdx + 1)..];
            if (payload.TryGetValue(simpleCol, out var simpleVal))
            {
                return simpleVal;
            }
        }

        return null;
    }

    private static bool EvaluateChildExpressions(RuleContext ctx, IReadOnlyDictionary<string, object?> payload)
    {
        for (int i = 0; i < ctx.ChildCount; i++)
        {
            if (ctx.GetChild(i) is RuleContext child)
            {
                if (EvaluateBoolean(child, payload)) return true;
            }
        }
        return false;
    }

    private static bool TryConvertToDecimal(object? val, out decimal result)
    {
        if (val is null)
        {
            result = 0;
            return false;
        }

        if (val is decimal d) { result = d; return true; }
        if (val is int i) { result = i; return true; }
        if (val is long l) { result = l; return true; }
        if (val is short s) { result = s; return true; }
        if (val is byte b) { result = b; return true; }
        if (val is float f) { result = (decimal)f; return true; }
        if (val is double db) { result = (decimal)db; return true; }

        return decimal.TryParse(val.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out result);
    }
}
