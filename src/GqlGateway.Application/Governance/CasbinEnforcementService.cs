namespace GqlGateway.Application.Governance;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security;
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

public sealed class CasbinEnforcementService : IPolicyEnforcementService, IDisposable
{
    private readonly ConcurrentDictionary<string, Enforcer> _tenantEnforcers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, List<CasbinRuleMetadata>> _tenantRules = new(StringComparer.OrdinalIgnoreCase);
    private sealed record CachedDecision(TableAccessDecision Decision, long CreatedTimestampTicks);
    private readonly ConcurrentDictionary<string, CachedDecision> _decisionCache = new(StringComparer.Ordinal);
    private static readonly TimeSpan DecisionCacheTtl = TimeSpan.FromSeconds(60);
    private readonly ConcurrentDictionary<string, string> _tenantPolicyFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, FileSystemWatcher> _fileWatchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, System.Timers.Timer> _debounceTimers = new(StringComparer.OrdinalIgnoreCase);
    private readonly IRlsFilterGenerator _rlsFilterGenerator;
    private readonly string _modelText;
    private long _policyEpoch = 1;

    public event Action<TenantId>? OnPolicyReloaded;


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

    private static readonly Regex SafeSubRulePattern = new(
        @"^[a-zA-Z0-9_.\s()|&!=<>',\[\]""+\-/*]+$",
        RegexOptions.Compiled);

    private static readonly Regex SafeRlsFilterPattern = new(
        @"^[a-zA-Z0-9_.\s()=<>!,'""$+\-*/%:@{}]+$",
        RegexOptions.Compiled);

    private static readonly string[] DangerousSqlTokens =
    [
        ";", "--", "/*", "*/", "@@",
        "DROP ", "ALTER ", "TRUNCATE ", "DELETE ", "INSERT ", "UPDATE ", "EXEC ", "EXECUTE ",
        "UNION ", "INTO ", "INFORMATION_SCHEMA", "XP_", "SP_"
    ];

    private static void ValidateSubRuleTokens(string subRule, string? rlsFilter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subRule);

        if (subRule.Length > 500)
        {
            throw new ArgumentException("Sicherheitsfehler: Casbin sub_rule überschreitet die maximale Länge von 500 Zeichen.", nameof(subRule));
        }

        if (!SafeSubRulePattern.IsMatch(subRule))
        {
            throw new ArgumentException("Sicherheitsfehler: Casbin sub_rule enthält nicht erlaubte Zeichen.", nameof(subRule));
        }

        if (!string.IsNullOrWhiteSpace(rlsFilter))
        {
            if (rlsFilter.Length > 1000)
            {
                throw new ArgumentException("Sicherheitsfehler: Casbin rls_filter überschreitet die maximale Länge von 1000 Zeichen.", nameof(rlsFilter));
            }

            if (!SafeRlsFilterPattern.IsMatch(rlsFilter))
            {
                throw new ArgumentException("Sicherheitsfehler: Casbin rls_filter enthält nicht erlaubte Zeichen.", nameof(rlsFilter));
            }

            foreach (var sqlToken in DangerousSqlTokens)
            {
                if (rlsFilter.Contains(sqlToken, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException($"Sicherheitsfehler: Casbin rls_filter enthält nicht erlaubten SQL-Ausdruck '{sqlToken.Trim()}'.", nameof(rlsFilter));
                }
            }
        }

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
    }

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
        ValidateSubRuleTokens(subRule, rlsFilter);

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
        var groupsStr = context.GroupSids != null && context.GroupSids.Count > 0
            ? string.Join(",", context.GroupSids.Select(g => g.Value).OrderBy(s => s, StringComparer.Ordinal))
            : string.Empty;
        var cacheKey = $"{context.Tenant.Value}:{context.UserSid.Value}:{tableStr}:{context.PurposeId}:{context.Department}:{context.Region}:{context.ClearanceLevel}:G[{groupsStr}]";

        if (_decisionCache.TryGetValue(cacheKey, out var cachedEntry))
        {
            if (Stopwatch.GetElapsedTime(cachedEntry.CreatedTimestampTicks) <= DecisionCacheTtl)
            {
                return ValueTask.FromResult(cachedEntry.Decision);
            }
            _decisionCache.TryRemove(cacheKey, out _);
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
            else if (context.GroupSids != null)
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

        _decisionCache.TryAdd(cacheKey, new CachedDecision(decision, Stopwatch.GetTimestamp()));
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

    private static readonly Regex SafeClaimValueRegex = new(@"^[a-zA-Z0-9\-_.@: ]{1,256}$", RegexOptions.Compiled);

    private static string SanitizeClaimForSql(string? value, string claimName)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        if (!SafeClaimValueRegex.IsMatch(value))
        {
            throw new SecurityException($"Sicherheitsfehler: Claim '{claimName}' enthält ungültige Zeichen für SQL-RLS-Interpolation.");
        }
        return value.Replace("'", "''");
    }

    private static string InterpolateRlsFilter(string filterTemplate, SecurityEvaluationContext context)
    {
        var userSid = SanitizeClaimForSql(context.UserSid.Value, "user_sid");
        var tenant = SanitizeClaimForSql(context.Tenant.Value, "tenant");
        var department = SanitizeClaimForSql(context.Department, "department");
        var region = SanitizeClaimForSql(context.Region, "region");
        var clearance = SanitizeClaimForSql(context.ClearanceLevel, "clearance");
        var purpose = SanitizeClaimForSql(context.PurposeId, "purpose");

        var result = filterTemplate
            .Replace("${r.sub}", userSid, StringComparison.OrdinalIgnoreCase)
            .Replace("${user_sid}", userSid, StringComparison.OrdinalIgnoreCase)
            .Replace("${r.tenant}", tenant, StringComparison.OrdinalIgnoreCase)
            .Replace("${tenant}", tenant, StringComparison.OrdinalIgnoreCase)
            .Replace("${r.ctx.Department}", department, StringComparison.OrdinalIgnoreCase)
            .Replace("${department}", department, StringComparison.OrdinalIgnoreCase)
            .Replace("${r.ctx.Region}", region, StringComparison.OrdinalIgnoreCase)
            .Replace("${region}", region, StringComparison.OrdinalIgnoreCase)
            .Replace("${r.ctx.ClearanceLevel}", clearance, StringComparison.OrdinalIgnoreCase)
            .Replace("${clearance}", clearance, StringComparison.OrdinalIgnoreCase)
            .Replace("${r.ctx.PurposeId}", purpose, StringComparison.OrdinalIgnoreCase)
            .Replace("${purpose}", purpose, StringComparison.OrdinalIgnoreCase);

        if (context.Attributes != null)
        {
            foreach (var (k, v) in context.Attributes)
            {
                if (v != null)
                {
                    var sanitized = SanitizeClaimForSql(v.ToString(), k);
                    result = result
                        .Replace($"${{attr.{k}}}", sanitized, StringComparison.OrdinalIgnoreCase)
                        .Replace($"${{{k}}}", sanitized, StringComparison.OrdinalIgnoreCase);
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
            try
            {
                var regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$";
                return Regex.IsMatch(target, regex, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200));
            }
            catch (RegexMatchTimeoutException)
            {
                return false;
            }
        }

        return false;
    }

    public void LoadPolicyFromText(TenantId tenant, string policyText)
    {
        ArgumentNullException.ThrowIfNull(policyText);

        var model = DefaultModel.CreateFromText(_modelText);
        var newEnforcer = new Enforcer(model);
        var newRules = new List<CasbinRuleMetadata>();

        var lines = policyText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.StartsWith('#') || string.IsNullOrWhiteSpace(line)) continue;

            var parts = line.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length == 0) continue;

            var type = parts[0].ToLowerInvariant();
            if (type == "p")
            {
                // p, sub, tenant, obj, act, [subRule], [eft], [rlsFilter]
                if (parts.Length >= 4)
                {
                    var sub = parts[1];
                    var ruleTenant = parts.Length > 2 && !string.IsNullOrWhiteSpace(parts[2]) ? parts[2] : tenant.Value;
                    var obj = parts.Length > 3 ? parts[3] : "*";
                    var act = parts.Length > 4 ? parts[4] : "read";
                    var subRule = parts.Length > 5 && !string.IsNullOrWhiteSpace(parts[5]) ? parts[5] : "true";
                    var eft = parts.Length > 6 && !string.IsNullOrWhiteSpace(parts[6]) ? parts[6] : "allow";
                    var rlsFilter = parts.Length > 7 && !string.IsNullOrWhiteSpace(parts[7]) ? parts[7] : null;

                    ValidateSubRuleTokens(subRule, rlsFilter);
                    var normalizedSubRule = Regex.Replace(subRule, @"'([^']{2,})'", "\"$1\"");
                    newEnforcer.AddPolicy(sub, ruleTenant, obj, act, normalizedSubRule, eft);
                    newRules.Add(new CasbinRuleMetadata(sub, ruleTenant, obj, act, subRule, eft, rlsFilter, null));
                }
            }
            else if (type == "g")
            {
                // g, user, role
                if (parts.Length >= 3)
                {
                    newEnforcer.AddGroupingPolicy(parts[1], parts[2]);
                }
            }
        }

        // Atomically replace enforcer and rules, then invalidate cache and notify
        _tenantEnforcers[tenant.Value] = newEnforcer;
        _tenantRules[tenant.Value] = newRules;
        _decisionCache.Clear();

        Interlocked.Increment(ref _policyEpoch);
        OnPolicyReloaded?.Invoke(tenant);
    }

    public void LoadPolicyFromFile(TenantId tenant, string filePath, bool watchFile = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"Casbin policy file not found: {filePath}", filePath);
        }

        _tenantPolicyFiles[tenant.Value] = Path.GetFullPath(filePath);
        var content = File.ReadAllText(filePath);
        LoadPolicyFromText(tenant, content);

        if (watchFile)
        {
            EnableFileWatcher(tenant, filePath);
        }
    }

    private void EnableFileWatcher(TenantId tenant, string filePath)
    {
        var fullPath = Path.GetFullPath(filePath);
        var dir = Path.GetDirectoryName(fullPath) ?? Directory.GetCurrentDirectory();
        var fileName = Path.GetFileName(fullPath);

        if (_fileWatchers.TryRemove(tenant.Value, out var existingWatcher))
        {
            existingWatcher.Dispose();
        }

        if (_debounceTimers.TryRemove(tenant.Value, out var existingTimer))
        {
            existingTimer.Dispose();
        }

        var watcher = new FileSystemWatcher(dir, fileName)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.CreationTime,
            EnableRaisingEvents = true
        };

        var debounceTimer = new System.Timers.Timer(100) { AutoReset = false };
        debounceTimer.Elapsed += (_, _) =>
        {
            try
            {
                if (File.Exists(fullPath))
                {
                    var text = File.ReadAllText(fullPath);
                    LoadPolicyFromText(tenant, text);
                }
            }
            catch
            {
                // Ignore transient file lock during write
            }
        };

        void OnFileEvent(object sender, FileSystemEventArgs e)
        {
            debounceTimer.Stop();
            debounceTimer.Start();
        }

        watcher.Changed += OnFileEvent;
        watcher.Created += OnFileEvent;
        watcher.Renamed += (_, _) =>
        {
            debounceTimer.Stop();
            debounceTimer.Start();
        };

        _fileWatchers[tenant.Value] = watcher;
        _debounceTimers[tenant.Value] = debounceTimer;
    }

    public Task ReloadPoliciesAsync(TenantId tenant, CancellationToken ct = default)
    {
        if (_tenantPolicyFiles.TryGetValue(tenant.Value, out var filePath) && File.Exists(filePath))
        {
            var text = File.ReadAllText(filePath);
            LoadPolicyFromText(tenant, text);
        }
        else
        {
            Interlocked.Increment(ref _policyEpoch);
            _decisionCache.Clear();
            _tenantEnforcers.TryRemove(tenant.Value, out _);
            _tenantRules.TryRemove(tenant.Value, out _);
            OnPolicyReloaded?.Invoke(tenant);
        }

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        foreach (var timer in _debounceTimers.Values)
        {
            timer.Dispose();
        }
        _debounceTimers.Clear();

        foreach (var watcher in _fileWatchers.Values)
        {
            watcher.Dispose();
        }
        _fileWatchers.Clear();
    }
}

