using GqlGateway.Domain.Common;
using GqlGateway.Domain.Model;

namespace GqlGateway.Application.Interfaces;

public interface IRlsFilterGenerator
{
    string BuildCorrelatedSubquery(ConsentRowFilter filter, DatabaseDialect dialect = DatabaseDialect.SqlServer);
    string BuildCrossSourceSetFilter(ConsentRowFilter filter, int maxBatchSize = 500, DatabaseDialect dialect = DatabaseDialect.SqlServer);
}
