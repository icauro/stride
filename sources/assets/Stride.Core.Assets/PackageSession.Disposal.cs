using System.Reflection;
using Stride.Core.Reflection;
using Stride.Core.Serialization;

namespace Stride.Core.Assets;

public sealed partial class PackageSession
{
    private readonly object disposalGate = new();
    private bool disposalStarted, disposalRunning, disposalCompleted;
    private bool dependenciesReleased, sourceTrackerReleased, artifactRegistrationsReleased, retainedResourcesReleased;
    private List<AssemblyDisposal>? disposalAssemblies;
    private sealed class AssemblyDisposal(Assembly assembly)
    {
        internal readonly Assembly Assembly = assembly;
        internal readonly AssemblyRegistry.Unregistration Registration = AssemblyRegistry.PrepareUnregister(assembly);
        internal bool RegistrationReleased, SerializerReleased, ContainerReleased;
    }
    private void DisposeOwnedSession()
    {
        lock (disposalGate)
        {
            if (disposalCompleted) return;
            if (disposalRunning) throw new InvalidOperationException("The package session cleanup is already running.");
            disposalRunning = true; disposalStarted = true;
            try
            {
                // Package.LoadedAssemblies includes borrowed default-context host
                // assemblies. Their serializer/asset registrations belong to the
                // host and remain live for other sessions. The container records
                // the generations this session actually loaded/adopted, including
                // transitive assemblies absent from package reference lists.
                disposalAssemblies ??= AssemblyContainer.LoadedAssemblies
                    .Select(loaded => loaded.Assembly).Distinct().Reverse().Select(assembly => new AssemblyDisposal(assembly)).ToList();
                if (!dependenciesReleased) { dependencies?.Dispose(); dependenciesReleased = true; }
                if (!sourceTrackerReleased) { sourceTracker?.Dispose(); sourceTrackerReleased = true; }
                foreach (var state in disposalAssemblies)
                {
                    if (!state.RegistrationReleased) { state.Registration.Complete(); state.RegistrationReleased = true; }
                    if (!state.SerializerReleased) { DataSerializerFactory.UnregisterSerializationAssembly(state.Assembly); state.SerializerReleased = true; }
                    if (!state.ContainerReleased) { AssemblyContainer.UnloadAssembly(state.Assembly); state.ContainerReleased = true; }
                }
                if (!artifactRegistrationsReleased) { AssemblyContainer.ReleaseArtifactRegistrations(); artifactRegistrationsReleased = true; }
                if (!retainedResourcesReleased) { ReleaseRetainedLifetimes(); retainedResourcesReleased = true; }
                disposalAssemblies.Clear();
                disposalCompleted = true;
            }
            finally { disposalRunning = false; }
        }
    }
}
