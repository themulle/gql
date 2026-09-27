namespace GqlGateway.Application.Dbt.Interfaces;

using System.IO;
using System.Threading;
using System.Threading.Tasks;
using GqlGateway.Domain.Model;

public interface IDbtMetadataIngestionService
{
    Task<DbtSyncResult> IngestManifestStreamAsync(Stream manifestStream, bool dryRun = false, CancellationToken ct = default);
    Task<DbtSyncResult> IngestManifestFileAsync(string filePath, bool dryRun = false, CancellationToken ct = default);
}
