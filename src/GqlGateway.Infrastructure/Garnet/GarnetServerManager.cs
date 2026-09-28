using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Garnet;
using GqlGateway.Domain.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GqlGateway.Infrastructure.Garnet;

public interface IGarnetServerManager : IDisposable
{
    bool IsRunning { get; }
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
    private GarnetServer? _server;
    private bool _isRunning;

    public bool IsRunning => _isRunning;

    public GarnetServerManager(
        IOptions<GatewayOptions> gatewayOptions,
        ILogger<GarnetServerManager>? logger = null,
        ILoggerFactory? loggerFactory = null)
    {
        _options = gatewayOptions?.Value?.Caching?.Garnet ?? new GarnetOptions();
        _logger = logger;
        _loggerFactory = loggerFactory;
    }

    public void StartServer()
    {
        lock (_syncLock)
        {
            if (_isRunning || !_options.EnableEmbeddedServer)
            {
                return;
            }

            _logger?.LogInformation("Starting embedded Microsoft Garnet server on {Host}:{Port}...", _options.Host, _options.Port);

            var args = new List<string>
            {
                "--port", _options.Port.ToString(),
                "--bind", _options.Host
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
