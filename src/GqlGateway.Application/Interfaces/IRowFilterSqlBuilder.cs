using System.Collections.Generic;
using GqlGateway.Domain.Common;
using GqlGateway.Domain.Model;

namespace GqlGateway.Application.Interfaces;

public interface IRowFilterSqlBuilder
{
    string FormatCondition(ConsentRowFilter filter, DatabaseDialect dialect = DatabaseDialect.SqlServer);

    string? BuildCombinedRowFilter(
        IReadOnlyList<Consent> aConsents,
        IReadOnlyList<Consent> dConsents,
        DatabaseDialect dialect = DatabaseDialect.SqlServer);
}
