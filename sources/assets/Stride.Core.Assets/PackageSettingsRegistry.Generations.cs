using System.Reflection;
using Stride.Core.Reflection;

namespace Stride.Core.Assets;

public static partial class PackageSettingsRegistry
{
    static PackageSettingsRegistry()
    {
        AssemblyRegistry.AssemblyUnregistered += (_, args) => SuspendAssemblyProviders(args.Assembly);
    }
    private static readonly AsyncLocal<ProviderRegistrationCapture?> registrationCapture = new();
    private static void CaptureProvider(Provider provider) => registrationCapture.Value?.Record(provider);
    public static ProviderRegistrationCapture CaptureAssemblyProviders(string assemblyName)
        => new(assemblyName, registrationCapture.Value);
    public static ProviderGeneration SuspendAssemblyProviders(Assembly assembly)
    {
        lock (providers)
        {
            var generation = new ProviderGeneration(assembly, providers.Values.Where(provider => ReferenceEquals(provider.Owner, assembly)).ToArray());
            generation.Suspend(); return generation;
        }
    }
    public sealed class ProviderGeneration
    {
        private readonly Assembly owner;
        private Provider[] entries;
        private bool suspended;
        internal ProviderGeneration(Assembly owner, Provider[] entries) { this.owner = owner; this.entries = entries; }
        public void Suspend()
        {
            lock (providers)
            {
                if (suspended) return;
                entries = entries.Concat(providers.Values.Where(provider => ReferenceEquals(provider.Owner, owner)))
                    .GroupBy(provider => provider.Identity, ReferenceEqualityComparer.Instance).Select(group => group.Last()).ToArray();
                if (entries.Any(entry => !providers.TryGetValue(entry.Identity, out var current) || !ReferenceEquals(current, entry)))
                    throw new InvalidOperationException("The package setting provider generation changed.");
                foreach (var entry in entries) providers.Remove(entry.Identity);
                suspended = true; version++;
            }
        }
        public void Restore()
        {
            lock (providers)
            {
                if (!suspended) return;
                if (entries.Any(entry => providers.TryGetValue(entry.Identity, out var current) && !ReferenceEquals(current, entry)))
                    throw new InvalidOperationException("A different package setting provider owns this registration.");
                foreach (var entry in entries) providers[entry.Identity] = entry;
                suspended = false; version++;
            }
        }
    }
    public sealed class ProviderRegistrationCapture : IDisposable
    {
        private readonly string name;
        private readonly ProviderRegistrationCapture? previous;
        private readonly List<Provider> entries = new();
        private bool completed;
        internal ProviderRegistrationCapture(string name, ProviderRegistrationCapture? previous)
        { this.name = name; this.previous = previous; registrationCapture.Value = this; }
        internal void Record(Provider provider)
        { if (string.Equals(provider.Owner.GetName().Name, name, StringComparison.OrdinalIgnoreCase)) entries.Add(provider); previous?.Record(provider); }
        public ProviderGeneration Complete(Assembly owner)
        {
            if (!string.Equals(owner.GetName().Name, name, StringComparison.OrdinalIgnoreCase) || entries.Any(entry => !ReferenceEquals(entry.Owner, owner)))
                throw new InvalidOperationException("The providers belong to a different assembly generation.");
            completed = true; return new(owner, entries.ToArray());
        }
        public void Dispose()
        {
            registrationCapture.Value = previous;
            if (!completed)
                lock (providers)
                {
                    foreach (var entry in entries) if (providers.TryGetValue(entry.Identity, out var current) && ReferenceEquals(current, entry)) providers.Remove(entry.Identity);
                    if (entries.Count > 0) version++;
                }
        }
    }
}
