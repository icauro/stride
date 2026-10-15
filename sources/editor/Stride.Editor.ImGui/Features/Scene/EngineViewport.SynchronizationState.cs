namespace Stride.Editor.ImGui;

internal readonly record struct SceneRuntimeSynchronizationState(long StructureAndContent, long Textures, long InstalledContent);

internal sealed partial class EngineViewport
{
    private long installedRuntimeContentVersion;
    private bool runtimeSourceFailed;
    internal bool RuntimeSourceAvailable => !runtimeSourceFailed;
    internal void SetRuntimeSourceFailed(bool failed)
    {
        if (runtimeSourceFailed == failed) return;
        runtimeSourceFailed = failed; selectionRuntimeVersion++;
        if (failed) Picker?.Cancel("Scene source synchronization failed");
    }
    internal void NotifyRuntimeContentInstalled() => installedRuntimeContentVersion++;
    internal SceneRuntimeSynchronizationState RuntimeContentSynchronizationVersion => new(selectionRuntimeVersion, runtimeTextureGeneration, installedRuntimeContentVersion);
    private readonly Dictionary<EntityHierarchyDocument, long> observedFailureSelectionVersions = [];
    private long selectionSourceVersion;
    internal long SelectionSourceVersion
    {
        get
        {
            var changed = observedFailureSelectionVersions.Count != presentedHierarchyDocuments.Count;
            foreach (var owner in presentedHierarchyDocuments)
            {
                if (observedFailureSelectionVersions.TryGetValue(owner, out var previous) && previous == owner.SelectionVersion) continue;
                observedFailureSelectionVersions[owner] = owner.SelectionVersion; changed = true;
            }
            if (!changed) return selectionSourceVersion;
            foreach (var old in observedFailureSelectionVersions.Keys.Where(owner => !presentedHierarchyDocuments.Contains(owner)).ToArray())
                observedFailureSelectionVersions.Remove(old);
            return ++selectionSourceVersion;
        }
    }
}
