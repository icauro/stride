using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

namespace Stride.Editor.ImGui;

// Weak tombstones outlive individual provider scopes without pinning collectible code.
internal static class EditorExtensionGenerations
{
    internal sealed class State { internal int Unloading; }
    private static readonly object Gate = new();
    private static readonly ConditionalWeakTable<AssemblyLoadContext, State> States = new();
    internal static State? Observe(Assembly assembly)
    {
        var loadContext = AssemblyLoadContext.GetLoadContext(assembly);
        if (loadContext?.IsCollectible != true) return null;
        lock (Gate)
        {
            if (States.TryGetValue(loadContext, out var existing)) return existing;
            var state = new State();
            loadContext.Unloading += _ => Interlocked.Exchange(ref state.Unloading, 1);
            States.Add(loadContext, state);
            return state;
        }
    }
    internal static bool IsUnloading(Assembly assembly)
        => Observe(assembly) is { } state && Volatile.Read(ref state.Unloading) != 0;
}
