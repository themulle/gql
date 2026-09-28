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

    [GeneratedRegex(@"^(INC|CHG|SEC|REQ|PR)-\d+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TicketPattern();

    public JustificationAndBreakGlassInterceptor(
        IOptions<GatewayOptions> options,
        ILogger<JustificationAndBreakGlassInterceptor> logger)
    {
        _options = options.Value;
        _logger = logger;
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

            context.Items["IsBreakGlass"] = true;
            _logger.LogWarning(
                "[AUDIT: BREAK-GLASS ACTIVATED] Emergency elevated access invoked by user '{User}' with ticket '{Ticket}'.",
                context.User?.Identity?.Name ?? "Anonymous",
                justification);
        }

        return ValueTask.FromResult(IngressResult.Continue());
    }
}
