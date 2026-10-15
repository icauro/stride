namespace Stride.Core.Reflection;

/// <summary>Owns physical artifacts required by actual assembly resolver registrations.</summary>
public interface IAssemblyArtifactLifetime
{
    IDisposable Acquire();
    bool Owns(string path);
}

public partial class LoadedAssembly
{
    private IDisposable? registrationArtifact;
    public IAssemblyArtifactLifetime? ArtifactLifetime { get; }
    public IDisposable? AcquireArtifactLifetime() => ArtifactLifetime?.Acquire();
    internal void AcquireRegistrationArtifact() => registrationArtifact ??= AcquireArtifactLifetime();
    internal void ReleaseRegistrationArtifact()
    {
        registrationArtifact?.Dispose();
        registrationArtifact = null;
    }
}

public partial class AssemblyContainer
{
    [ThreadStatic] private static IAssemblyArtifactLifetime? currentArtifactLifetime;

    /// <summary>Releases remaining resolver registrations after the owning session has unregistered its package assemblies.</summary>
    public void ReleaseArtifactRegistrations()
    {
        lock (loadedAssemblies)
            foreach (var entry in loadedAssemblies.Where(entry => entry.ArtifactLifetime != null).ToArray())
            {
                entry.ReleaseRegistrationArtifact();
                loadedAssemblies.Remove(entry);
                if (loadedAssembliesByName.TryGetValue(entry.Path, out var mapped) && ReferenceEquals(mapped, entry))
                    loadedAssembliesByName.Remove(entry.Path);
                if (assemblyToContainers.TryGetValue(entry.Assembly, out var resolver) && ReferenceEquals(resolver, entry))
                    assemblyToContainers.Remove(entry.Assembly);
            }
    }
}
