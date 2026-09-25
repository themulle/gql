using GqlGateway.Application.Interfaces;
using GqlGateway.Domain.Interfaces;
using GqlGateway.Domain.Model;
using HotChocolate.Types;

namespace GqlGateway.GraphQL.DynamicTypes;

public sealed class DynamicTableType : ObjectType
{
    private readonly TableMetadata _metadata;
    private readonly IColumnMaskingProvider _maskingProvider;

    public DynamicTableType(TableMetadata metadata, IColumnMaskingProvider maskingProvider)
    {
        _metadata = metadata;
        _maskingProvider = maskingProvider;
    }

    protected override void Configure(IObjectTypeDescriptor descriptor)
    {
        descriptor.Name($"{_metadata.Identifier.Domain}_{_metadata.Identifier.TableName}");
        descriptor.Description(_metadata.Table.DisplayName);

        foreach (var col in _metadata.Columns)
        {
            var fieldDesc = descriptor.Field(col.ColumnName);
            ConfigureType(fieldDesc, col.DataType);

            fieldDesc.Resolve(ctx =>
            {
                var row = ctx.Parent<IReadOnlyDictionary<string, object?>>();
                if (!row.TryGetValue(col.ColumnName, out var val) || val == null || val is DBNull)
                {
                    return null;
                }

                // Strict Fail-Closed: Check ColumnAccess in ContextData
                if (!ctx.ContextData.TryGetValue("ColumnAccess", out var accessObj) ||
                    accessObj is not IReadOnlyDictionary<string, ColumnAccessLevel> colAccess ||
                    !colAccess.TryGetValue(col.ColumnName, out var level) ||
                    level == ColumnAccessLevel.Deny)
                {
                    return null;
                }

                if (level == ColumnAccessLevel.Mask)
                {
                    var rule = _metadata.ColumnMaskingRules.TryGetValue(col.ColumnName, out var r)
                        ? r
                        : new MaskingRule { RuleType = "REDACT" };
                    var masked = _maskingProvider.MaskValue(col.ColumnName, val, rule);
                    if (masked is string s && s == "REDACTED" && !IsStringType(col.DataType))
                    {
                        return null;
                    }
                    return masked;
                }

                return val;
            });
        }
    }

    private static bool IsStringType(string dataType)
    {
        var lower = dataType.ToLowerInvariant();
        return lower.Contains("char") || lower.Contains("text") || lower.Contains("string") || lower.Contains("clob");
    }

    private static void ConfigureType(IObjectFieldDescriptor field, string dataType)
    {
        var lower = dataType.ToLowerInvariant();
        if (lower.Contains("bigint") || lower.Contains("long"))
        {
            field.Type<LongType>();
        }
        else if (lower.Contains("int"))
        {
            field.Type<IntType>();
        }
        else if (lower.Contains("decimal") || lower.Contains("numeric") || lower.Contains("money"))
        {
            field.Type<DecimalType>();
        }
        else if (lower.Contains("bool"))
        {
            field.Type<BooleanType>();
        }
        else if (lower.Contains("date") || lower.Contains("time"))
        {
            field.Type<DateTimeType>();
        }
        else
        {
            field.Type<StringType>();
        }
    }
}
