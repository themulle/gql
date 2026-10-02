namespace GqlGateway.Application.Interfaces;

using System.Threading;
using System.Threading.Tasks;
using GqlGateway.Domain.Common;
using GqlGateway.Domain.Model;

public interface IOpenJevClient
{
    Task<JustificationTriageResult> ClassifyJustificationAsync(
        TenantId tenant,
        Sid userSid,
        TableIdentifier table,
        string justificationText,
        CancellationToken ct = default);
}
