namespace GqlGateway.Domain.Interfaces;

public interface IConsentResolutionService
{
    /// <summary>
    /// Löst Zugriffsrechte deterministisch nach F-CONS-07 Wahrheitstabelle auf.
    /// </summary>
    TableAccessDecision ResolveAccess(
        Sid userSid,
        IReadOnlySet<Sid> subjectGroupSids,
        IReadOnlySet<string> userRoles,
        TableIdentifier table,
        IReadOnlyList<Consent> activeConsents,
        GqlGateway.Domain.Common.DatabaseDialect dialect = GqlGateway.Domain.Common.DatabaseDialect.SqlServer
    );
}
