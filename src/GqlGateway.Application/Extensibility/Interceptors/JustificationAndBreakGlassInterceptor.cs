namespace GqlGateway.Application.Extensibility.Interceptors;

using System.Text.RegularExpressions;
using GqlGateway.Application.Extensibility;
using GqlGateway.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public sealed partial class JustificationAndBreakGlassInterceptor : IIngressInterceptor
{
    private readonly GatewayOptions _options;
    private readonly ILogger<JustificationAndBreakGlassInterceptor> _logger;
    private readonly GqlGateway.Application.Interfaces.IGovernanceRepository? _governanceRepo;

    [GeneratedRegex(@"^(INC|CHG|SEC|REQ|PR)-\d+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TicketPattern();

    public JustificationAndBreakGlassInterceptor(
        IOptions<GatewayOptions> options,
        ILogger<JustificationAndBreakGlassInterceptor> logger,
        GqlGateway.Application.Interfaces.IGovernanceRepository? governanceRepo = null)
    {
        _options = options.Value;
        _logger = logger;
        _governanceRepo = governanceRepo;
    }

    public int Order => 10;

    public ValueTask<IngressResult> OnIngressAsync(IngressContext context, CancellationToken cancellationToken = default)
    {
        if (!_options.Extensibility.Enabled)
        {
            return ValueTask.FromResult(IngressResult.Continue());
        }

        var extOptions = _options.Extensibility;
        string breakGlassHeader = extOptions.BreakGlassHeaderName ?? "X-Break-Glass";
        string justificationHeader = extOptions.JustificationHeaderName ?? "X-Access-Justification";

        var isBreakGlassRequested = string.Equals(context.GetHeader(breakGlassHeader), "true", StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(context.GetHeader(breakGlassHeader), "1", StringComparison.OrdinalIgnoreCase);

        var justification = context.GetHeader(justificationHeader)?.Trim();

        if (!string.IsNullOrWhiteSpace(justification))
        {
            context.Items["AccessJustification"] = justification;
        }

        if (isBreakGlassRequested)
        {
            if (!extOptions.EnableBreakGlass)
            {
                _logger.LogWarning("Break-glass access attempt rejected: Break-glass is disabled globally.");
                return ValueTask.FromResult(IngressResult.Deny("Break-glass emergency bypass is disabled by gateway policy.", 403));
            }

            if (extOptions.RequireJustificationForBreakGlass)
            {
                if (string.IsNullOrWhiteSpace(justification) || !TicketPattern().IsMatch(justification))
                {
                    _logger.LogWarning(
                        "Break-glass access challenged: Missing or invalid ticket format '{Justification}'.",
                        justification ?? "<none>");

                    var challengeHeaders = new Dictionary<string, string>
                    {
                        { "X-Challenge-Reason", "Valid enterprise ticket required (e.g., INC-12345, CHG-9876, SEC-001)" }
                    };

                    return ValueTask.FromResult(IngressResult.Challenge(
                        "Break-glass emergency access requires a valid justification ticket (e.g., INC-12345, CHG-9876).",
                        412,
                        challengeHeaders));
                }
            }

            if (extOptions.RequireRoleForBreakGlass && context.User?.Identity?.IsAuthenticated == true)
            {
                var user = context.User;
                bool isAuthorized = false;
                foreach (var role in extOptions.BreakGlassAllowedRoles)
                {
                    if (user.IsInRole(role) || user.HasClaim(c => c.Type == System.Security.Claims.ClaimTypes.Role && string.Equals(c.Value, role, StringComparison.OrdinalIgnoreCase)))
                    {
                        isAuthorized = true;
                        break;
                    }
                }

                if (!isAuthorized)
                {
                    _logger.LogWarning(
                        "Break-glass access denied: User '{User}' does not possess any authorized break-glass role ({Roles}).",
                        user.Identity?.Name ?? "Anonymous",
                        string.Join(", ", extOptions.BreakGlassAllowedRoles));
                    return ValueTask.FromResult(IngressResult.Deny("User is not authorized to invoke emergency break-glass bypass.", 403));
                }
            }

            context.Items["IsBreakGlass"] = true;
            _logger.LogWarning(
                "[AUDIT: BREAK-GLASS ACTIVATED] Emergency elevated access invoked by user '{User}' with ticket '{Ticket}'.",
                context.User?.Identity?.Name ?? "Anonymous",
                justification);

            if (_governanceRepo != null)
            {
                var userSid = context.User?.Identity?.Name ?? "Anonymous";
                var tenantId = context.User?.FindFirst("tenant_id")?.Value ?? "default";
                var clientIp = context.GetHeader("X-Forwarded-For") ?? "unknown";
                var auditEntry = new GqlGateway.Domain.Model.AuditLogEntry
                {
                    Id = Guid.NewGuid(),
                    OccurredAt = DateTimeOffset.UtcNow,
                    EventType = "BREAK_GLASS_ACTIVATED",
                    ActorSid = new GqlGateway.Domain.Common.Sid(userSid),
                    TargetTable = "GATEWAY_INGRESS",
                    Decision = "ALLOW",
                    TraceId = Guid.NewGuid().ToString("N"),
                    DetailsJson = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        tenantId,
                        clientIp,
                        action = "BREAK_GLASS",
                        resource = "GATEWAY_INGRESS",
                        justificationTicket = justification,
                        breakGlassActive = true
                    })
                };
                _ = _governanceRepo.RecordAuditEventAsync(auditEntry, cancellationToken);
            }
        }

        return ValueTask.FromResult(IngressResult.Continue());
    }
}
