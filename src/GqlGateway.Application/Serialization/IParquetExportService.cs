namespace GqlGateway.Application.Serialization;

using System.Collections.Generic;
using GqlGateway.Domain.Model;

public interface IParquetExportService
{
    ParquetExportResult ExportToParquet(
        ParquetExportRequest request,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows);
}
