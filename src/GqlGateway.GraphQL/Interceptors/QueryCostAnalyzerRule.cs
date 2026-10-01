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

    public static int CalculateCost(DocumentNode document, ISchemaDefinition schema, int defaultListMultiplier = 10, int maxResponseRows = 1000)
    {
        var rule = new QueryCostAnalyzerRule(int.MaxValue, defaultListMultiplier, maxResponseRows);
        return rule.ComputeCost(document, schema);
    }

    public void Validate(DocumentValidatorContext context, DocumentNode document)
    {
        int totalCost = ComputeCost(document, context.Schema);
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

    private static int SafeAdd(int a, int b) => (int)Math.Min((long)int.MaxValue, (long)a + b);

    public int ComputeCost(DocumentNode document, ISchemaDefinition schema)
    {
        var fragments = document.Definitions
            .OfType<FragmentDefinitionNode>()
            .ToDictionary(f => f.Name.Value, f => f, StringComparer.Ordinal);

        var activeFragments = new HashSet<string>(StringComparer.Ordinal);
        var fragmentCostCache = new Dictionary<string, int>(StringComparer.Ordinal);
        var maskingCostCache = new Dictionary<string, int>(StringComparer.Ordinal);
        int spreadCounter = 0;
        const int maxSpreadExpansions = 250;
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

                totalCost = SafeAdd(totalCost, CalculateSelectionSetCost(
                    operation.SelectionSet,
                    rootType,
                    fragments,
                    activeFragments,
                    fragmentCostCache,
                    maskingCostCache,
                    schema,
                    ref spreadCounter,
                    maxSpreadExpansions));
            }
        }

        return Math.Max(1, totalCost);
    }

    private int CalculateSelectionSetCost(
        SelectionSetNode? selectionSet,
        IObjectTypeDefinition? currentType,
        IReadOnlyDictionary<string, FragmentDefinitionNode> fragments,
        HashSet<string> activeFragments,
        Dictionary<string, int> fragmentCostCache,
        Dictionary<string, int> maskingCostCache,
        ISchemaDefinition schema,
        ref int spreadCounter,
        int maxSpreadExpansions)
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
                    cost = SafeAdd(cost, 1);
                    continue;
                }

                IOutputFieldDefinition? fieldDef = null;
                currentType?.Fields.TryGetField(field.Name.Value, out fieldDef);

                bool isList = false;
                IObjectTypeDefinition? nextType = null;

                if (fieldDef != null)
                {
                    isList = IsListType(fieldDef.Type);
                    var named = UnwrapType(fieldDef.Type);
                    if (named is IObjectTypeDefinition ot)
                    {
                        nextType = ot;
                    }
                    else if (named is INameProvider np && schema.Types.TryGetType<IObjectTypeDefinition>(np.Name, out var foundOt))
                    {
                        nextType = foundOt;
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

                    cost = SafeAdd(cost, SafeAdd(0, (int)Math.Min((long)int.MaxValue, (long)_defaultListMultiplier * effectiveRows)));

                    if (field.SelectionSet != null)
                    {
                        cost = SafeAdd(cost, CalculateMaskingCost(field.SelectionSet, fragments, activeFragments, maskingCostCache));
                        cost = SafeAdd(cost, CalculateSelectionSetCost(field.SelectionSet, nextType, fragments, activeFragments, fragmentCostCache, maskingCostCache, schema, ref spreadCounter, maxSpreadExpansions));
                    }
                }
                else
                {
                    // Non-paginated entity, scalar, or unpaginated relation list
                    cost = SafeAdd(cost, isList ? 5 : 1);
                    if (field.SelectionSet != null)
                    {
                        cost = SafeAdd(cost, CalculateSelectionSetCost(field.SelectionSet, nextType, fragments, activeFragments, fragmentCostCache, maskingCostCache, schema, ref spreadCounter, maxSpreadExpansions));
                    }
                }
            }
            else if (selection is InlineFragmentNode inlineFrag)
            {
                IObjectTypeDefinition? inlineType = currentType;
                if (inlineFrag.TypeCondition != null && schema.Types.TryGetType<IObjectTypeDefinition>(inlineFrag.TypeCondition.Name.Value, out var foundType))
                {
                    inlineType = foundType;
                }

                cost = SafeAdd(cost, CalculateSelectionSetCost(inlineFrag.SelectionSet, inlineType, fragments, activeFragments, fragmentCostCache, maskingCostCache, schema, ref spreadCounter, maxSpreadExpansions));
            }
            else if (selection is FragmentSpreadNode fragmentSpread)
            {
                spreadCounter++;
                if (spreadCounter > maxSpreadExpansions)
                {
                    return SafeAdd(_maxAllowedCost, 1000);
                }

                if (fragmentCostCache.TryGetValue(fragmentSpread.Name.Value, out var cachedFragCost))
                {
                    cost = SafeAdd(cost, cachedFragCost);
                }
                else if (fragments.TryGetValue(fragmentSpread.Name.Value, out var fragDef) && activeFragments.Add(fragDef.Name.Value))
                {
                    IObjectTypeDefinition? fragType = currentType;
                    if (fragDef.TypeCondition != null && schema.Types.TryGetType<IObjectTypeDefinition>(fragDef.TypeCondition.Name.Value, out var foundType))
                    {
                        fragType = foundType;
                    }

                    int fragCost = CalculateSelectionSetCost(fragDef.SelectionSet, fragType, fragments, activeFragments, fragmentCostCache, maskingCostCache, schema, ref spreadCounter, maxSpreadExpansions);
                    activeFragments.Remove(fragDef.Name.Value);
                    fragmentCostCache[fragDef.Name.Value] = fragCost;
                    cost = SafeAdd(cost, fragCost);
                }
            }
        }

        return cost;
    }

    private static int CalculateMaskingCost(
        SelectionSetNode selectionSet,
        IReadOnlyDictionary<string, FragmentDefinitionNode> fragments,
        HashSet<string> activeFragments,
        Dictionary<string, int> maskingCostCache)
    {
        int maskingCost = 0;
        foreach (var childSel in selectionSet.Selections)
        {
            if (childSel is FieldNode childField)
            {
                if (IsMaskedCandidate(childField.Name.Value))
                {
                    maskingCost = SafeAdd(maskingCost, 3);
                }
            }
            else if (childSel is InlineFragmentNode inlineFrag)
            {
                maskingCost = SafeAdd(maskingCost, CalculateMaskingCost(inlineFrag.SelectionSet, fragments, activeFragments, maskingCostCache));
            }
            else if (childSel is FragmentSpreadNode spread)
            {
                if (maskingCostCache.TryGetValue(spread.Name.Value, out var cachedMaskCost))
                {
                    maskingCost = SafeAdd(maskingCost, cachedMaskCost);
                }
                else if (fragments.TryGetValue(spread.Name.Value, out var fragDef) && activeFragments.Add(fragDef.Name.Value))
                {
                    int fragMaskCost = CalculateMaskingCost(fragDef.SelectionSet, fragments, activeFragments, maskingCostCache);
                    activeFragments.Remove(fragDef.Name.Value);
                    maskingCostCache[fragDef.Name.Value] = fragMaskCost;
                    maskingCost = SafeAdd(maskingCost, fragMaskCost);
                }
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

    private static bool IsListType(IType type)
    {
        var current = type;
        while (current is IWrapperType wrapper)
        {
            if (current.Kind == TypeKind.List) return true;
            current = wrapper.InnerType;
        }
        return current.Kind == TypeKind.List;
    }

    private static IType UnwrapType(IType type)
    {
        var current = type;
        while (current is IWrapperType wrapper)
        {
            current = wrapper.InnerType;
        }
        return current;
    }
}
