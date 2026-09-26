using System.Security.Claims;
using GqlGateway.Domain.Common;
using Microsoft.AspNetCore.Authentication;

namespace GqlGateway.Api.Security;

public sealed class EnterpriseClaimsTransformation : IClaimsTransformation
{
    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity is not ClaimsIdentity identity || !identity.IsAuthenticated)
        {
            return Task.FromResult(principal);
        }

        if (identity.HasClaim(c => c.Type == "__EnterpriseTransformed"))
        {
            return Task.FromResult(principal);
        }

        // 1. Ensure ClaimTypes.PrimarySid is populated
        var existingPrimarySid = identity.FindFirst(ClaimTypes.PrimarySid);
        if (existingPrimarySid == null)
        {
            var userSid = principal.GetUserSid();
            if (userSid != null)
            {
                identity.AddClaim(new Claim(ClaimTypes.PrimarySid, userSid.Value.Value));
            }
        }

        // 2. Ensure ClaimTypes.GroupSid is populated from any provider group claims
        var existingGroups = identity.FindAll(ClaimTypes.GroupSid)
            .Select(c => c.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var allGroupSids = principal.GetGroupSids();
        foreach (var groupSid in allGroupSids)
        {
            if (!existingGroups.Contains(groupSid.Value))
            {
                identity.AddClaim(new Claim(ClaimTypes.GroupSid, groupSid.Value));
            }
        }

        // 3. Ensure ClaimTypes.Role is populated from any provider role claims
        var existingRoles = identity.FindAll(ClaimTypes.Role)
            .Select(c => c.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var allRoles = principal.GetUserRoles();
        foreach (var role in allRoles)
        {
            if (!existingRoles.Contains(role))
            {
                identity.AddClaim(new Claim(ClaimTypes.Role, role));
            }
        }

        identity.AddClaim(new Claim("__EnterpriseTransformed", "1"));
        return Task.FromResult(principal);
    }
}
