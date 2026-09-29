namespace GqlGateway.Application.Federation.Services;

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using GqlGateway.Application.Federation.Interfaces;
using GqlGateway.Domain.Common;
using GqlGateway.Domain.Interfaces;
using GqlGateway.Domain.Model;
using GqlGateway.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public sealed class SubgraphResultMasker : ISubgraphResultMasker
{
    private readonly IColumnMaskingProvider _maskingProvider;
    private readonly IOptions<GatewayOptions> _options;
    private readonly ILogger<SubgraphResultMasker> _logger;

    private static readonly MaskingRule EmailMaskRule = new() { RuleType = "MASK_EMAIL" };
    private static readonly MaskingRule IbanMaskRule = new() { RuleType = "MASK_IBAN" };
    private static readonly MaskingRule PhoneMaskRule = new() { RuleType = "MASK_PHONE" };
    private static readonly MaskingRule RedactMaskRule = new() { RuleType = "REDACT", Replacement = "[REDACTED]" };

    public SubgraphResultMasker(
        IColumnMaskingProvider maskingProvider,
        IOptions<GatewayOptions> options,
        ILogger<SubgraphResultMasker> logger)
    {
        _maskingProvider = maskingProvider ?? throw new ArgumentNullException(nameof(maskingProvider));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public object? MaskResultData(object? data, ClaimsPrincipal? principal)
    {
        if (data == null) return null;

        var fedOptions = _options.Value.Federation;
        if (!fedOptions.EnableResultMasking || _options.Value.IsColumnMaskingDisabled)
        {
            return data;
        }

        // Privileged governance roles with full cleartext access
        if (principal != null)
        {
            var roles = principal.GetUserRoles();
            if (roles.Contains("GovernanceAdmin") || roles.Contains("ClusterAdmin"))
            {
                return data;
            }
        }

        return MaskResultData(data, principal, null);
    }

    public object? MaskResultData(object? data, ClaimsPrincipal? principal, IReadOnlyDictionary<string, string>? aliasToFieldMap)
    {
        if (data == null) return null;

        var fedOptions = _options.Value.Federation;
        if (!fedOptions.EnableResultMasking || _options.Value.IsColumnMaskingDisabled)
        {
            return data;
        }

        if (principal != null)
        {
            var roles = principal.GetUserRoles();
            if (roles.Contains("GovernanceAdmin") || roles.Contains("ClusterAdmin"))
            {
                return data;
            }
        }

        return MaskRecursive(data, aliasToFieldMap);
    }

    private object? MaskRecursive(object? node, IReadOnlyDictionary<string, string>? aliasToFieldMap)
    {
        if (node == null) return null;

        if (node is IReadOnlyDictionary<string, object?> dict)
        {
            var result = new Dictionary<string, object?>(dict.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in dict)
            {
                if (ShouldMaskField(k, aliasToFieldMap, out var rule))
                {
                    result[k] = _maskingProvider.MaskValue(k, v, rule);
                }
                else
                {
                    result[k] = MaskRecursive(v, aliasToFieldMap);
                }
            }
            return result;
        }

        if (node is IDictionary legacyDict)
        {
            var result = new Dictionary<string, object?>(legacyDict.Count, StringComparer.OrdinalIgnoreCase);
            foreach (DictionaryEntry entry in legacyDict)
            {
                var k = entry.Key?.ToString() ?? string.Empty;
                var v = entry.Value;
                if (ShouldMaskField(k, aliasToFieldMap, out var rule))
                {
                    result[k] = _maskingProvider.MaskValue(k, v, rule);
                }
                else
                {
                    result[k] = MaskRecursive(v, aliasToFieldMap);
                }
            }
            return result;
        }

        if (node is IEnumerable list && node is not string)
        {
            var resultList = new List<object?>();
            foreach (var item in list)
            {
                resultList.Add(MaskRecursive(item, aliasToFieldMap));
            }
            return resultList;
        }

        return node;
    }

    private static bool ShouldMaskField(string fieldOrAliasName, IReadOnlyDictionary<string, string>? aliasToFieldMap, out MaskingRule rule)
    {
        if (aliasToFieldMap != null && aliasToFieldMap.TryGetValue(fieldOrAliasName, out var realFieldName))
        {
            if (IsSensitiveFieldName(realFieldName, out rule))
            {
                return true;
            }
        }

        return IsSensitiveFieldName(fieldOrAliasName, out rule);
    }

    private static bool IsSensitiveFieldName(string fieldName, out MaskingRule rule)
    {
        var lower = fieldName.ToLowerInvariant();

        if (lower.Contains("email", StringComparison.Ordinal) || lower.EndsWith("mail", StringComparison.Ordinal))
        {
            rule = EmailMaskRule;
            return true;
        }

        if (lower.Contains("iban", StringComparison.Ordinal) || lower.Contains("bankaccount", StringComparison.Ordinal))
        {
            rule = IbanMaskRule;
            return true;
        }

        if (lower.Contains("phone", StringComparison.Ordinal) || lower.Contains("mobile", StringComparison.Ordinal) || lower.Contains("telefon", StringComparison.Ordinal))
        {
            rule = PhoneMaskRule;
            return true;
        }

        if (lower == "ssn" || lower.Contains("socialsecurity", StringComparison.Ordinal) || lower == "salary" ||
            lower == "gehalt" || lower == "diagnosis" || lower == "medicalrecord" ||
            lower == "healthcondition" || lower.Contains("creditcard", StringComparison.Ordinal) ||
            lower.Contains("passwort", StringComparison.Ordinal) || lower.Contains("password", StringComparison.Ordinal))
        {
            rule = RedactMaskRule;
            return true;
        }

        rule = RedactMaskRule;
        return false;
    }
}
