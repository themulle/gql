namespace GqlGateway.Application.Dbt.Interfaces;

using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using GqlGateway.Domain.Model;

public interface IDbtExposurePublisher
{
    Task<string> GenerateExposuresYamlAsync(CancellationToken ct = default);
    Task ExportExposuresFileAsync(string outputFilePath, CancellationToken ct = default);
}
