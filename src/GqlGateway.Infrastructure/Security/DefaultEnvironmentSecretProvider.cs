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
        if (string.IsNullOrWhiteSpace(secretRef))
        {
            throw new ArgumentException("Secret reference cannot be empty.", nameof(secretRef));
        }

        var candidates = new List<string> { secretRef };

        // 1. If secretRef is a URI (e.g. https://my-vault.vault.azure.net/secrets/hmac-key or /v1)
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

        // 2. Standard aliases
        candidates.Add("HMAC_SECRET");
        candidates.Add("HMAC_SECRET_KEY");
        candidates.Add("Gateway:DataMasking:HmacSecret");
        candidates.Add("Gateway__DataMasking__HmacSecret");

        foreach (var key in candidates)
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
