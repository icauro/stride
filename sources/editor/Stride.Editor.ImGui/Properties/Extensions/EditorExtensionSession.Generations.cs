using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorExtensionSession
{
    private sealed partial class Provider
    {
        internal AssemblyLoadContext? LoadContext;
        internal EditorExtensionGenerations.State? Generation;
        internal Action<AssemblyLoadContext>? UnloadingHandler;
    }
    private static bool IsProviderGenerationUnloading(Type type)
        => EditorExtensionGenerations.IsUnloading(type.Assembly);
    private void ObserveProviderGeneration(Provider provider)
    {
        var loadContext = AssemblyLoadContext.GetLoadContext(provider.Type.Assembly);
        if (loadContext?.IsCollectible != true) return;
        var generation = EditorExtensionGenerations.Observe(provider.Type.Assembly)!;
        provider.LoadContext = loadContext; provider.Generation = generation;
        provider.UnloadingHandler = _ =>
        {
            Interlocked.Exchange(ref generation.Unloading, 1);
            Interlocked.Exchange(ref assemblyChanged, 1);
        };
        loadContext.Unloading += provider.UnloadingHandler;
    }
    private void ApplyUnloadingProviderGenerations()
    {
        Check();
        foreach (var provider in providers)
        {
            if (provider.Retiring || provider.Generation == null || Volatile.Read(ref provider.Generation.Unloading) == 0) continue;
            provider.Available = false; provider.Retiring = true; Version++;
        }
    }
    private static void ReleaseProviderGenerationObserver(Provider provider)
    {
        if (provider.LoadContext != null && provider.UnloadingHandler != null)
            provider.LoadContext.Unloading -= provider.UnloadingHandler;
        provider.UnloadingHandler = null; provider.Generation = null; provider.LoadContext = null;
    }
}
