using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using GqlGateway.Application.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace GqlGateway.Infrastructure.Security;

public sealed class DefaultEnvironmentSecretProvider : IKeyVaultSecretProvider
{
    private readonly IConfiguration _configuration;
    private readonly IHostEnvironment _environment;

    public DefaultEnvironmentSecretProvider(IConfiguration configuration, IHostEnvironment environment)
    {
        _configuration = configuration;
        _environment = environment;
    }

    public byte[] GetSecretBytes(string secretRef)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secretRef);

        var candidates = new List<string> { secretRef };

        // 1. If secretRef is a URI (e.g. https://my-vault.vault.azure.net/secrets/itsm-webhook-secret)
        if (Uri.TryCreate(secretRef, UriKind.Absolute, out var uri))
        {
            var segments = uri.Segments
                .Select(s => s.Trim('/'))
                .Where(s => !string.IsNullOrEmpty(s) && !string.Equals(s, "secrets", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (segments.Count > 0)
            {
                var secretName = segments[0];
                candidates.Add(secretName);
                candidates.Add(secretName.Replace("-", "_"));
                candidates.Add(secretName.Replace("-", ":"));
            }
        }

        // 2. Namespaced aliases strictly derived from the requested secretRef
        var cleanRef = secretRef.Replace(":", "__").Replace("-", "_").ToUpperInvariant();
        candidates.Add(cleanRef);
        candidates.Add(secretRef.Replace("-", "_"));
        candidates.Add(secretRef.Replace("-", ":"));

        // Dedicated namespaced candidate for itsm
        if (secretRef.StartsWith("itsm:", StringComparison.OrdinalIgnoreCase) || secretRef.Contains("itsm", StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add("ITSM__WEBHOOK_SECRET");
            candidates.Add("ITSM_WEBHOOK_SECRET");
            candidates.Add("Gateway:Itsm:WebhookSecret");
        }
        else if (secretRef.Contains("hmac", StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add("HMAC_SECRET");
            candidates.Add("HMAC_SECRET_KEY");
            candidates.Add("Gateway:DataMasking:HmacSecret");
            candidates.Add("Gateway__DataMasking__HmacSecret");
        }

        foreach (var key in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var secretVal = _configuration[key];
            if (!string.IsNullOrWhiteSpace(secretVal))
            {
                return Encoding.UTF8.GetBytes(secretVal);
            }

            var envVal = Environment.GetEnvironmentVariable(key.Replace(":", "__").Replace("-", "_"));
            if (!string.IsNullOrWhiteSpace(envVal))
            {
                return Encoding.UTF8.GetBytes(envVal);
            }

            envVal = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrWhiteSpace(envVal))
            {
                return Encoding.UTF8.GetBytes(envVal);
            }
        }

        // In Development, allow using the secret reference itself as dev key
        if (_environment.IsDevelopment())
        {
            return Encoding.UTF8.GetBytes(secretRef);
        }

        // Fail-fast in non-development if secret cannot be resolved from Key Vault
        throw new InvalidOperationException($"Sicherheitsfehler: Das Secret '{secretRef}' konnte weder über Azure Key Vault / Konfiguration noch Umgebungsvariablen aufgelöst werden.");
    }
}
