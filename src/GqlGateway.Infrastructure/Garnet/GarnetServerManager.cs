using System;
using System.Collections.Generic;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Garnet;
using GqlGateway.Application.Interfaces;
using GqlGateway.Domain.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GqlGateway.Infrastructure.Garnet;

public interface IGarnetServerManager : IDisposable
{
    bool IsRunning { get; }

    /// <summary>
    /// Password the in-process RESP client must present (Garnet runs with <c>--auth Password</c>).
    /// </summary>
    string ClientPassword { get; }

    void StartServer();
    void StopServer();
}

/// <summary>
/// Manages the lifecycle of an embedded Microsoft Garnet cache server (RESP-compatible, Tsavorite-powered).
/// Can be operated as an IHostedService in ASP.NET Core or started imperatively.
/// </summary>
public sealed class GarnetServerManager : IGarnetServerManager, IHostedService
{
    private readonly GarnetOptions _options;
    private readonly ILogger<GarnetServerManager>? _logger;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly object _syncLock = new();
    private readonly string? _environmentName;
    private GarnetServer? _server;
    private bool _isRunning;

    public bool IsRunning => _isRunning;

    public string ClientPassword { get; }

    public GarnetServerManager(
        IOptions<GatewayOptions> gatewayOptions,
        ILogger<GarnetServerManager>? logger = null,
        ILoggerFactory? loggerFactory = null,
        IKeyVaultSecretProvider? secretProvider = null,
        IHostEnvironment? environment = null)
    {
        _options = gatewayOptions?.Value?.Caching?.Garnet ?? new GarnetOptions();
        _logger = logger;
        _loggerFactory = loggerFactory;
        _environmentName = environment?.EnvironmentName
                           ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
                           ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
        ClientPassword = ResolvePassword(_options, secretProvider);
    }

    /// <summary>
    /// SEC H-01: Garnet always runs with password authentication. Without an explicit secret reference an
    /// ephemeral random password is generated per process (only the in-process client needs to know it).
    /// </summary>
    private static string ResolvePassword(GarnetOptions options, IKeyVaultSecretProvider? secretProvider)
    {
        var secretRef = options.PasswordSecretRef;
        if (!string.IsNullOrWhiteSpace(secretRef))
        {
            byte[]? secretBytes = null;
            if (secretProvider != null)
            {
                secretBytes = secretProvider.GetSecretBytes(secretRef);
            }
            else
            {
                var envValue = Environment.GetEnvironmentVariable(secretRef.Replace(":", "__").Replace("-", "_"))
                               ?? Environment.GetEnvironmentVariable(secretRef);
                if (!string.IsNullOrWhiteSpace(envValue))
                {
                    secretBytes = Encoding.UTF8.GetBytes(envValue);
                }
            }

            if (secretBytes == null || secretBytes.Length == 0)
            {
                throw new InvalidOperationException($"Sicherheitsfehler: Das Garnet-Passwort ('{secretRef}') konnte nicht aufgelöst werden.");
            }

            return Encoding.UTF8.GetString(secretBytes);
        }

        return Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
    }

    internal static bool IsLoopbackBinding(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        var trimmed = host.Trim().Trim('[', ']');
        if (string.Equals(trimmed, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(trimmed, out var ip) && IPAddress.IsLoopback(ip);
    }

    private bool IsDevelopmentEnvironment() =>
        string.Equals(_environmentName, "Development", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(_environmentName, "Testing", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(_environmentName, "Test", StringComparison.OrdinalIgnoreCase);

    public void StartServer()
    {
        lock (_syncLock)
        {
            if (_isRunning || !_options.EnableEmbeddedServer)
            {
                return;
            }

            // SEC H-01: outside Development the embedded cache must never be reachable from the network
            // (no TLS on the RESP port; consent decisions and epochs live there).
            if (!IsLoopbackBinding(_options.Host) && !IsDevelopmentEnvironment())
            {
                throw new InvalidOperationException(
                    $"Sicherheitsfehler: Der eingebettete Garnet-Server darf außerhalb von Development nur an Loopback gebunden werden (konfiguriert: '{_options.Host}').");
            }

            _logger?.LogInformation("Starting embedded Microsoft Garnet server on {Host}:{Port}...", _options.Host, _options.Port);

            var args = new List<string>
            {
                "--port", _options.Port.ToString(),
                "--bind", _options.Host,
                "--auth", "Password",
                "--password", ClientPassword
            };

            if (!string.IsNullOrWhiteSpace(_options.CheckpointDir))
            {
                args.Add("--checkpointdir");
                args.Add(_options.CheckpointDir);
            }

            _server = _loggerFactory != null
                ? new GarnetServer(args.ToArray(), _loggerFactory)
                : new GarnetServer(args.ToArray());

            _server.Start();
            _isRunning = true;

            _logger?.LogInformation("Embedded Microsoft Garnet server is listening on {Host}:{Port}.", _options.Host, _options.Port);
        }
    }

    public void StopServer()
    {
        lock (_syncLock)
        {
            if (!_isRunning)
            {
                return;
            }

            _logger?.LogInformation("Stopping embedded Microsoft Garnet server...");
            try
            {
                _server?.Dispose();
                _server = null;
                _isRunning = false;
                _logger?.LogInformation("Embedded Microsoft Garnet server stopped.");
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Exception occurred while stopping Garnet server.");
            }
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        StartServer();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        StopServer();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        StopServer();
    }
}
