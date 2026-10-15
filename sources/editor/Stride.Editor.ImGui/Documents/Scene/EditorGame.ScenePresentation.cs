using Stride.Assets.Entities;
using Stride.Assets.Presentation.SceneEditor;
using Stride.Core.Assets;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private readonly Dictionary<AssetId, bool> sceneLoadedOverrides = [];
    private readonly HashSet<AssetId> lockedScenes = [];
    private long scenePresentationVersion;
    private bool IsSceneLoaded(SceneDocument scene)
    {
        if (sceneLoadedOverrides.TryGetValue(scene.Asset.Id, out var loaded)) return loaded;
        if (ReferenceEquals(scene, engineViewport?.PresentedDocument)) return true;
        var package = workspace?.AssetItems.FirstOrDefault(item => item.Id == scene.Asset.Id)?.Package;
        return package != null && package.UserSettings.GetValue(PackageSceneSettings.SceneSettings)
            .TryGetValue(scene.Asset.Id, out var value) && value.SceneLoaded;
    }
    private bool IsSceneLocked(SceneDocument scene) => lockedScenes.Contains(scene.Asset.Id);
    private void RequestSceneLoading(SceneDocument scene, bool load, bool recursive)
    {
        if (IsAuthoringPipelineUpdating || IsSceneLoaded(scene) == load
            || sessionHistory?.Registry.Find(scene.Asset.Id) is not { IsPublished: true } state
            || !ReferenceEquals(state.Document, scene)) return;
        var targets = PrepareSceneLoadingTargets(scene, load, recursive);
        if (targets.Length == 0) return;
        ValidatePendingPropertyStrings();
        var previous = new Dictionary<AssetId, bool>(sceneLoadedOverrides);
        try
        {
            if (sessionHistory?.IsCapturingOperations == true) ApplySceneLoading(targets, load);
            else
            {
                using var captured = sessionHistory!.CaptureOperationsAtomic(() => ApplySceneLoading(targets, load));
                captured.Publish(sessionHistory, load ? "Load scene" : "Unload scene");
            }
        }
        catch
        {
            sceneLoadedOverrides.Clear(); foreach (var pair in previous) sceneLoadedOverrides.Add(pair.Key, pair.Value);
            scenePresentationVersion++; throw;
        }
    }
    private SceneDocument[] PrepareSceneLoadingTargets(SceneDocument scene, bool load, bool recursive)
    {
        var targets = new List<SceneDocument>(); var visited = new HashSet<AssetId>();
        void Check(SceneDocument current)
        {
            if (!visited.Add(current.Asset.Id)) throw new InvalidOperationException("Circular scene hierarchy");
            if (sessionHistory?.Registry.Find(current.Asset.Id) is not { IsPublished: true } owner || !ReferenceEquals(owner.Document, current))
                throw new InvalidOperationException("The scene loading owner is no longer current.");
        }
        if (load)
        {
            var current = scene;
            while (true)
            {
                Check(current);
                if (!IsSceneLoaded(current)) targets.Add(current);
                if (ReferenceEquals(current, engineViewport?.PresentedDocument) || SceneParentId(current.Asset) is not { } id) break;
                current = ResolveSceneHierarchyDocument(id) ?? throw new InvalidOperationException("The parent scene is not available.");
            }
            targets.Reverse();
        }
        else { Check(scene); if (IsSceneLoaded(scene)) targets.Add(scene); }
        if (recursive || !load)
        {
            var queue = new Queue<AssetId>(scene.Asset.ChildrenIds);
            while (queue.TryDequeue(out var id))
            {
                var child = ResolveSceneHierarchyDocument(id) ?? throw new InvalidOperationException("The child scene is not available.");
                Check(child);
                if (IsSceneLoaded(child) != load) targets.Add(child);
                foreach (var descendant in child.Asset.ChildrenIds) queue.Enqueue(descendant);
            }
        }
        if (!load) targets.Reverse();
        return targets.ToArray();
    }
    private void ApplySceneLoading(SceneDocument[] targets, bool load)
    {
        documents.CommitAll(); engineViewport?.Gizmos?.Cancel(); engineViewport?.Picker?.Cancel("Scene loading changed");
        foreach (var target in targets)
        {
            var package = workspace!.FindAsset(target.Asset.Id).Package;
            if (ReferenceEquals(target, engineViewport?.PresentedDocument) || package.IsReadOnly) sceneLoadedOverrides[target.Asset.Id] = load;
            else sceneLoadedOverrides.Remove(target.Asset.Id);
            if (package.IsReadOnly) continue;
            var values = package.UserSettings.GetValue(PackageSceneSettings.SceneSettings);
            if (values.TryGetValue(target.Asset.Id, out var previous) && previous.SceneLoaded == load) continue;
            var next = (Dictionary<AssetId, SceneSettingsData>)AssetCloner.Clone(values, AssetClonerFlags.None, out _)!;
            if (!next.TryGetValue(target.Asset.Id, out var settings)) next.Add(target.Asset.Id, settings = SceneSettingsData.CreateDefault());
            settings.SceneLoaded = load;
            GetPackageSettingsDocument(package, sessionHistory!).Set(PackageSceneSettings.SceneSettings, next);
        }
        scenePresentationVersion++;
    }
    private void RequestSceneLocking(SceneDocument scene, bool locked, bool recursive)
    {
        if (IsAuthoringPipelineUpdating || !IsSceneLoaded(scene)
            || sessionHistory?.Registry.Find(scene.Asset.Id) is not { IsPublished: true } state
            || !ReferenceEquals(state.Document, scene)) return;
        var visited = new HashSet<AssetId>();
        var targets = new List<SceneDocument>();
        void Collect(SceneDocument current)
        {
            if (!visited.Add(current.Asset.Id)) throw new InvalidOperationException("Circular scene hierarchy");
            if (sessionHistory.Registry.Find(current.Asset.Id) is not { IsPublished: true } owner || !ReferenceEquals(owner.Document, current))
                throw new InvalidOperationException("The scene lock owner is no longer current.");
            targets.Add(current);
            if (recursive) foreach (var id in current.Asset.ChildrenIds) if (ResolveSceneHierarchyDocument(id) is { } child) Collect(child);
        }
        Collect(scene);
        if (!targets.Any(target => IsSceneLocked(target) != locked
            || target.Asset.Hierarchy.Parts.Keys.Any(id => IsHierarchyEntityLocked(target, id) != locked))) return;
        ValidatePendingPropertyStrings();
        documents.CommitAll(); engineViewport?.Gizmos?.Cancel(); engineViewport?.Picker?.Cancel("Scene locking changed");
        foreach (var target in targets)
        {
            if (locked) lockedScenes.Add(target.Asset.Id); else lockedScenes.Remove(target.Asset.Id);
            foreach (var id in target.Asset.Hierarchy.Parts.Keys)
                if (locked) lockedHierarchyEntities.Add((target.Asset.Id, id)); else lockedHierarchyEntities.Remove((target.Asset.Id, id));
        }
        scenePresentationVersion++;
    }
}
