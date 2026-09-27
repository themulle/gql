namespace GqlGateway.GraphQL.Federation;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using GqlGateway.Application.Federation.Interfaces;
using GqlGateway.Domain.Options;
using HotChocolate.Execution;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RequestDelegate = HotChocolate.Execution.RequestDelegate;

/// <summary>
/// Hot Chocolate request pipeline middleware that executes after query completion to enforce
/// Zero-Trust in-memory column and PII data masking on federated subgraph results.
/// </summary>
public sealed class SubgraphResultMaskingMiddleware
{
    private readonly RequestDelegate _next;

    public SubgraphResultMaskingMiddleware(RequestDelegate next)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
    }

    public async ValueTask InvokeAsync(IRequestContext context)
    {
        await _next(context).ConfigureAwait(false);

        var options = context.Services.GetService<IOptions<GatewayOptions>>();
        if (options?.Value?.Federation?.Enabled != true ||
            !options.Value.Federation.EnableResultMasking ||
            options.Value.IsColumnMaskingDisabled)
        {
            return;
        }

        if (context.Result is IOperationResult opResult && opResult.Data != null)
        {
            var masker = context.Services.GetService<ISubgraphResultMasker>();
            if (masker == null) return;

            HttpContext? httpContext = null;
            if (context.ContextData.TryGetValue("HttpContext", out var hcObj) && hcObj is HttpContext hc)
            {
                httpContext = hc;
            }
            else
            {
                httpContext = context.Services.GetService<IHttpContextAccessor>()?.HttpContext;
            }

            var principal = httpContext?.User;
            var maskedObj = masker.MaskResultData(opResult.Data, principal);

            if (maskedObj is IReadOnlyDictionary<string, object?> maskedDict)
            {
                context.Result = OperationResultBuilder.FromResult(opResult)
                    .SetData(maskedDict)
                    .Build();
            }
        }
    }
}
