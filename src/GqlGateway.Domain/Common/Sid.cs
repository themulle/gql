using System;

namespace GqlGateway.Domain.Common;

public readonly record struct Sid(string Value) : IEquatable<Sid>
{
    public override string ToString() => Value;

    public static implicit operator string(Sid sid) => sid.Value;
    public static implicit operator Sid(string value) => new(value);

    public bool Equals(Sid other) =>
        string.Equals(Value, other.Value, StringComparison.OrdinalIgnoreCase);

    public override int GetHashCode() =>
        StringComparer.OrdinalIgnoreCase.GetHashCode(Value ?? string.Empty);
}

public static class ClaimsPrincipalExtensions
{
    public static Sid? GetUserSid(this System.Security.Claims.ClaimsPrincipal? principal)
    {
        if (principal == null) return null;

        var sidStr = principal.FindFirst(System.Security.Claims.ClaimTypes.PrimarySid)?.Value
            ?? principal.FindFirst("objectSid")?.Value
            ?? principal.FindFirst("onprem_sid")?.Value
            ?? principal.FindFirst("primarysid")?.Value
            ?? principal.FindFirst("http://schemas.microsoft.com/ws/2008/06/identity/claims/primarysid")?.Value
            ?? principal.FindFirst("oid")?.Value
            ?? principal.FindFirst("http://schemas.microsoft.com/identity/claims/objectidentifier")?.Value
            ?? principal.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
            ?? principal.FindFirst("sub")?.Value;

        return string.IsNullOrWhiteSpace(sidStr) ? (Sid?)null : new Sid(sidStr);
    }

    public static HashSet<Sid> GetGroupSids(this System.Security.Claims.ClaimsPrincipal? principal)
    {
        if (principal == null) return [];

        return principal.FindAll(System.Security.Claims.ClaimTypes.GroupSid)
            .Concat(principal.FindAll("groups"))
            .Concat(principal.FindAll("groupsid"))
            .Concat(principal.FindAll("http://schemas.microsoft.com/ws/2008/06/identity/claims/groups"))
            .Concat(principal.FindAll("http://schemas.microsoft.com/ws/2008/06/identity/claims/groupsid"))
            .Where(c => !string.IsNullOrWhiteSpace(c.Value))
            .Select(c => new Sid(c.Value))
            .ToHashSet();
    }

    public static HashSet<string> GetUserRoles(this System.Security.Claims.ClaimsPrincipal? principal)
    {
        if (principal == null) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        return principal.FindAll(System.Security.Claims.ClaimTypes.Role)
            .Concat(principal.FindAll("roles"))
            .Concat(principal.FindAll("role"))
            .Concat(principal.FindAll("http://schemas.microsoft.com/ws/2008/06/identity/claims/role"))
            .Where(c => !string.IsNullOrWhiteSpace(c.Value))
            .Select(c => c.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
