namespace GqlGateway.Application.Governance;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Casbin;
using Casbin.Model;
using GqlGateway.Application.Interfaces;
using GqlGateway.Domain.Common;
using GqlGateway.Domain.Diagnostics;
using GqlGateway.Domain.Interfaces;
using GqlGateway.Domain.Model;

public sealed class CasbinEnforcementService : IPolicyEnforcementService
{
    private readonly ConcurrentDictionary<string, Enforcer> _tenantEnforcers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<(string Tenant, string Sub, string Obj), bool> _decisionCache = new();
    private readonly string _modelText;
    private long _policyEpoch = 1;

    public CasbinEnforcementService(string? modelConfigPath = null)
    {
        if (!string.IsNullOrWhiteSpace(modelConfigPath) && File.Exists(modelConfigPath))
        {
            _modelText = File.ReadAllText(modelConfigPath);
        }
        else
        {
            _modelText = @"
[request_definition]
r = sub, tenant, obj, act, ctx

[policy_definition]
p = sub, tenant, obj, act, sub_rule, eft

[role_definition]
g = _, _

[policy_effect]
e = some(where (p.eft == allow)) && !some(where (p.eft == deny))

[matchers]
m = g(r.sub, p.sub) && r.tenant == p.tenant && keyMatch2(r.obj, p.obj) && (r.act == p.act || p.act == ""*"") && eval(p.sub_rule)
";
        }
    }

    public long CurrentEpoch => Interlocked.Read(ref _policyEpoch);

    public Enforcer GetOrCreateEnforcer(TenantId tenant)
    {
        return _tenantEnforcers.GetOrAdd(tenant.Value, _ =>
        {
            var model = DefaultModel.CreateFromText(_modelText);
            return new Enforcer(model);
        });
    }

    public void AddPolicy(TenantId tenant, string sub, string obj, string act, string subRule = "true", string eft = "allow")
    {
        _decisionCache.Clear();
        var enforcer = GetOrCreateEnforcer(tenant);
        enforcer.AddPolicy(sub, tenant.Value, obj, act, subRule, eft);
    }

    public void AddRoleForUser(TenantId tenant, string user, string role)
    {
        _decisionCache.Clear();
        var enforcer = GetOrCreateEnforcer(tenant);
        enforcer.AddGroupingPolicy(user, role);
    }

    public ValueTask<TableAccessDecision> EvaluatePolicyAsync(
        SecurityEvaluationContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var tableStr = context.TargetTable.ToString();
        var cacheKey = (context.Tenant.Value, context.UserSid.Value, tableStr);

        if (_decisionCache.TryGetValue(cacheKey, out bool cachedAllowed))
        {
            if (cachedAllowed)
            {
                var decision = TableAccessDecision.Allowed(
                    context.TargetTable,
                    new Dictionary<string, ColumnAccessLevel>(),
                    hasUnconstrainedColumnAllow: true);
                return ValueTask.FromResult(decision);
            }

            return ValueTask.FromResult(TableAccessDecision.Denied(
                context.TargetTable,
                $"Access to table '{tableStr}' denied by ABAC policy for tenant '{context.Tenant.Value}'."));
        }

        var sw = Stopwatch.StartNew();
        var enforcer = GetOrCreateEnforcer(context.Tenant);

        // 1. Evaluate user SID and groups without allocating lists or LINQ iterators
        bool allowed = false;

        try
        {
            // r = sub, tenant, obj, act, ctx
            if (enforcer.Enforce(context.UserSid.Value, context.Tenant.Value, tableStr, "read", context))
            {
                allowed = true;
            }
            else
            {
                foreach (var groupSid in context.GroupSids)
                {
                    if (enforcer.Enforce(groupSid.Value, context.Tenant.Value, tableStr, "read", context))
                    {
                        allowed = true;
                        break;
                    }
                }
            }
        }
        catch (ArgumentException)
        {
            allowed = false;
        }

        _decisionCache.TryAdd(cacheKey, allowed);

        sw.Stop();
        GatewayDiagnostics.PolicyEvaluationDuration.Record(sw.Elapsed.TotalMilliseconds);

        var activity = Activity.Current;
        if (activity != null)
        {
            GatewayDiagnostics.SetSafeTag(activity, "Governance.CasbinAbacEnforcement", "user.sid", context.UserSid.Value);
            GatewayDiagnostics.SetSafeTag(activity, "Governance.CasbinAbacEnforcement", "tenant.id", context.Tenant.Value);
            GatewayDiagnostics.SetSafeTag(activity, "Governance.CasbinAbacEnforcement", "policy.match", allowed ? "allow" : "deny");
            GatewayDiagnostics.SetSafeTag(activity, "Governance.CasbinAbacEnforcement", "latency.ms", sw.Elapsed.TotalMilliseconds);
        }

        if (allowed)
        {
            var decision = TableAccessDecision.Allowed(
                context.TargetTable,
                new Dictionary<string, ColumnAccessLevel>(),
                hasUnconstrainedColumnAllow: true);
            return ValueTask.FromResult(decision);
        }

        GatewayDiagnostics.ForbiddenRequestsCounter.Add(1);
        return ValueTask.FromResult(TableAccessDecision.Denied(
            context.TargetTable,
            $"Access to table '{tableStr}' denied by ABAC policy for tenant '{context.Tenant.Value}'."));
    }

    public Task ReloadPoliciesAsync(TenantId tenant, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _policyEpoch);
        _decisionCache.Clear();
        _tenantEnforcers.TryRemove(tenant.Value, out _);
        return Task.CompletedTask;
    }
}
