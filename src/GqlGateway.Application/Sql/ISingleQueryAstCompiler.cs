namespace GqlGateway.Application.Sql;

using System.Collections.Generic;
using GqlGateway.Domain.Common;
using GqlGateway.Domain.Model;

public interface ISingleQueryAstCompiler
{
    bool SupportsDialect(DatabaseDialect dialect);

    string CompileHierarchicalQuery(
        SqlAstNode rootNode,
        DatabaseDialect dialect,
        IReadOnlyDictionary<TableIdentifier, string?>? rlsPredicates = null);
}
