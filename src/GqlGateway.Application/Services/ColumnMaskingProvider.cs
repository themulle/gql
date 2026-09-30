using System.Globalization;
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

        // SEC-SPEC-02: Prevent Type Confusion & Hash Collisions on raw binary byte[]
        if (rawValue is byte[] rawBytes)
        {
            switch (ruleType)
            {
                case "NULLIFY":
                    return null;

                case "REDACT":
                    return rule.Replacement ?? "REDACTED";

                case "HMAC":
                case "HMAC_SHA256":
                    return ComputeHmacSha256(rawBytes, rule.HmacKeyId ?? _options.HmacKeyId);

                default:
                    // String patterns (email, iban, regex) do not apply to raw byte[] - fail-closed
                    return rule.Replacement ?? "REDACTED";
            }
        }

        // SEC-SPEC-04: Deterministic culture-invariant UTC normalization for timestamps
        string textValue = rawValue switch
        {
            DateTime dt => (dt.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(dt, DateTimeKind.Utc)
                : dt.ToUniversalTime()).ToString("O", CultureInfo.InvariantCulture),
            DateTimeOffset dto => dto.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            DateOnly d => d.ToString("O", CultureInfo.InvariantCulture),
            TimeOnly t => t.ToString("O", CultureInfo.InvariantCulture),
            TimeSpan ts => ts.ToString("c", CultureInfo.InvariantCulture),
            string s when s.Length >= 19 && s[10] == 'T' && !s.EndsWith('Z') && !s.Contains('+') && s.IndexOf('-', 11) == -1 => s + "Z",
            _ => rawValue.ToString() ?? string.Empty
        };

        switch (ruleType)
        {
            case "NULLIFY":
                return null;

            case "REDACT":
                return rule.Replacement ?? "REDACTED";

            case "HMAC":
            case "HMAC_SHA256":
                return ComputeHmacSha256(textValue, rule.HmacKeyId ?? _options.HmacKeyId);

            case "MASK_EMAIL":
                return MaskEmail(textValue);

            case "MASK_IBAN":
                return MaskIban(textValue);

            case "MASK_PHONE":
                return MaskPhone(textValue);

            case "REGEX":
                return ApplyRegexOrFormatMask(columnName, textValue, rule);

            default:
                return rule.Replacement ?? "REDACTED";
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

    private string ComputeHmacSha256(byte[] inputBytes, string? hmacKeyId)
    {
        var keyToUse = GetOrDeriveKey(hmacKeyId);
        Span<byte> hashBytes = stackalloc byte[32];
        HMACSHA256.HashData(keyToUse, inputBytes, hashBytes);
        return Convert.ToHexString(hashBytes);
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

        if (string.Equals(rule.PatternOrFormat, "MASK_EMAIL", StringComparison.OrdinalIgnoreCase))
        {
            return MaskEmail(text);
        }

        if (string.Equals(rule.PatternOrFormat, "MASK_LAST_FOUR", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(rule.PatternOrFormat, "MASK_IBAN", StringComparison.OrdinalIgnoreCase))
        {
            return MaskIban(text);
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

        return string.Create(text.Length, text, static (span, src) =>
        {
            src.AsSpan(0, 2).CopyTo(span);
            span.Slice(2, span.Length - 4).Fill('*');
            src.AsSpan(src.Length - 2).CopyTo(span[^2..]);
        });
    }

    private static string MaskEmail(string email)
    {
        ReadOnlySpan<char> span = email.AsSpan();
        int atIndex = span.IndexOf('@');
        if (atIndex <= 1)
        {
            return "***@***";
        }

        char firstChar = span[0];
        ReadOnlySpan<char> domain = span[(atIndex + 1)..];
        int dotIndex = domain.LastIndexOf('.');

        if (dotIndex > 0)
        {
            int tldStart = atIndex + 1 + dotIndex + 1;
            int tldLength = span.Length - tldStart;
            int totalLength = 9 + tldLength;

            return string.Create(totalLength, (email, firstChar, tldStart, tldLength), static (buffer, state) =>
            {
                buffer[0] = state.firstChar;
                "***@***.".AsSpan().CopyTo(buffer[1..9]);
                state.email.AsSpan(state.tldStart, state.tldLength).CopyTo(buffer[9..]);
            });
        }
        else
        {
            return string.Create(8, firstChar, static (buffer, ch) =>
            {
                buffer[0] = ch;
                "***@***".AsSpan().CopyTo(buffer[1..]);
            });
        }
    }

    private static string MaskIban(string iban)
    {
        Span<char> clean = stackalloc char[34];
        char[]? rented = null;
        int cleanLen = 0;
        ReadOnlySpan<char> ibanSpan = iban.AsSpan();

        for (int i = 0; i < ibanSpan.Length; i++)
        {
            char c = ibanSpan[i];
            if (c != ' ')
            {
                if (cleanLen == clean.Length)
                {
                    int newSize = clean.Length * 2;
                    var newRented = System.Buffers.ArrayPool<char>.Shared.Rent(newSize);
                    clean.CopyTo(newRented);
                    if (rented != null)
                    {
                        System.Buffers.ArrayPool<char>.Shared.Return(rented);
                    }
                    rented = newRented;
                    clean = rented;
                }
                clean[cleanLen++] = c;
            }
        }

        try
        {
            if (cleanLen < 8)
            {
                return "****";
            }

            var cleanSpan = clean[..cleanLen];
            var country = cleanSpan[..2];
            var lastDigits = cleanSpan[^4..];

            return string.Create(19, (country[0], country[1], lastDigits[0], lastDigits[1], lastDigits[2], lastDigits[3]), static (buffer, state) =>
            {
                buffer[0] = state.Item1;
                buffer[1] = state.Item2;
                "** **** **** ".AsSpan().CopyTo(buffer[2..15]);
                buffer[15] = state.Item3;
                buffer[16] = state.Item4;
                buffer[17] = state.Item5;
                buffer[18] = state.Item6;
            });
        }
        finally
        {
            if (rented != null)
            {
                System.Buffers.ArrayPool<char>.Shared.Return(rented);
            }
        }
    }

    private static string MaskPhone(string phone)
    {
        if (phone.Length <= 4)
        {
            return new string('*', phone.Length);
        }

        int unmasked = phone.Length <= 8 ? 1 : 3;
        return string.Create(phone.Length, (phone, unmasked), static (span, state) =>
        {
            state.phone.AsSpan(0, state.unmasked).CopyTo(span);
            span.Slice(state.unmasked, span.Length - 2 * state.unmasked).Fill('*');
            state.phone.AsSpan(span.Length - state.unmasked).CopyTo(span[^state.unmasked..]);
        });
    }
}
