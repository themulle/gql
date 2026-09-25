using System;
using System.Reflection;
using System.Runtime.Loader;

namespace GqlGateway.Infrastructure.Plugins;

public sealed class PluginAssemblyLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;

    public PluginAssemblyLoadContext(string pluginPath) : base(isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(pluginPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // Shared host contracts and runtime assemblies must resolve via Default ALC
        if (IsSharedAssembly(assemblyName.Name))
        {
            return null; // delegates to AssemblyLoadContext.Default
        }

        var assemblyPath = _resolver.ResolveAssemblyToPath(assemblyName);
        if (assemblyPath != null)
        {
            return LoadFromAssemblyPath(assemblyPath);
        }

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

    private static bool IsSharedAssembly(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        return name.StartsWith("GqlGateway.Domain", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("GqlGateway.Application", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("Microsoft.AspNetCore", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("Microsoft.Extensions", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("System.", StringComparison.OrdinalIgnoreCase);
    }
}
