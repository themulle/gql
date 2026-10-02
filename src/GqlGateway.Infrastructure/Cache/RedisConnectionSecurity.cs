using System.Text;
using GqlGateway.Application.Interfaces;
using GqlGateway.Domain.Options;
using Microsoft.Extensions.Hosting;
using StackExchange.Redis;

namespace GqlGateway.Infrastructure.Cache;

/// <summary>
/// SEC H-01: Hardening of the external Redis connection that carries consent decisions, policy epochs,
/// rate-limit buckets and idempotency keys.
/// </summary>
public static class RedisConnectionSecurity
{
    /// <summary>
    /// Applies the password from <see cref="RedisOptions.PasswordSecretRef"/> (if configured) and enforces,
    /// outside Development, that the connection is authenticated.
    /// </summary>
    public static ConfigurationOptions Apply(
        ConfigurationOptions configuration,
        RedisOptions redisOptions,
        IKeyVaultSecretProvider? secretProvider,
        IHostEnvironment? environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(redisOptions);

        if (!string.IsNullOrWhiteSpace(redisOptions.PasswordSecretRef))
        {
            if (secretProvider == null)
            {
                throw new InvalidOperationException("Sicherheitsfehler: Caching.Redis.PasswordSecretRef ist gesetzt, aber kein IKeyVaultSecretProvider registriert.");
            }

            configuration.Password = Encoding.UTF8.GetString(secretProvider.GetSecretBytes(redisOptions.PasswordSecretRef));
        }

        var isDevelopment = environment != null &&
                            (environment.IsDevelopment() ||
                             string.Equals(environment.EnvironmentName, "Testing", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(environment.EnvironmentName, "Test", StringComparison.OrdinalIgnoreCase));

        if (!isDevelopment && string.IsNullOrEmpty(configuration.Password))
        {
            throw new InvalidOperationException(
                "Sicherheitsfehler: Redis ohne Authentifizierung ist außerhalb von Development nicht zulässig. Setzen Sie Caching.Redis.PasswordSecretRef oder ein Passwort im Verbindungsstring.");
        }

        return configuration;
    }
}
