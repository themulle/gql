namespace GqlGateway.Domain.Model;

using System;
using System.Collections.Generic;
using System.Net;
using GqlGateway.Domain.Common;

public sealed record SecurityEvaluationContext(
    Sid UserSid,
    IReadOnlyCollection<Sid> GroupSids,
    TenantId Tenant,
    TableIdentifier TargetTable,
    IReadOnlyCollection<string> RequestedColumns,
    IPAddress ClientIp,
    DateTimeOffset Timestamp,
    string? PurposeId
);

public enum LineageNodeType
{
    Table = 0,
    Column = 1,
    Dashboard = 2,
    Pipeline = 3,
    ExternalService = 4
}

public sealed record LineageNode(
    string Id,
    string Name,
    LineageNodeType Type,
    IReadOnlyCollection<string> DownstreamNodeIds,
    string? OwnerTeam = null,
    string? OwnerEmail = null
);

public sealed record AffectedEntity(
    string Id,
    string Name,
    LineageNodeType Type,
    string? OwnerTeam,
    string? OwnerEmail, // Null, sofern Aufrufer nicht autorisiert (Zero-Trust Spaltenautorisierung)
    bool CyclicReferenceDetected = false
);

public sealed record ConsentRevocationImpactReport(
    string Severity, // HIGH, MEDIUM, LOW
    int AffectedDownstreamCount,
    IReadOnlyList<AffectedEntity> AffectedEntities,
    bool ContainsCycles
);

public enum ItsmSystemType
{
    ServiceNow = 1,
    Jira = 2
}

public sealed record ItsmTicketReference(
    ItsmSystemType System,
    string TicketId,
    string TicketUrl
);

public enum JustificationCategory
{
    LegitimateAudit = 1,
    IncidentTriage = 2,
    Unjustified = 3,
    SuspiciousExfiltration = 4,
    Unclassified = 5
}

public sealed record JustificationTriageResult(
    JustificationCategory Category,
    double Confidence,
    string RawModelOutput,
    bool AutoGrantEligible,
    TimeSpan? GrantedDuration
);
