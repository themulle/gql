using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security;
using System.Security.Cryptography;
using System.Text.Json;
using GqlGateway.Application.Plugins;
using GqlGateway.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GqlGateway.Infrastructure.Plugins;

public sealed class PluginManager : IPluginManager, IDisposable
{
    private readonly ILogger<PluginManager> _logger;
    private readonly IServiceProvider? _serviceProvider;
    private readonly IOptions<GatewayOptions>? _options;
    private readonly ConcurrentDictionary<string, PluginEntry> _plugins = new(StringComparer.OrdinalIgnoreCase);

    private sealed record PluginEntry(IHttpDataSourcePlugin Plugin, PluginAssemblyLoadContext? Context);

    public PluginManager(
        ILogger<PluginManager> logger,
        IServiceProvider? serviceProvider = null,
        IOptions<GatewayOptions>? options = null)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _options = options;
    }

    public IReadOnlyCollection<IHttpDataSourcePlugin> GetAllPlugins() =>
        _plugins.Values.Select(e => e.Plugin).ToList();

    public IHttpDataSourcePlugin? GetPlugin(string name)
    {
        return _plugins.TryGetValue(name, out var entry) ? entry.Plugin : null;
    }

    public void RegisterPlugin(IHttpDataSourcePlugin plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        _plugins[plugin.Name] = new PluginEntry(plugin, null);
        _logger.LogInformation("Directly registered HTTP Data Source Plugin: {PluginName}", plugin.Name);
    }

    public int LoadPluginsFromDirectory(string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            return 0;
        }

        var fullDirectoryPath = Path.GetFullPath(directoryPath);
        if (!Directory.Exists(fullDirectoryPath))
        {
            _logger.LogInformation("Plugin directory '{Directory}' does not exist or is empty. Skipping plugin discovery.", fullDirectoryPath);
            return 0;
        }

        var dllFiles = Directory.GetFiles(fullDirectoryPath, "*.dll", SearchOption.AllDirectories);
        if (dllFiles.Length == 0)
        {
            return 0;
        }

        // CRIT-01: Cryptographic Integrity Verification via manifest.json
        var manifestPath = Path.Combine(fullDirectoryPath, "manifest.json");
        Dictionary<string, string>? manifest = null;
        if (File.Exists(manifestPath))
        {
            manifest = LoadManifest(manifestPath);
        }
        else
        {
            var env = _serviceProvider?.GetService(typeof(Microsoft.Extensions.Hosting.IHostEnvironment)) as Microsoft.Extensions.Hosting.IHostEnvironment;
            bool isDev = string.Equals(env?.EnvironmentName, "Development", StringComparison.OrdinalIgnoreCase);
            if (_options?.Value.Plugins.RequireIntegrityManifest == true || !isDev)
            {
                throw new SecurityException($"Sicherheitsfehler: Kein Integrity-Manifest (manifest.json) in '{fullDirectoryPath}' vorhanden.");
            }
        }

        int loadedCount = 0;

        foreach (var dllFile in dllFiles)
        {
            var fullDllPath = Path.GetFullPath(dllFile);
            if (!fullDllPath.StartsWith(fullDirectoryPath, StringComparison.Ordinal))
            {
                _logger.LogWarning("Skipping plugin DLL outside configured directory: '{DllPath}'", dllFile);
                continue;
            }

            // Verify integrity against manifest if present or required
            if (manifest != null)
            {
                VerifyPluginIntegrity(fullDllPath, manifest);
            }

            try
            {
                var alc = new PluginAssemblyLoadContext(fullDllPath);
                var assembly = alc.LoadFromAssemblyPath(fullDllPath);

                var pluginTypes = assembly.GetTypes()
                    .Where(t => typeof(IHttpDataSourcePlugin).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface);

                foreach (var type in pluginTypes)
                {
                    try
                    {
                        object? instance = null;
                        if (_serviceProvider != null)
                        {
                            try
                            {
                                instance = Microsoft.Extensions.DependencyInjection.ActivatorUtilities.CreateInstance(_serviceProvider, type);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogDebug(ex, "ActivatorUtilities could not instantiate plugin type '{Type}', falling back to default constructor.", type.FullName);
                            }
                        }

                        instance ??= Activator.CreateInstance(type);

                        if (instance is IHttpDataSourcePlugin pluginInstance)
                        {
                            _plugins[pluginInstance.Name] = new PluginEntry(pluginInstance, alc);
                            _logger.LogInformation("Successfully loaded plugin '{PluginName}' from {DllPath} ({Type})",
                                pluginInstance.Name, dllFile, type.FullName);
                            loadedCount++;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to instantiate plugin type '{Type}' from {DllPath}", type.FullName, dllFile);
                    }
                }
            }
            catch (BadImageFormatException)
            {
                // Not a .NET assembly, skip
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to scan or load potential plugin assembly from {DllPath}", dllFile);
            }
        }

        _logger.LogInformation("Plugin discovery finished. Total plugins loaded: {Count}", loadedCount);
        return loadedCount;
    }

    public void Dispose()
    {
        foreach (var entry in _plugins.Values)
        {
            entry.Context?.Unload();
        }
        _plugins.Clear();
    }

    private static Dictionary<string, string> LoadManifest(string manifestPath)
    {
        var json = File.ReadAllText(manifestPath);
        using var doc = JsonDocument.Parse(json);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (doc.RootElement.ValueKind == JsonValueKind.Object)
        {
            if (doc.RootElement.TryGetProperty("plugins", out var pluginsElem))
            {
                if (pluginsElem.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in pluginsElem.EnumerateArray())
                    {
                        if (item.TryGetProperty("file", out var fileProp) && item.TryGetProperty("sha256", out var hashProp))
                        {
                            result[fileProp.GetString()!] = hashProp.GetString()!;
                        }
                    }
                }
                else if (pluginsElem.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in pluginsElem.EnumerateObject())
                    {
                        result[prop.Name] = prop.Value.GetString()!;
                    }
                }
            }
            else
            {
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    result[prop.Name] = prop.Value.GetString()!;
                }
            }
        }

        return result;
    }

    private static void VerifyPluginIntegrity(string dllPath, Dictionary<string, string> manifest)
    {
        var filename = Path.GetFileName(dllPath);

        if (!manifest.TryGetValue(filename, out var expectedHash) || string.IsNullOrWhiteSpace(expectedHash))
        {
            throw new SecurityException($"Sicherheitsfehler: Plugin '{filename}' ist nicht im Integrity-Manifest verzeichnet.");
        }

        var actualBytes = File.ReadAllBytes(dllPath);
        var actualHash = Convert.ToHexString(SHA256.HashData(actualBytes));
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(expectedHash.Trim()),
                Convert.FromHexString(actualHash)))
        {
            throw new SecurityException($"Sicherheitsfehler: Integritätsprüfung fehlgeschlagen für Plugin '{filename}'. Erwarteter SHA-256: {expectedHash}, Tatsächlich: {actualHash}");
        }
    }
}
