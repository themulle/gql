namespace GqlGateway.Application.Governance;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Casbin;
using Casbin.Model;
using GqlGateway.Application.Interfaces;
using GqlGateway.Application.Services;
using GqlGateway.Domain.Common;
using GqlGateway.Domain.Diagnostics;
using GqlGateway.Domain.Interfaces;
using GqlGateway.Domain.Model;

public sealed class CasbinEnforcementService : IPolicyEnforcementService
{
    private readonly ConcurrentDictionary<string, Enforcer> _tenantEnforcers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, List<CasbinRuleMetadata>> _tenantRules = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, TableAccessDecision> _decisionCache = new(StringComparer.Ordinal);
    private readonly IRlsFilterGenerator _rlsFilterGenerator;
    private readonly string _modelText;
    private long _policyEpoch = 1;

    public sealed record CasbinRuleMetadata(
        string Sub,
        string Tenant,
        string Obj,
        string Act,
        string SubRule,
        string Eft,
        string? RlsFilter = null,
        ConsentRowFilter? CorrelatedRowFilter = null
    );

    public CasbinEnforcementService(
        string? modelConfigPath = null,
        IRlsFilterGenerator? rlsFilterGenerator = null)
    {
        _rlsFilterGenerator = rlsFilterGenerator ?? RlsFilterGenerator.Instance;

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

    public bool HasPolicies(TenantId tenant)
    {
        if (_tenantRules.TryGetValue(tenant.Value, out var rules) && rules.Count > 0)
        {
            return true;
        }

        if (_tenantEnforcers.TryGetValue(tenant.Value, out var enforcer) && enforcer.GetPolicy().Any())
        {
            return true;
        }

        return false;
    }

    public Enforcer GetOrCreateEnforcer(TenantId tenant)
    {
        return _tenantEnforcers.GetOrAdd(tenant.Value, _ =>
        {
            var model = DefaultModel.CreateFromText(_modelText);
            return new Enforcer(model);
        });
    }

    private static readonly string[] DangerousSubRuleTokens =
    [
        "System.", "System;", "Process", "File.", "Directory.", "Assembly", "GetType", "Activator",
        "Environment.", "AppDomain", "MethodInfo", "Invoke", "Type.", "TypeName", "Reflection",
        "DllImport", "Marshal", "Socket", "WebClient", "HttpClient", "Net.", "Unsafe", "Pointer",
        "Diagnostics.", "Compiler", "IO.", "Security.", "Microsoft.", "Configuration", "Registry"
    ];

    public void AddPolicy(
        TenantId tenant,
        string sub,
        string obj,
        string act,
        string subRule = "true",
        string eft = "allow",
        string? rlsFilter = null,
        ConsentRowFilter? correlatedRowFilter = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subRule);
        foreach (var token in DangerousSubRuleTokens)
        {
            if (subRule.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"Sicherheitsfehler: Casbin sub_rule enthält nicht erlaubten Ausdruck '{token}'.", nameof(subRule));
            }

            if (!string.IsNullOrWhiteSpace(rlsFilter) && rlsFilter.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"Sicherheitsfehler: Casbin rls_filter enthält nicht erlaubten Ausdruck '{token}'.", nameof(rlsFilter));
            }
        }

        // Normalize single-quoted strings (length > 1) to double-quoted C# strings for DynamicExpresso
        var normalizedSubRule = Regex.Replace(subRule, @"'([^']{2,})'", "\"$1\"");

        _decisionCache.Clear();
        var enforcer = GetOrCreateEnforcer(tenant);
        enforcer.AddPolicy(sub, tenant.Value, obj, act, normalizedSubRule, eft);

        var ruleMeta = new CasbinRuleMetadata(sub, tenant.Value, obj, act, subRule, eft, rlsFilter, correlatedRowFilter);
        var rules = _tenantRules.GetOrAdd(tenant.Value, _ => new List<CasbinRuleMetadata>());
        lock (rules)
        {
            rules.Add(ruleMeta);
        }
    }

    public void AddRlsPolicy(
        TenantId tenant,
        string sub,
        string obj,
        string rlsFilter,
        string act = "read",
        string subRule = "true")
    {
        AddPolicy(tenant, sub, obj, act, subRule, "allow", rlsFilter: rlsFilter);
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
        var cacheKey = $"{context.Tenant.Value}:{context.UserSid.Value}:{tableStr}:{context.PurposeId}:{context.Department}:{context.Region}:{context.ClearanceLevel}";

        if (_decisionCache.TryGetValue(cacheKey, out var cachedDecision))
        {
            return ValueTask.FromResult(cachedDecision);
        }

        var sw = Stopwatch.StartNew();
        var enforcer = GetOrCreateEnforcer(context.Tenant);

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
        catch (Exception)
        {
            allowed = false;
        }

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

        TableAccessDecision decision;

        if (allowed)
        {
            // Resolve any attached RLS pushdown filters from matching allow rules
            var activeRlsFilters = CollectActiveRlsFilters(context, tableStr, enforcer);

            string? combinedSql = null;
            if (activeRlsFilters.Count > 0)
            {
                combinedSql = activeRlsFilters.Count == 1
                    ? activeRlsFilters[0]
                    : string.Join(" OR ", activeRlsFilters.Select(f => $"({f})"));
            }

            decision = TableAccessDecision.Allowed(
                context.TargetTable,
                new Dictionary<string, ColumnAccessLevel>(),
                rowFilterSql: combinedSql,
                hasUnconstrainedColumnAllow: true);
        }
        else
        {
            GatewayDiagnostics.ForbiddenRequestsCounter.Add(1);
            decision = TableAccessDecision.Denied(
                context.TargetTable,
                $"Access to table '{tableStr}' denied by ABAC policy for tenant '{context.Tenant.Value}'.");
        }

        _decisionCache.TryAdd(cacheKey, decision);
        return ValueTask.FromResult(decision);
    }

    private List<string> CollectActiveRlsFilters(SecurityEvaluationContext context, string tableStr, Enforcer enforcer)
    {
        var result = new List<string>();

        if (!_tenantRules.TryGetValue(context.Tenant.Value, out var rules))
        {
            return result;
        }

        lock (rules)
        {
            foreach (var rule in rules)
            {
                if (!string.Equals(rule.Eft, "allow", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Check subject match
                bool subMatch = string.Equals(rule.Sub, "*", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(rule.Sub, context.UserSid.Value, StringComparison.OrdinalIgnoreCase) ||
                                context.GroupSids.Any(g => string.Equals(g.Value, rule.Sub, StringComparison.OrdinalIgnoreCase)) ||
                                enforcer.HasRoleForUser(context.UserSid.Value, rule.Sub) ||
                                context.GroupSids.Any(g => enforcer.HasRoleForUser(g.Value, rule.Sub));

                if (!subMatch)
                {
                    continue;
                }

                // Check object match
                bool objMatch = MatchObjectPattern(rule.Obj, tableStr);

                if (!objMatch)
                {
                    continue;
                }

                // Handle Correlated Row Filter
                if (rule.CorrelatedRowFilter != null)
                {
                    try
                    {
                        var subquery = _rlsFilterGenerator.BuildCorrelatedSubquery(rule.CorrelatedRowFilter);
                        if (!string.IsNullOrWhiteSpace(subquery))
                        {
                            result.Add(subquery);
                        }
                    }
                    catch
                    {
                        // Ignore generation error on incompatible dialect or skip
                    }
                }

                // Handle Direct RLS SQL Filter with parameter/context interpolation
                if (!string.IsNullOrWhiteSpace(rule.RlsFilter))
                {
                    var interpolated = InterpolateRlsFilter(rule.RlsFilter, context);
                    if (!string.IsNullOrWhiteSpace(interpolated))
                    {
                        result.Add(interpolated);
                    }
                }
            }
        }

        return result;
    }

    private static string InterpolateRlsFilter(string filterTemplate, SecurityEvaluationContext context)
    {
        var result = filterTemplate
            .Replace("${r.sub}", context.UserSid.Value, StringComparison.OrdinalIgnoreCase)
            .Replace("${user_sid}", context.UserSid.Value, StringComparison.OrdinalIgnoreCase)
            .Replace("${r.tenant}", context.Tenant.Value, StringComparison.OrdinalIgnoreCase)
            .Replace("${tenant}", context.Tenant.Value, StringComparison.OrdinalIgnoreCase)
            .Replace("${r.ctx.Department}", context.Department ?? "", StringComparison.OrdinalIgnoreCase)
            .Replace("${department}", context.Department ?? "", StringComparison.OrdinalIgnoreCase)
            .Replace("${r.ctx.Region}", context.Region ?? "", StringComparison.OrdinalIgnoreCase)
            .Replace("${region}", context.Region ?? "", StringComparison.OrdinalIgnoreCase)
            .Replace("${r.ctx.ClearanceLevel}", context.ClearanceLevel ?? "", StringComparison.OrdinalIgnoreCase)
            .Replace("${clearance}", context.ClearanceLevel ?? "", StringComparison.OrdinalIgnoreCase)
            .Replace("${r.ctx.PurposeId}", context.PurposeId ?? "", StringComparison.OrdinalIgnoreCase)
            .Replace("${purpose}", context.PurposeId ?? "", StringComparison.OrdinalIgnoreCase);

        if (context.Attributes != null)
        {
            foreach (var (k, v) in context.Attributes)
            {
                if (v != null)
                {
                    result = result
                        .Replace($"${{attr.{k}}}", v.ToString(), StringComparison.OrdinalIgnoreCase)
                        .Replace($"${{{k}}}", v.ToString(), StringComparison.OrdinalIgnoreCase);
                }
            }
        }

        return result;
    }

    private static bool MatchObjectPattern(string pattern, string target)
    {
        if (string.Equals(pattern, "*", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(pattern, target, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (pattern.EndsWith(".*", StringComparison.Ordinal))
        {
            var prefix = pattern[..^2];
            return target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        if (pattern.Contains('*'))
        {
            var regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$";
            return Regex.IsMatch(target, regex, RegexOptions.IgnoreCase);
        }

        return false;
    }

    public Task ReloadPoliciesAsync(TenantId tenant, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _policyEpoch);
        _decisionCache.Clear();
        _tenantEnforcers.TryRemove(tenant.Value, out _);
        _tenantRules.TryRemove(tenant.Value, out _);
        return Task.CompletedTask;
    }
}
