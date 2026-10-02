using GqlGateway.Domain.Common;
using GqlGateway.Domain.Interfaces;
using GqlGateway.Domain.Model;
using HotChocolate.Language;

namespace GqlGateway.GraphQL.Filtering;

public interface ISqlFilterProvider
{
    (string SqlWhereClause, IReadOnlyDictionary<string, object?> Parameters) TranslateFilterAst(
        FieldNode filterAst,
        TableMetadata metadata,
        DatabaseDialect dialect,
        IReadOnlyDictionary<string, ColumnAccessLevel> columnAccess
    );

    (string SqlWhereClause, IReadOnlyDictionary<string, object?> Parameters) TranslateObjectValue(
        IValueNode filterValueNode,
        TableMetadata metadata,
        DatabaseDialect dialect,
        IReadOnlyDictionary<string, ColumnAccessLevel> columnAccess
    );
}
