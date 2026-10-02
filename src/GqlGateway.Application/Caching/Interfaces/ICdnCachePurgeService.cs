namespace GqlGateway.Application.Caching.Interfaces;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GqlGateway.Domain.Model;

public interface ICdnCachePurgeService
{
    Task<PurgeResult> PurgeTagsAsync(IReadOnlyList<string> tags, CancellationToken ct = default);
}
