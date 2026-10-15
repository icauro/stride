using System.Reflection;

namespace Stride.Core.Reflection;

public partial class LoadedAssembly
{
    /// <summary>SHA256 of the original immutable image supplied to the byte-array loader.</summary>
    public string? SourceSha256 { get; internal set; }
}

public partial class AssemblyContainer
{
    /// <summary>Finds the actual registered source of a loaded module without resolving by display name.</summary>
    public static LoadedAssembly? FindLoadedSource(Assembly assembly)
        => assemblyToContainers.TryGetValue(assembly, out var source) ? source : null;
}
