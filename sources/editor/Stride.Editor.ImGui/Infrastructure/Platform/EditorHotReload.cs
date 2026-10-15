using System.Reflection.Metadata;

[assembly: MetadataUpdateHandler(typeof(Stride.Editor.ImGui.EditorHotReload))]

namespace Stride.Editor.ImGui;

internal static class EditorHotReload
{
    private static int revision;
    internal static int Revision => Volatile.Read(ref revision);
    internal static void ClearCache(Type[]? updatedTypes) { }
    internal static void UpdateApplication(Type[]? updatedTypes)
    {
        var current = Interlocked.Increment(ref revision);
        Console.WriteLine($"Editor hot reload applied: revision {current}, process {Environment.ProcessId}, updated types {updatedTypes?.Length ?? 0}.");
    }
}
