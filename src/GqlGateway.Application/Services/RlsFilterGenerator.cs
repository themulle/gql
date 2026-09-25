using GqlGateway.Application.Interfaces;
using GqlGateway.Domain.Common;
using GqlGateway.Domain.Model;

namespace GqlGateway.Application.Services;

public sealed class RlsFilterGenerator : IRlsFilterGenerator
{
    public static readonly RlsFilterGenerator Instance = new();

    public string BuildCorrelatedSubquery(ConsentRowFilter filter, DatabaseDialect dialect = DatabaseDialect.SqlServer)
    {
        return AdvancedRlsFilterGenerator.BuildCorrelatedSubquery(filter, dialect);
    }

    public string BuildCrossSourceSetFilter(ConsentRowFilter filter, int maxBatchSize = 500, DatabaseDialect dialect = DatabaseDialect.SqlServer)
    {
        return AdvancedRlsFilterGenerator.BuildCrossSourceSetFilter(filter, maxBatchSize, dialect);
    }
}
