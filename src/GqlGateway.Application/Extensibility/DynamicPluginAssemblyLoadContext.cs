namespace GqlGateway.Application.Extensibility;

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;

using System.Security;
using System.Security.Cryptography;

/// <summary>
/// Collectible AssemblyLoadContext enabling zero-downtime hot-reloading and unloading
/// of customer C# Ingress/Egress middleware plugins (.dll) without restarting the gateway process.
/// </summary>
/// <remarks>
/// SEC M-27: the SHA-256 of the plugin is mandatory; the plugin is loaded from exactly the verified bytes.
/// Managed dependencies and native libraries resolved next to the plugin must be listed in
/// <c>trustedDependencyHashes</c> (file name -> SHA-256), otherwise loading is refused.
/// </remarks>
public sealed class DynamicPluginAssemblyLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;
    private readonly string _pluginPath;
    private readonly byte[] _verifiedPluginBytes;
    private readonly Dictionary<string, string> _trustedDependencyHashes;
    private readonly object _loadLock = new();
    private Assembly? _pluginAssembly;

    public string PluginPath => _pluginPath;

    public DynamicPluginAssemblyLoadContext(
        string pluginPath,
        string expectedSha256,
        IReadOnlyDictionary<string, string>? trustedDependencyHashes = null)
        : base(name: $"PluginALC_{Path.GetFileNameWithoutExtension(pluginPath)}_{Guid.NewGuid():N}", isCollectible: true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginPath);
        if (string.IsNullOrWhiteSpace(expectedSha256))
        {
            throw new SecurityException($"Integritätsprüfung fehlgeschlagen für Plugin '{Path.GetFileName(pluginPath)}': kein erwarteter SHA-256-Hash angegeben.");
        }

        if (!File.Exists(pluginPath))
        {
            throw new FileNotFoundException($"Plugin assembly not found at '{pluginPath}'.", pluginPath);
        }

        _pluginPath = Path.GetFullPath(pluginPath);
        _verifiedPluginBytes = ReadVerified(_pluginPath, expectedSha256);

        _trustedDependencyHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (trustedDependencyHashes != null)
        {
            foreach (var (file, hash) in trustedDependencyHashes)
            {
                if (!string.IsNullOrWhiteSpace(file) && !string.IsNullOrWhiteSpace(hash))
                {
                    _trustedDependencyHashes[Path.GetFileName(file)] = hash;
                }
            }
        }

        _resolver = new AssemblyDependencyResolver(_pluginPath);
    }

    private static byte[] ReadVerified(string path, string expectedSha256)
    {
        var actualBytes = File.ReadAllBytes(path);
        var actualHash = Convert.ToHexString(SHA256.HashData(actualBytes));
        byte[] expected;
        try
        {
            expected = Convert.FromHexString(expectedSha256.Trim());
        }
        catch (FormatException)
        {
            expected = Array.Empty<byte>();
        }

        if (expected.Length != SHA256.HashSizeInBytes ||
            !CryptographicOperations.FixedTimeEquals(expected, Convert.FromHexString(actualHash)))
        {
            throw new SecurityException($"Integritätsprüfung fehlgeschlagen für Plugin '{Path.GetFileName(path)}'. Erwartet: {expectedSha256}, Tatsächlich: {actualHash}");
        }

        return actualBytes;
    }

    private byte[] ReadTrustedDependency(string path)
    {
        var fileName = Path.GetFileName(path);
        if (!_trustedDependencyHashes.TryGetValue(fileName, out var expected))
        {
            throw new SecurityException($"Integritätsprüfung fehlgeschlagen: Plugin-Abhängigkeit '{fileName}' ist nicht als vertrauenswürdig konfiguriert.");
        }

        return ReadVerified(path, expected);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var assemblyPath = _resolver.ResolveAssemblyToPath(assemblyName);
        if (assemblyPath != null)
        {
            var bytes = ReadTrustedDependency(assemblyPath);
            using var stream = new MemoryStream(bytes, writable: false);
            return LoadFromStream(stream);
        }

        // Fall back to default context for shared framework assemblies (e.g. GqlGateway.Application interfaces)
        return null;
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var libraryPath = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        if (libraryPath != null)
        {
            // Native code can only be loaded by path; verify immediately before loading.
            ReadTrustedDependency(libraryPath);
            return LoadUnmanagedDllFromPath(libraryPath);
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// Loads the target plugin assembly (from the verified bytes) and discovers instances of TInterface.
    /// </summary>
    public IReadOnlyList<TInterface> CreateInstancesOf<TInterface>() where TInterface : class
    {
        Assembly assembly;
        lock (_loadLock)
        {
            if (_pluginAssembly == null)
            {
                using var stream = new MemoryStream(_verifiedPluginBytes, writable: false);
                _pluginAssembly = LoadFromStream(stream);
            }
            assembly = _pluginAssembly;
        }

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
