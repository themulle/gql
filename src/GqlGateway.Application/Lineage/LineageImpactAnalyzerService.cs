namespace GqlGateway.Application.Lineage;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using GqlGateway.Application.Interfaces;
using GqlGateway.Domain.Common;
using GqlGateway.Domain.Diagnostics;
using GqlGateway.Domain.Model;
using Microsoft.Extensions.Logging;

public sealed class LineageImpactAnalyzerService(
    IConsentRepository consentRepo,
    IDataOwnershipRepository ownershipRepo,
    ILineageGraphStore graphStore,
    ILogger<LineageImpactAnalyzerService> logger) : ILineageImpactAnalyzerService
{
    public async Task<ConsentRevocationImpactReport> CalculateConsentRevocationImpactAsync(
        TenantId tenant,
        Guid consentId,
        CallerSecurityContext callerContext,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        using var activity = GatewayDiagnostics.Source.StartActivity("Lineage.Traverse");

        var consent = await consentRepo.GetConsentByIdAsync(consentId, ct).ConfigureAwait(false);
        if (consent == null)
        {
            throw new KeyNotFoundException($"Consent mit ID '{consentId}' nicht gefunden.");
        }

        var rootTableId = consent.TableIdentifier.ToString();
        GatewayDiagnostics.SetSafeTag(activity, "Lineage.Traverse", "tenant.id", tenant.Value);
        GatewayDiagnostics.SetSafeTag(activity, "Lineage.Traverse", "root.node_id", rootTableId);

        // Zero-Trust Spaltenautorisierung für ownerEmail:
        // Nur GovernanceAdmin, ClusterAdmin oder der registrierte Data Owner der betroffenen Tabelle dürfen E-Mails sehen
        bool canViewEmail = callerContext.IsGovernanceAdmin || callerContext.IsClusterAdmin;
        if (!canViewEmail)
        {
            canViewEmail = await ownershipRepo.IsAuthorizedApproverForTableAsync(consent.TableIdentifier, callerContext.UserSid, ct).ConfigureAwait(false);
        }

        var queue = new Queue<(string CurrentId, LineagePathNode Path)>(capacity: 64);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var affectedMap = new Dictionary<string, AffectedEntity>(StringComparer.OrdinalIgnoreCase);
        bool containsCycles = false;

        queue.Enqueue((rootTableId, new LineagePathNode(rootTableId, null)));
        visited.Add(rootTableId);

        while (queue.Count > 0)
        {
            var (currentId, path) = queue.Dequeue();
            var node = graphStore.GetNode(currentId);
            if (node == null || node.DownstreamNodeIds == null) continue;

            foreach (var downstreamId in node.DownstreamNodeIds)
            {
                if (path.Contains(downstreamId))
                {
                    // Echter Zyklus! downstreamId liegt auf dem Pfad von der Wurzel zu diesem Knoten
                    containsCycles = true;
                    logger.LogWarning("Lineage Zyklus erkannt bei Knoten: {NodeId} -> {DownstreamId}", currentId, downstreamId);

                    var cyclicNode = graphStore.GetNode(downstreamId);
                    if (cyclicNode != null)
                    {
                        affectedMap[downstreamId] = new AffectedEntity(
                            cyclicNode.Id,
                            cyclicNode.Name,
                            cyclicNode.Type,
                            cyclicNode.OwnerTeam,
                            canViewEmail ? cyclicNode.OwnerEmail : null,
                            CyclicReferenceDetected: true);
                    }
                    continue;
                }

                if (!visited.Add(downstreamId))
                {
                    // Konvergierender Pfad in einem DAG (z.B. Diamant-Graph) - bereits traversiert, kein Zyklus
                    continue;
                }

                var downstreamNode = graphStore.GetNode(downstreamId);
                if (downstreamNode != null)
                {
                    affectedMap[downstreamId] = new AffectedEntity(
                        downstreamNode.Id,
                        downstreamNode.Name,
                        downstreamNode.Type,
                        downstreamNode.OwnerTeam,
                        canViewEmail ? downstreamNode.OwnerEmail : null,
                        CyclicReferenceDetected: false);

                    queue.Enqueue((downstreamId, new LineagePathNode(downstreamId, path)));
                }
            }
        }

        var affectedList = new List<AffectedEntity>(affectedMap.Values);

        // Severity-Berechnung:
        // HIGH wenn Dashboards oder Pipelines betroffen sind
        // MEDIUM bei ExternalService
        // LOW bei reinen Tabellen/Spalten
        string severity = "LOW";
        foreach (var entity in affectedList)
        {
            if (entity.Type is LineageNodeType.Dashboard or LineageNodeType.Pipeline)
            {
                severity = "HIGH";
                break;
            }
            if (entity.Type == LineageNodeType.ExternalService)
            {
                severity = "MEDIUM";
            }
        }

        sw.Stop();
        GatewayDiagnostics.LineageTraversalDuration.Record(sw.Elapsed.TotalMilliseconds);
        GatewayDiagnostics.SetSafeTag(activity, "Lineage.Traverse", "nodes.visited", visited.Count);
        GatewayDiagnostics.SetSafeTag(activity, "Lineage.Traverse", "cycle.detected", containsCycles);

        return new ConsentRevocationImpactReport(
            severity,
            affectedList.Count,
            affectedList,
            containsCycles);
    }

    public sealed class LineagePathNode(string nodeId, LineagePathNode? parent)
    {
        public string NodeId { get; } = nodeId;
        public LineagePathNode? Parent { get; } = parent;

        public bool Contains(string id)
        {
            var curr = this;
            while (curr != null)
            {
                if (string.Equals(curr.NodeId, id, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
                curr = curr.Parent;
            }
            return false;
        }
    }
}
