namespace GqlGateway.Application.Interfaces;

using System;
using System.Threading;
using System.Threading.Tasks;

public interface IItsmWebhookHandler
{
    /// <summary>
    /// Verarbeitet ITSM-Statusänderungen unter strikter Prüfung von Tenant- und Ticket-Bindung.
    /// </summary>
    Task<bool> HandleStatusChangeAsync(
        string rawPayload,
        string hmacSignature,
        DateTimeOffset timestamp,
        CancellationToken ct = default);
}
