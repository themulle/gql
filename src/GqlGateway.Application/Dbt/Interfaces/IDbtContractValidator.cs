namespace GqlGateway.Application.Dbt.Interfaces;

using System.IO;
using System.Threading;
using System.Threading.Tasks;
using GqlGateway.Domain.Model;

public interface IDbtContractValidator
{
    Task<DbtContractValidationResult> ValidateContractsStreamAsync(Stream manifestStream, CancellationToken ct = default);
    Task<DbtContractValidationResult> ValidateContractsFileAsync(string filePath, CancellationToken ct = default);
}
