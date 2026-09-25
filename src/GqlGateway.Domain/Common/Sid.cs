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
            ?? principal.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

        return string.IsNullOrWhiteSpace(sidStr) ? (Sid?)null : new Sid(sidStr);
    }
}
