using Stride.Core.Reflection;

namespace Stride.Editor.ImGui;

internal static class CompositorTypeCatalog
{
    private static int version;
    static CompositorTypeCatalog()
    {
        AssemblyRegistry.AssemblyRegistered += (_, _) => Interlocked.Increment(ref version);
        AssemblyRegistry.AssemblyUnregistered += (_, _) => Interlocked.Increment(ref version);
    }
    public static int Version => Volatile.Read(ref version);
}
