namespace GqlGateway.Application.Extensibility;

using System;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;

using System.Security;
using System.Security.Cryptography;

/// <summary>
/// Collectible AssemblyLoadContext enabling zero-downtime hot-reloading and unloading
/// of customer C# Ingress/Egress middleware plugins (.dll) without restarting the gateway process.
/// </summary>
public sealed class DynamicPluginAssemblyLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;
    private readonly string _pluginPath;

    public string PluginPath => _pluginPath;

    public DynamicPluginAssemblyLoadContext(string pluginPath, string? expectedSha256 = null)
        : base(name: $"PluginALC_{Path.GetFileNameWithoutExtension(pluginPath)}_{Guid.NewGuid():N}", isCollectible: true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginPath);
        if (!File.Exists(pluginPath))
        {
            throw new FileNotFoundException($"Plugin assembly not found at '{pluginPath}'.", pluginPath);
        }

        _pluginPath = Path.GetFullPath(pluginPath);

        if (!string.IsNullOrWhiteSpace(expectedSha256))
        {
            var actualBytes = File.ReadAllBytes(_pluginPath);
            var actualHash = Convert.ToHexString(SHA256.HashData(actualBytes));
            if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(expectedSha256.Trim()),
                    Convert.FromHexString(actualHash)))
            {
                throw new SecurityException($"Integritätsprüfung fehlgeschlagen für Plugin '{Path.GetFileName(_pluginPath)}'. Erwartet: {expectedSha256}, Tatsächlich: {actualHash}");
            }
        }

        _resolver = new AssemblyDependencyResolver(_pluginPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var assemblyPath = _resolver.ResolveAssemblyToPath(assemblyName);
        if (assemblyPath != null)
        {
            return LoadFromAssemblyPath(assemblyPath);
        }

        // Fall back to default context for shared framework assemblies (e.g. GqlGateway.Application interfaces)
        return null;
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var libraryPath = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        if (libraryPath != null)
        {
            return LoadUnmanagedDllFromPath(libraryPath);
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// Loads the target plugin assembly and discovers instances of TInterface.
    /// </summary>
    public IReadOnlyList<TInterface> CreateInstancesOf<TInterface>() where TInterface : class
    {
        var assembly = LoadFromAssemblyPath(_pluginPath);
        var instances = new List<TInterface>();

        foreach (var type in assembly.GetExportedTypes())
        {
            if (typeof(TInterface).IsAssignableFrom(type) && !type.IsAbstract && !type.IsInterface)
            {
                if (Activator.CreateInstance(type) is TInterface instance)
                {
                    instances.Add(instance);
                }
            }
        }

        return instances;
    }
}
