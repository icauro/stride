using System.Reflection;

namespace Stride.Core.Reflection;

public static partial class AssemblyRegistry
{
    /// <summary>Prepares retryable cleanup of this exact assembly's registration and notifications.</summary>
    public static Unregistration PrepareUnregister(Assembly assembly) => new(assembly);
    private static EventHandler<AssemblyRegisteredEventArgs>[] CaptureUnregistrationHandlers()
        => AssemblyUnregistered?.GetInvocationList().Cast<EventHandler<AssemblyRegisteredEventArgs>>().ToArray() ?? [];

    public sealed class Unregistration
    {
        private readonly Assembly assembly;
        private readonly object gate = new();
        private bool removed;
        private EventHandler<AssemblyRegisteredEventArgs>[] handlers = [];
        private AssemblyRegisteredEventArgs? arguments;
        private int nextHandler;
        internal Unregistration(Assembly assembly) => this.assembly = assembly ?? throw new ArgumentNullException(nameof(assembly));

        /// <summary>Completes remaining actual notifications without repeating successful handlers or removing a fresh registration.</summary>
        public void Complete()
        {
            lock (gate)
            {
                lock (Lock)
                {
                    if (!removed)
                    {
                        if (MapAssemblyToCategories.TryGetValue(assembly, out var categories))
                        {
                            arguments = new AssemblyRegisteredEventArgs(assembly, categories);
                            handlers = CaptureUnregistrationHandlers();
                            MapAssemblyToCategories.Remove(assembly);
                            var name = assembly.GetName().Name;
                            if (name != null && AssemblyNameToAssembly.TryGetValue(name, out var current) && ReferenceEquals(current, assembly))
                            {
                                AssemblyNameToAssembly.Remove(name);
                                var retained = MapAssemblyToCategories.Keys.LastOrDefault(candidate => candidate.GetName().Name == name);
                                if (retained != null) AssemblyNameToAssembly[name] = retained;
                            }
                            foreach (var category in categories)
                                if (MapCategoryToAssemblies.TryGetValue(category, out var registered)) registered.Remove(assembly);
                        }
                        removed = true;
                    }
                    else if (nextHandler < handlers.Length && MapAssemblyToCategories.ContainsKey(assembly))
                        throw new InvalidOperationException("The assembly was registered again while its prior cleanup was incomplete.");
                }
                while (nextHandler < handlers.Length)
                {
                    handlers[nextHandler](null, arguments!);
                    nextHandler++;
                }
                handlers = []; arguments = null;
            }
        }
    }
}
