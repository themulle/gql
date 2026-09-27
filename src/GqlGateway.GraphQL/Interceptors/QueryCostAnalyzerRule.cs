namespace GqlGateway.GraphQL.Interceptors;

using System;
using System.Linq;
using HotChocolate;
using HotChocolate.Language;
using HotChocolate.Types;
using HotChocolate.Validation;

public sealed class QueryCostAnalyzerRule : IDocumentValidatorRule
{
    private readonly int _maxAllowedCost;
    private readonly int _defaultListMultiplier;
    private readonly int _maxResponseRows;
    private readonly Action? _onQueryTooComplex;

    public QueryCostAnalyzerRule(
        int maxAllowedCost = 250,
        int defaultListMultiplier = 10,
        int maxResponseRows = 1000,
        Action? onQueryTooComplex = null)
    {
        _maxAllowedCost = maxAllowedCost;
        _defaultListMultiplier = defaultListMultiplier;
        _maxResponseRows = maxResponseRows;
        _onQueryTooComplex = onQueryTooComplex;
    }

    public bool IsCacheable => true;
    public ushort Priority => 10;

    public static int CalculateCost(DocumentNode document, ISchema schema, int defaultListMultiplier = 10, int maxResponseRows = 1000)
    {
        var rule = new QueryCostAnalyzerRule(int.MaxValue, defaultListMultiplier, maxResponseRows);
        var fragments = document.Definitions
            .OfType<FragmentDefinitionNode>()
            .ToDictionary(f => f.Name.Value, f => f, StringComparer.Ordinal);

        var visitedFragments = new HashSet<string>(StringComparer.Ordinal);
        int totalCost = 0;

        foreach (var def in document.Definitions)
        {
            if (def is OperationDefinitionNode operation)
            {
                var rootType = operation.Operation switch
                {
                    OperationType.Mutation => schema.MutationType,
                    OperationType.Subscription => schema.SubscriptionType,
                    _ => schema.QueryType
                };

                totalCost += rule.CalculateSelectionSetCost(operation.SelectionSet, rootType, fragments, visitedFragments, schema);
            }
        }

        return Math.Max(1, totalCost);
    }

    public void Validate(IDocumentValidatorContext context, DocumentNode document)
    {
        var fragments = document.Definitions
            .OfType<FragmentDefinitionNode>()
            .ToDictionary(f => f.Name.Value, f => f, StringComparer.Ordinal);

        var visitedFragments = new HashSet<string>(StringComparer.Ordinal);

        foreach (var def in document.Definitions)
        {
            if (def is OperationDefinitionNode operation)
            {
                var rootType = operation.Operation switch
                {
                    OperationType.Mutation => context.Schema.MutationType,
                    OperationType.Subscription => context.Schema.SubscriptionType,
                    _ => context.Schema.QueryType
                };

                int totalCost = CalculateSelectionSetCost(operation.SelectionSet, rootType, fragments, visitedFragments, context.Schema);
                if (totalCost > _maxAllowedCost)
                {
                    _onQueryTooComplex?.Invoke();
                    context.ReportError(
                        ErrorBuilder.New()
                            .SetMessage($"Die Abfrage überschreitet das Komplexitätsbudget von {_maxAllowedCost} (berechnete Kosten: {totalCost}).")
                            .SetCode("QUERY_TOO_COMPLEX")
                            .SetExtension("calculatedCost", totalCost)
                            .SetExtension("maxAllowedCost", _maxAllowedCost)
                            .Build());
                }
            }
        }
    }

    private int CalculateSelectionSetCost(
        SelectionSetNode? selectionSet,
        ObjectType? currentType,
        IReadOnlyDictionary<string, FragmentDefinitionNode> fragments,
        HashSet<string> visitedFragments,
        ISchema schema)
    {
        if (selectionSet == null || selectionSet.Selections.Count == 0)
        {
            return 0;
        }

        int cost = 0;
        foreach (var selection in selectionSet.Selections)
        {
            if (selection is FieldNode field)
            {
                if (field.Name.Value.StartsWith("__", StringComparison.Ordinal))
                {
                    cost += 1;
                    continue;
                }

                IOutputField? fieldDef = null;
                currentType?.Fields.TryGetField(field.Name.Value, out fieldDef);

                bool isList = false;
                ObjectType? nextType = null;

                if (fieldDef != null)
                {
                    isList = fieldDef.Type.IsListType();
                    var named = fieldDef.Type.NamedType();
                    if (named is ObjectType ot)
                    {
                        nextType = ot;
                    }
                }
                else
                {
                    isList = field.Arguments.Any(a =>
                        string.Equals(a.Name.Value, "first", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(a.Name.Value, "last", StringComparison.OrdinalIgnoreCase));
                }

                bool acceptsPagination = fieldDef?.Arguments.Any(a =>
                    string.Equals(a.Name, "first", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(a.Name, "last", StringComparison.OrdinalIgnoreCase)) ?? false;

                if (isList && acceptsPagination)
                {
                    int requestedLimit = -1;
                    foreach (var arg in field.Arguments)
                    {
                        if (string.Equals(arg.Name.Value, "first", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(arg.Name.Value, "last", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(arg.Name.Value, "limit", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(arg.Name.Value, "take", StringComparison.OrdinalIgnoreCase))
                        {
                            if (arg.Value is IntValueNode intVal && int.TryParse(intVal.Value, out var parsed))
                            {
                                requestedLimit = parsed;
                                break;
                            }
                        }
                    }

                    int effectiveRows = requestedLimit > 0
                        ? Math.Min(requestedLimit, _maxResponseRows)
                        : _maxResponseRows;

                    cost += _defaultListMultiplier * effectiveRows;

                    if (field.SelectionSet != null)
                    {
                        cost += CalculateMaskingCost(field.SelectionSet, fragments, visitedFragments);
                        cost += CalculateSelectionSetCost(field.SelectionSet, nextType, fragments, visitedFragments, schema);
                    }
                }
                else
                {
                    // Non-paginated entity, scalar, or unpaginated relation list
                    cost += isList ? 5 : 1;
                    if (field.SelectionSet != null)
                    {
                        cost += CalculateSelectionSetCost(field.SelectionSet, nextType, fragments, visitedFragments, schema);
                    }
                }
            }
            else if (selection is InlineFragmentNode inlineFrag)
            {
                ObjectType? inlineType = currentType;
                if (inlineFrag.TypeCondition != null && schema.TryGetType<ObjectType>(inlineFrag.TypeCondition.Name.Value, out var foundType))
                {
                    inlineType = foundType;
                }

                cost += CalculateSelectionSetCost(inlineFrag.SelectionSet, inlineType, fragments, visitedFragments, schema);
            }
            else if (selection is FragmentSpreadNode fragmentSpread)
            {
                if (fragments.TryGetValue(fragmentSpread.Name.Value, out var fragDef) && visitedFragments.Add(fragDef.Name.Value))
                {
                    ObjectType? fragType = currentType;
                    if (fragDef.TypeCondition != null && schema.TryGetType<ObjectType>(fragDef.TypeCondition.Name.Value, out var foundType))
                    {
                        fragType = foundType;
                    }

                    cost += CalculateSelectionSetCost(fragDef.SelectionSet, fragType, fragments, visitedFragments, schema);
                    visitedFragments.Remove(fragDef.Name.Value);
                }
            }
        }

        return cost;
    }

    private static int CalculateMaskingCost(
        SelectionSetNode selectionSet,
        IReadOnlyDictionary<string, FragmentDefinitionNode> fragments,
        HashSet<string> visitedFragments)
    {
        int maskingCost = 0;
        foreach (var childSel in selectionSet.Selections)
        {
            if (childSel is FieldNode childField)
            {
                if (IsMaskedCandidate(childField.Name.Value))
                {
                    maskingCost += 3;
                }
            }
            else if (childSel is InlineFragmentNode inlineFrag)
            {
                maskingCost += CalculateMaskingCost(inlineFrag.SelectionSet, fragments, visitedFragments);
            }
            else if (childSel is FragmentSpreadNode spread &&
                     fragments.TryGetValue(spread.Name.Value, out var fragDef) &&
                     visitedFragments.Add(fragDef.Name.Value))
            {
                maskingCost += CalculateMaskingCost(fragDef.SelectionSet, fragments, visitedFragments);
                visitedFragments.Remove(fragDef.Name.Value);
            }
        }
        return maskingCost;
    }

    private static bool IsMaskedCandidate(string fieldName)
    {
        return fieldName.Contains("email", StringComparison.OrdinalIgnoreCase) ||
               fieldName.Contains("iban", StringComparison.OrdinalIgnoreCase) ||
               fieldName.Contains("salary", StringComparison.OrdinalIgnoreCase) ||
               fieldName.Contains("ssn", StringComparison.OrdinalIgnoreCase) ||
               fieldName.Contains("creditcard", StringComparison.OrdinalIgnoreCase) ||
               fieldName.Contains("mask", StringComparison.OrdinalIgnoreCase);
    }
}
