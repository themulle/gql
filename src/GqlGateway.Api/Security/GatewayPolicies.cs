namespace GqlGateway.Api.Security;

using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;

/// <summary>
/// SEC M-03: Named authorization policies. Endpoints attach them declaratively via
/// <c>.RequireAuthorization(GatewayPolicies.GovernanceAdmin)</c> instead of hand-written role checks.
/// Role names are taken verbatim from the existing handler checks.
/// </summary>
public static class GatewayPolicies
{
    public const string GovernanceAdmin = "GovernanceAdmin";
    public const string Approver = "Approver";
    public const string ClusterAdmin = "ClusterAdmin";
    public const string PrivacyAdmin = "PrivacyAdmin";
    public const string SchemaAdmin = "SchemaAdmin";
    public const string SchemaPublisher = "SchemaPublisher";

    public static readonly string[] GovernanceAdminRoles = ["GovernanceAdmin", "ClusterAdmin"];
    public static readonly string[] ApproverRoles = ["DataSteward", "DataOwner", "GovernanceAdmin"];
    public static readonly string[] ClusterAdminRoles = ["ClusterAdmin"];
    public static readonly string[] PrivacyAdminRoles = ["GovernanceAdmin", "PrivacyAdmin", "DataProtectionOfficer", "ClusterAdmin"];
    public static readonly string[] SchemaAdminRoles = ["GovernanceAdmin", "SchemaAdmin", "GatewayAdmin", "PlatformAdmin", "ClusterAdmin"];
    public static readonly string[] SchemaPublisherRoles = ["GovernanceAdmin", "SchemaAdmin", "GatewayAdmin", "PlatformAdmin", "ClusterAdmin", "Developer", "DataOwner"];

    private static readonly string[] RoleClaimTypes = [ClaimTypes.Role, "role", "roles"];

    /// <summary>
    /// True when the principal carries any of the given roles, either via <see cref="ClaimsPrincipal.IsInRole"/>
    /// (identity role claim type) or via a raw "role"/"roles" claim as issued by Entra ID / ADFS.
    /// </summary>
    public static bool HasAnyRole(ClaimsPrincipal? principal, IReadOnlyList<string> roles)
    {
        ArgumentNullException.ThrowIfNull(roles);

        if (principal?.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        foreach (var role in roles)
        {
            if (principal.IsInRole(role))
            {
                return true;
            }
        }

        return principal.Claims.Any(c =>
            RoleClaimTypes.Contains(c.Type, StringComparer.Ordinal) &&
            roles.Contains(c.Value, StringComparer.Ordinal));
    }

    /// <summary>
    /// Registers all named gateway policies and the authenticated-user fallback policy.
    /// </summary>
    public static void Configure(AuthorizationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // SEC M-03: Endpoints without explicit metadata require an authenticated user.
        // Public endpoints must opt out explicitly via .AllowAnonymous().
        options.FallbackPolicy = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .Build();

        AddRolePolicy(options, GovernanceAdmin, GovernanceAdminRoles);
        AddRolePolicy(options, Approver, ApproverRoles);
        AddRolePolicy(options, ClusterAdmin, ClusterAdminRoles);
        AddRolePolicy(options, PrivacyAdmin, PrivacyAdminRoles);
        AddRolePolicy(options, SchemaAdmin, SchemaAdminRoles);
        AddRolePolicy(options, SchemaPublisher, SchemaPublisherRoles);
    }

    private static void AddRolePolicy(AuthorizationOptions options, string name, string[] roles)
    {
        options.AddPolicy(name, policy => policy
            .RequireAuthenticatedUser()
            .RequireAssertion(ctx => HasAnyRole(ctx.User, roles)));
    }
}
