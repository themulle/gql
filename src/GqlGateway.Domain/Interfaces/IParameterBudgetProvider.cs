using GqlGateway.Domain.Common;

namespace GqlGateway.Domain.Interfaces;

public interface IParameterBudgetProvider
{
    int GetMaxParameters(DatabaseDialect dialect);

    int CalculateEffectiveChunkSize(
        int defaultChunkSize,
        int keyColumnCount,
        int contextParameterCount = 0,
        DatabaseDialect dialect = DatabaseDialect.Sqlite,
        int safetyBuffer = 50);
}
