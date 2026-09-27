namespace GqlGateway.Application.Workflows;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GqlGateway.Application.Interfaces;
using GqlGateway.Domain.Common;
using GqlGateway.Domain.Model;
using Microsoft.Extensions.Logging;

public sealed class ItsmWorkflowDispatcher
{
    private readonly IEnumerable<IItsmWorkflowClient> _clients;
    private readonly ILogger<ItsmWorkflowDispatcher> _logger;

    public ItsmWorkflowDispatcher(
        IEnumerable<IItsmWorkflowClient> clients,
        ILogger<ItsmWorkflowDispatcher> logger)
    {
        _clients = clients;
        _logger = logger;
    }

    public async Task<ItsmTicketResult> DispatchTicketRequestAsync(
        ItsmTicketRequest request,
        ItsmSystemType preferredSystem = ItsmSystemType.ServiceNow,
        CancellationToken ct = default)
    {
        var client = _clients.FirstOrDefault(c => c.SystemType == preferredSystem)
            ?? _clients.FirstOrDefault();

        if (client == null)
        {
            _logger.LogError("No ITSM workflow client registered for system {System}", preferredSystem);
            return new ItsmTicketResult(false, null, "ITSM_UNAVAILABLE", $"No ITSM client configured for {preferredSystem}.");
        }

        return await client.CreateAccessTicketAsync(request, ct).ConfigureAwait(false);
    }
}
