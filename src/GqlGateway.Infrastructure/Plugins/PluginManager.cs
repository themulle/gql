using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using GqlGateway.Application.Plugins;
using Microsoft.Extensions.Logging;

namespace GqlGateway.Infrastructure.Plugins;

public sealed class PluginManager : IPluginManager, IDisposable
{
    private readonly ILogger<PluginManager> _logger;
    private readonly ConcurrentDictionary<string, PluginEntry> _plugins = new(StringComparer.OrdinalIgnoreCase);

    private sealed record PluginEntry(IHttpDataSourcePlugin Plugin, PluginAssemblyLoadContext? Context);

    public PluginManager(ILogger<PluginManager> logger)
    {
        _logger = logger;
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
        if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
        {
            _logger.LogInformation("Plugin directory '{Directory}' does not exist or is empty. Skipping plugin discovery.", directoryPath);
            return 0;
        }

        int loadedCount = 0;
        var dllFiles = Directory.GetFiles(directoryPath, "*.dll", SearchOption.AllDirectories);

        foreach (var dllFile in dllFiles)
        {
            try
            {
                var alc = new PluginAssemblyLoadContext(dllFile);
                var assembly = alc.LoadFromAssemblyPath(Path.GetFullPath(dllFile));

                var pluginTypes = assembly.GetTypes()
                    .Where(t => typeof(IHttpDataSourcePlugin).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface);

                foreach (var type in pluginTypes)
                {
                    try
                    {
                        if (Activator.CreateInstance(type) is IHttpDataSourcePlugin pluginInstance)
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
}
