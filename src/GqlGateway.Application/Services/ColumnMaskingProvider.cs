using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using GqlGateway.Application.Interfaces;
using GqlGateway.Domain.Interfaces;
using GqlGateway.Domain.Model;
using GqlGateway.Domain.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace GqlGateway.Application.Services;

public sealed partial class ColumnMaskingProvider : IColumnMaskingProvider
{
    private readonly DataMaskingOptions _options;
    private readonly byte[] _hmacKey;

    public ColumnMaskingProvider(
        IOptions<GatewayOptions>? options = null,
        IKeyVaultSecretProvider? secretProvider = null,
        IHostEnvironment? environment = null)
    {
        _options = options?.Value?.DataMasking ?? new DataMaskingOptions();

        if (secretProvider != null && !string.IsNullOrWhiteSpace(_options.HmacSecretKeyVaultRef))
        {
            _hmacKey = secretProvider.GetSecretBytes(_options.HmacSecretKeyVaultRef);
        }
        else if (environment != null && !environment.IsDevelopment())
        {
            throw new InvalidOperationException("Sicherheitsfehler: In Nicht-Entwicklungsumgebungen muss der HMAC-Schlüssel zwingend über einen IKeyVaultSecretProvider aufgelöst werden.");
        }
        else
        {
            var keySecret = string.IsNullOrEmpty(_options.HmacSecretKeyVaultRef)
                ? "dev-only-hmac-salt-secure-fallback"
                : _options.HmacSecretKeyVaultRef;
            _hmacKey = Encoding.UTF8.GetBytes(keySecret);
        }

        if (_hmacKey == null || _hmacKey.Length == 0)
        {
            throw new InvalidOperationException("HMAC key cannot be empty.");
        }
    }

    public object? MaskValue(string columnName, object? rawValue, MaskingRule rule)
    {
        if (rawValue == null || rawValue is DBNull)
        {
            return null;
        }

        var ruleType = rule.RuleType?.ToUpperInvariant() ?? "REDACT";

        switch (ruleType)
        {
            case "NULLIFY":
                return null;

            case "REDACT":
                return rule.Replacement ?? "REDACTED";

            case "HMAC":
                return ComputeHmacSha256(rawValue.ToString() ?? string.Empty, rule.HmacKeyId ?? _options.HmacKeyId);

            case "REGEX":
                return ApplyRegexOrFormatMask(columnName, rawValue.ToString() ?? string.Empty, rule);

            default:
                return "REDACTED";
        }
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> _derivedKeys = new(StringComparer.Ordinal);

    private byte[] GetOrDeriveKey(string? hmacKeyId)
    {
        if (string.IsNullOrEmpty(hmacKeyId))
        {
            return _hmacKey;
        }

        return _derivedKeys.GetOrAdd(hmacKeyId, static (id, masterKey) =>
        {
            byte[] idBytes = Encoding.UTF8.GetBytes(id);
            return HMACSHA256.HashData(masterKey, idBytes);
        }, _hmacKey);
    }

    private string ComputeHmacSha256(string input, string? hmacKeyId)
    {
        var keyToUse = GetOrDeriveKey(hmacKeyId);
        int maxByteCount = Encoding.UTF8.GetMaxByteCount(input.Length);
        byte[]? rented = null;
        Span<byte> sourceBytes = maxByteCount <= 512
            ? stackalloc byte[512]
            : (rented = System.Buffers.ArrayPool<byte>.Shared.Rent(maxByteCount));

        try
        {
            int written = Encoding.UTF8.GetBytes(input, sourceBytes);
            Span<byte> hashBytes = stackalloc byte[32];
            HMACSHA256.HashData(keyToUse, sourceBytes[..written], hashBytes);
            return Convert.ToHexString(hashBytes);
        }
        finally
        {
            if (rented != null)
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    private static string ApplyRegexOrFormatMask(string columnName, string text, MaskingRule rule)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        if (!string.IsNullOrEmpty(rule.PatternOrFormat) && !string.IsNullOrEmpty(rule.Replacement))
        {
            try
            {
                return Regex.Replace(text, rule.PatternOrFormat, rule.Replacement, RegexOptions.None, TimeSpan.FromMilliseconds(250));
            }
            catch (Exception ex) when (ex is RegexMatchTimeoutException or ArgumentException)
            {
                return "REDACTED";
            }
        }

        // Auto-detect common formats based on column name or value
        if (columnName.Contains("email", StringComparison.OrdinalIgnoreCase) || text.Contains('@'))
        {
            return MaskEmail(text);
        }

        if (columnName.Contains("iban", StringComparison.OrdinalIgnoreCase))
        {
            return MaskIban(text);
        }

        if (columnName.Contains("phone", StringComparison.OrdinalIgnoreCase) || columnName.Contains("tel", StringComparison.OrdinalIgnoreCase))
        {
            return MaskPhone(text);
        }

        // Generic partial mask: keep first and last character
        if (text.Length <= 4)
        {
            return new string('*', text.Length);
        }

        return $"{text[..2]}{new string('*', text.Length - 4)}{text[^2..]}";
    }

    private static string MaskEmail(string email)
    {
        var atIndex = email.IndexOf('@');
        if (atIndex <= 1)
        {
            return "***@***";
        }

        var username = email[..atIndex];
        var domain = email[(atIndex + 1)..];

        var maskedUser = $"{username[0]}***";
        var dotIndex = domain.LastIndexOf('.');
        var maskedDomain = dotIndex > 0
            ? $"***.{domain[(dotIndex + 1)..]}"
            : "***";

        return $"{maskedUser}@{maskedDomain}";
    }

    private static string MaskIban(string iban)
    {
        var clean = iban.Replace(" ", "");
        if (clean.Length < 8)
        {
            return "****";
        }

        var country = clean[..2];
        var lastDigits = clean[^4..];
        return $"{country}** **** **** {lastDigits}";
    }

    private static string MaskPhone(string phone)
    {
        if (phone.Length <= 4)
        {
            return new string('*', phone.Length);
        }

        if (phone.Length <= 8)
        {
            return $"{phone[..1]}{new string('*', phone.Length - 2)}{phone[^1..]}";
        }

        return $"{phone[..3]}{new string('*', phone.Length - 6)}{phone[^3..]}";
    }
}
