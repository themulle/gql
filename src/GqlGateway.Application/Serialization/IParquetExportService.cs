namespace GqlGateway.Application.Serialization;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GqlGateway.Domain.Model;

public interface IParquetExportService
{
    ParquetExportResult ExportToParquet(
        ParquetExportRequest request,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows);

    /// <summary>
    /// Serializes already governed rows (RLS, masking, consent applied) into an Apache Parquet file.
    /// This is a pure output transformation and never reads data on its own.
    /// </summary>
    Task<ParquetExportResult> ExportToParquetAsync(
        ParquetExportRequest request,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        CancellationToken ct = default);
}
