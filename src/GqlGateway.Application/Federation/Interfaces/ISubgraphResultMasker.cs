namespace GqlGateway.Application.Federation.Interfaces;

using System.Collections.Generic;
using System.Security.Claims;

/// <summary>
/// Enforces in-memory data masking rules on aggregated federated subgraph response trees.
/// </summary>
public interface ISubgraphResultMasker
{
    /// <summary>
    /// Recursively traverses a GraphQL response object or list and applies data masking
    /// to sensitive fields (e.g. Email, IBAN, SSN, salary) for principals without clear consent.
    /// </summary>
    object? MaskResultData(object? data, ClaimsPrincipal? principal);
}
