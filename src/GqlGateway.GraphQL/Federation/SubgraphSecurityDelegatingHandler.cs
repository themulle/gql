namespace GqlGateway.GraphQL.Federation;

using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using GqlGateway.Application.Federation.Interfaces;
using GqlGateway.Application.Services;
using GqlGateway.Domain.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// HTTP DelegatingHandler attached to federated Subgraph HTTP clients.
/// Enforces Zero-Trust security context propagation and SSRF protection on every outbound subgraph query.
/// </summary>
public sealed class SubgraphSecurityDelegatingHandler : DelegatingHandler
{
    private readonly string _subgraphName;
    private readonly ISubgraphContextPropagationService _propagationService;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<SubgraphSecurityDelegatingHandler> _logger;
    private readonly bool _isDev;

    public SubgraphSecurityDelegatingHandler(
        string subgraphName,
        ISubgraphContextPropagationService propagationService,
        IHttpContextAccessor httpContextAccessor,
        ILogger<SubgraphSecurityDelegatingHandler> logger,
        IOptions<GatewayOptions>? options = null)
    {
        _subgraphName = subgraphName ?? throw new ArgumentNullException(nameof(subgraphName));
        _propagationService = propagationService ?? throw new ArgumentNullException(nameof(propagationService));
        _httpContextAccessor = httpContextAccessor ?? throw new ArgumentNullException(nameof(httpContextAccessor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _isDev = options?.Value.HasAnySecurityBypassActive == true;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (request.RequestUri != null)
        {
            await DeclarativeHttpDataSourceExecutor.ValidateDestinationUrlAsync(request.RequestUri, _isDev, cancellationToken).ConfigureAwait(false);
        }

        var httpContext = _httpContextAccessor.HttpContext;
        var principal = httpContext?.User;

        string? tenantId = null;
        if (httpContext?.Items.TryGetValue("TenantId", out var tObj) == true && tObj is string tStr)
        {
            tenantId = tStr;
        }

        // Apply Zero-Trust Security headers (Subject SID, Tenant, Roles) & SSRF check
        _propagationService.ApplySecurityHeaders(request, _subgraphName, principal, tenantId);

        // Forward Authorization Bearer token downstream if present and caller has one
        if (httpContext?.Request.Headers.TryGetValue("Authorization", out var authVals) == true && authVals.Count > 0)
        {
            if (!request.Headers.Contains("Authorization"))
            {
                request.Headers.TryAddWithoutValidation("Authorization", authVals.ToArray());
            }
        }

        _logger.LogDebug("Dispatching federated query to Subgraph '{Subgraph}' at {Uri}",
            _subgraphName, request.RequestUri);

        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
