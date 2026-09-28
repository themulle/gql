namespace GqlGateway.Api.Middleware;

using System.Diagnostics;
using System.Text.Json;
using GqlGateway.Application.Extensibility;
using GqlGateway.Domain.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public sealed class GatewayExtensibilityMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IExtensibilityPipeline _pipeline;
    private readonly GatewayOptions _options;
    private readonly ILogger<GatewayExtensibilityMiddleware> _logger;

    public GatewayExtensibilityMiddleware(
        RequestDelegate _next,
        IExtensibilityPipeline pipeline,
        IOptions<GatewayOptions> options,
        ILogger<GatewayExtensibilityMiddleware> logger)
    {
        this._next = _next;
        _pipeline = pipeline;
        _options = options.Value;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!_options.Extensibility.Enabled)
        {
            await _next(context);
            return;
        }

        var endpoint = _options.GraphQL.EndpointPath.StartsWith('/')
            ? _options.GraphQL.EndpointPath
            : "/" + _options.GraphQL.EndpointPath;

        // Apply to GraphQL endpoint and API endpoints
        if (!context.Request.Path.StartsWithSegments(endpoint) && !context.Request.Path.StartsWithSegments("/api"))
        {
            await _next(context);
            return;
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in context.Request.Headers)
        {
            headers[k] = v.ToString();
        }

        var ingressContext = new IngressContext
        {
            User = context.User,
            Path = context.Request.Path.Value ?? "/",
            Method = context.Request.Method,
            Headers = headers,
            Query = context.Request.QueryString.Value
        };

        // 1. Ingress phase
        var ingressResult = await _pipeline.ProcessIngressAsync(ingressContext, context.RequestAborted);
        if (ingressResult.Decision != IngressDecision.Continue)
        {
            context.Response.StatusCode = ingressResult.StatusCode;
            foreach (var (k, v) in ingressResult.ResponseHeaders)
            {
                context.Response.Headers[k] = v;
            }

            if (ingressResult.ShortCircuitPayload != null)
            {
                await context.Response.WriteAsJsonAsync(ingressResult.ShortCircuitPayload, context.RequestAborted);
            }
            else
            {
                await context.Response.WriteAsJsonAsync(new
                {
                    decision = ingressResult.Decision.ToString(),
                    reason = ingressResult.Reason,
                    statusCode = ingressResult.StatusCode
                }, context.RequestAborted);
            }
            return;
        }

        // Store ingress items in HttpContext.Items for downstream handlers
        foreach (var (k, v) in ingressContext.Items)
        {
            context.Items[k] = v;
        }

        // 2. Execution phase with response interception for Egress
        var originalBodyStream = context.Response.Body;
        await using var memoryStream = new MemoryStream();
        context.Response.Body = memoryStream;

        var sw = Stopwatch.StartNew();
        try
        {
            await _next(context);
        }
        finally
        {
            sw.Stop();
        }

        memoryStream.Position = 0;
        var responseBytes = memoryStream.ToArray();

        // 3. Egress phase
        var egressContext = new EgressContext
        {
            IngressContext = ingressContext,
            StatusCode = context.Response.StatusCode,
            ResponseBytes = responseBytes,
            Elapsed = sw.Elapsed
        };

        var egressResult = await _pipeline.ProcessEgressAsync(egressContext, context.RequestAborted);

        foreach (var (k, v) in egressResult.AdditionalHeaders)
        {
            context.Response.Headers[k] = v;
        }

        byte[] finalBytes = responseBytes;
        if (egressResult.Handled && egressResult.MutatedResponseBytes.HasValue)
        {
            finalBytes = egressResult.MutatedResponseBytes.Value.ToArray();
        }
        else if (egressResult.Handled && egressResult.MutatedResponseText != null)
        {
            finalBytes = System.Text.Encoding.UTF8.GetBytes(egressResult.MutatedResponseText);
        }

        context.Response.Body = originalBodyStream;
        if (finalBytes.Length > 0 && !context.Response.HasStarted)
        {
            context.Response.ContentLength = finalBytes.Length;
            await context.Response.Body.WriteAsync(finalBytes, context.RequestAborted);
        }
    }
}
