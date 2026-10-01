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
/// High-throughput compiled AST evaluator for SQL Row-Level Security (RLS) predicates on in-memory streaming CDC events.
/// Pre-compiles Antlr SQL AST into high-speed Zero-Allocation evaluation plans supporting comparisons, IN, LIKE, IS NULL, BETWEEN, and boolean logic.
/// </summary>
public static partial class StreamingRowFilterAstEvaluator
{
    private static readonly FastSqlEngine Engine = new() { MaxQueryLength = 4096 };
    private static readonly ConcurrentDictionary<string, Func<IReadOnlyDictionary<string, object?>, bool>?> PlanCache = new(StringComparer.Ordinal);
    private const int MaxCacheSize = 1000;

    [GeneratedRegex(@"@([a-zA-Z0-9_]+)")]
    private static partial Regex ParameterTokenRegex();

    /// <summary>
    /// Evaluates whether the given CDC payload matches the SQL row filter.
    /// Fast-path uses pre-compiled zero-allocation delegate plan from PlanCache.
    /// </summary>
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

        var plan = GetOrCompilePlan(filterSql);
        if (plan == null)
        {
            // Unparseable or unsupported subquery filter -> fail-closed for zero-trust security
            return false;
        }

        try
        {
            return plan(payload);
        }
        catch
        {
            // Any evaluation error -> fail-closed
            return false;
        }
    }

    private static Func<IReadOnlyDictionary<string, object?>, bool>? GetOrCompilePlan(string filterSql)
    {
        if (PlanCache.TryGetValue(filterSql, out var cached))
        {
            return cached;
        }

        if (PlanCache.Count >= MaxCacheSize)
        {
            PlanCache.Clear();
        }

        Func<IReadOnlyDictionary<string, object?>, bool>? plan = null;
        try
        {
            string normalized = filterSql.Contains('@')
                ? ParameterTokenRegex().Replace(filterSql, "__param_$1")
                : filterSql;

            var (tree, _) = Engine.ParseExpression(normalized.AsMemory());
            if (tree?.expression() != null)
            {
                plan = CompileBoolean(tree.expression());
            }
        }
        catch
        {
            plan = null;
        }

        PlanCache[filterSql] = plan;
        return plan;
    }

    private static Func<IReadOnlyDictionary<string, object?>, bool>? CompileBoolean(RuleContext? ctx)
    {
        if (ctx == null) return null;

        return ctx switch
        {
            SqlBaseParser.ExpressionContext exprCtx =>
                CompileBoolean(exprCtx.booleanExpression()),

            SqlBaseParser.AndContext andCtx =>
                CompileAnd(andCtx),

            SqlBaseParser.OrContext orCtx =>
                CompileOr(orCtx),

            SqlBaseParser.LogicalNotContext notCtx =>
                CompileNot(notCtx),

            SqlBaseParser.ParenthesizedExpressionContext parenCtx =>
                CompileBoolean(parenCtx.expression()),

            SqlBaseParser.PredicatedContext predCtx =>
                CompilePredicated(predCtx),

            _ => null
        };
    }

    private static Func<IReadOnlyDictionary<string, object?>, bool>? CompileAnd(SqlBaseParser.AndContext andCtx)
    {
        var left = CompileBoolean(andCtx.booleanExpression(0));
        var right = CompileBoolean(andCtx.booleanExpression(1));
        if (left == null || right == null) return null;
        return payload => left(payload) && right(payload);
    }

    private static Func<IReadOnlyDictionary<string, object?>, bool>? CompileOr(SqlBaseParser.OrContext orCtx)
    {
        var left = CompileBoolean(orCtx.booleanExpression(0));
        var right = CompileBoolean(orCtx.booleanExpression(1));
        if (left == null || right == null) return null;
        return payload => left(payload) || right(payload);
    }

    private static Func<IReadOnlyDictionary<string, object?>, bool>? CompileNot(SqlBaseParser.LogicalNotContext notCtx)
    {
        var inner = CompileBoolean(notCtx.booleanExpression());
        if (inner == null) return null;
        return payload => !inner(payload);
    }

    private static Func<IReadOnlyDictionary<string, object?>, bool>? CompilePredicated(SqlBaseParser.PredicatedContext ctx)
    {
        var predicate = ctx.predicate();

        if (predicate == null)
        {
            if (ctx.valueExpression() is SqlBaseParser.ValueExpressionDefaultContext def &&
                def.primaryExpression() is SqlBaseParser.ParenthesizedExpressionContext paren)
            {
                return CompileBoolean(paren.expression());
            }

            var (colName, staticVal) = InspectValueExpression(ctx.valueExpression());
            if (staticVal is bool b) return _ => b;
            if (colName != null)
            {
                return payload => TryGetPayloadValue(payload, colName, out var v) &&
                                  (v is true || (v is string s && bool.TryParse(s, out var pb) && pb));
            }
            return null;
        }

        var (leftCol, leftConst) = InspectValueExpression(ctx.valueExpression());

        return predicate switch
        {
            SqlBaseParser.ComparisonContext comp =>
                CompileComparison(leftCol, leftConst, comp),

            SqlBaseParser.InListContext inList =>
                CompileInList(leftCol, inList),

            SqlBaseParser.NullPredicateContext nullPred =>
                CompileNullPredicate(leftCol, nullPred),

            SqlBaseParser.LikeContext likeCtx =>
                CompileLike(leftCol, likeCtx),

            SqlBaseParser.BetweenContext betweenCtx =>
                CompileBetween(leftCol, betweenCtx),

            _ => null
        };
    }

    private static Func<IReadOnlyDictionary<string, object?>, bool>? CompileComparison(
        string? leftCol, object? leftConst, SqlBaseParser.ComparisonContext comp)
    {
        var op = comp.comparisonOperator()?.GetText()?.Trim();
        if (string.IsNullOrEmpty(op)) return null;

        var (rightCol, rightConst) = InspectValueExpression(comp.right);

        // Case 1: left is column, right is constant
        if (leftCol != null && rightCol == null)
        {
            return payload =>
            {
                TryGetPayloadValue(payload, leftCol, out var val);
                return EvaluateComparison(val, op, rightConst);
            };
        }

        // Case 2: left is constant, right is column
        if (leftConst != null && rightCol != null)
        {
            return payload =>
            {
                TryGetPayloadValue(payload, rightCol, out var val);
                return EvaluateComparison(leftConst, op, val);
            };
        }

        // Case 3: both columns
        if (leftCol != null && rightCol != null)
        {
            return payload =>
            {
                TryGetPayloadValue(payload, leftCol, out var lVal);
                TryGetPayloadValue(payload, rightCol, out var rVal);
                return EvaluateComparison(lVal, op, rVal);
            };
        }

        // Case 4: both constants
        return _ => EvaluateComparison(leftConst, op, rightConst);
    }

    private static Func<IReadOnlyDictionary<string, object?>, bool>? CompileInList(
        string? leftCol, SqlBaseParser.InListContext inList)
    {
        if (leftCol == null) return null;

        bool isNot = inList.NOT() != null;
        var expressions = inList.expression();
        if (expressions == null || expressions.Length == 0) return _ => isNot;

        var strSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var numSet = new HashSet<decimal>();

        foreach (var expr in expressions)
        {
            var (_, constant) = InspectValueExpression(expr);
            if (constant != null)
            {
                strSet.Add(constant.ToString() ?? string.Empty);
                if (TryConvertToDecimal(constant, out decimal d))
                {
                    numSet.Add(d);
                }
            }
        }

        return payload =>
        {
            if (!TryGetPayloadValue(payload, leftCol, out var val) || val == null)
            {
                return false;
            }

            bool match = (TryConvertToDecimal(val, out decimal d) && numSet.Contains(d))
                         || strSet.Contains(val.ToString() ?? string.Empty);

            return isNot ? !match : match;
        };
    }

    private static Func<IReadOnlyDictionary<string, object?>, bool>? CompileLike(
        string? leftCol, SqlBaseParser.LikeContext likeCtx)
    {
        if (leftCol == null) return null;

        bool isNot = likeCtx.NOT() != null;
        var (_, patternObj) = InspectValueExpression(likeCtx.pattern);
        string patternVal = patternObj?.ToString() ?? string.Empty;

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

        var compiledRegex = new Regex(sb.ToString(), RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200));

        return payload =>
        {
            if (!TryGetPayloadValue(payload, leftCol, out var val) || val == null)
            {
                return false;
            }

            try
            {
                bool isMatch = compiledRegex.IsMatch(val.ToString() ?? string.Empty);
                return isNot ? !isMatch : isMatch;
            }
            catch (RegexMatchTimeoutException)
            {
                return false;
            }
        };
    }

    private static Func<IReadOnlyDictionary<string, object?>, bool>? CompileNullPredicate(
        string? leftCol, SqlBaseParser.NullPredicateContext nullPred)
    {
        if (leftCol == null) return null;
        bool isNot = nullPred.NOT() != null;

        return payload =>
        {
            bool hasVal = TryGetPayloadValue(payload, leftCol, out var val) && val != null && val is not DBNull;
            return isNot ? hasVal : !hasVal;
        };
    }

    private static Func<IReadOnlyDictionary<string, object?>, bool>? CompileBetween(
        string? leftCol, SqlBaseParser.BetweenContext betweenCtx)
    {
        if (leftCol == null) return null;
        bool isNot = betweenCtx.NOT() != null;

        var (_, lowConst) = InspectValueExpression(betweenCtx.lower);
        var (_, upConst) = InspectValueExpression(betweenCtx.upper);

        if (!TryConvertToDecimal(lowConst, out decimal lowNum) ||
            !TryConvertToDecimal(upConst, out decimal upNum))
        {
            return null;
        }

        return payload =>
        {
            if (!TryGetPayloadValue(payload, leftCol, out var val) || val == null)
            {
                return false;
            }

            if (TryConvertToDecimal(val, out decimal actual))
            {
                bool inRange = actual >= lowNum && actual <= upNum;
                return isNot ? !inRange : inRange;
            }

            return false;
        };
    }

    private static (string? ColName, object? ConstantVal) InspectValueExpression(RuleContext? ctx)
    {
        if (ctx == null) return (null, null);

        string text = ctx.GetText();

        if (text.StartsWith('\'') && text.EndsWith('\'') && text.Length >= 2)
        {
            return (null, text[1..^1].Replace("''", "'"));
        }

        if (decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal num))
        {
            return (null, num);
        }

        if (bool.TryParse(text, out bool b))
        {
            return (null, b);
        }

        if (string.Equals(text, "NULL", StringComparison.OrdinalIgnoreCase))
        {
            return (null, null);
        }

        string colName = SqlIdentifierHelper.NormalizeIdentifier(text);
        return (colName, null);
    }

    private static bool TryGetPayloadValue(IReadOnlyDictionary<string, object?> payload, string colName, out object? val)
    {
        if (payload.TryGetValue(colName, out val))
        {
            return true;
        }

        int dotIdx = colName.LastIndexOf('.');
        if (dotIdx >= 0 && dotIdx + 1 < colName.Length)
        {
            string simple = colName[(dotIdx + 1)..];
            if (payload.TryGetValue(simple, out val))
            {
                return true;
            }
        }

        val = null;
        return false;
    }

    private static bool EvaluateComparison(object? left, string opText, object? right)
    {
        // Null comparison semantics
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

        // String comparison
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
