using Stride.Core.Assets;
using Stride.Core.Assets.Quantum;
using Stride.Engine;

namespace Stride.Editor.ImGui;

internal static partial class CoreCapabilityScenarios
{
    private static void RunPrefabSession(Action<EditorAuthoringRegistry, EditorSessionHistory> scenario)
    {
        var registry = new EditorAuthoringRegistry();
        var history = new EditorSessionHistory(registry);
        List<Exception> failures = [];
        try { scenario(registry, history); }
        catch (Exception failure) { failures.Add(failure); }
        // Attempt every teardown phase and retain the original assertion first.
        void Cleanup(Action action)
        { try { action(); } catch (Exception error) { failures.Add(error); } }
        Cleanup(history.Clear);
        Cleanup(history.HierarchyGraphs.DetachAll);
        Cleanup(history.Dispose);
        Cleanup(() =>
        {
            var remaining = registry.RetainedStates.Where(state => state.HistoryReferences != 0 || state.ViewReferences != 0).ToArray();
            if (remaining.Length != 0)
                throw new InvalidOperationException("Prefab teardown retained owners: " + string.Join("; ", remaining.Select(state =>
                    $"{state.CanonicalPath}: history={state.HistoryReferences}, views={state.ViewReferences}")));
        });
        Cleanup(registry.Dispose);
        if (failures.Count != 0) throw new AggregateException("Prefab scenario or teardown failed.", failures);
    }
    private static void PrefabInheritance(string path)
    {
        RunPrefabSession((registry, history) =>
        {
        history.HierarchyGraphs.Resolve = id => registry.Find(id)?.Document as EntityHierarchyDocument;
        var scene = new SceneDocument(path, history);
        var sceneState = registry.Register(scene, scene.Asset.Id);
        using var sceneLease = sceneState.AcquireView();
        var light = scene.Asset.Hierarchy.Parts.Values.Single(part => part.Entity.Name == "Directional light").Entity;
        scene.Selected = light;
        Require(scene.CanExtractPrefab, "Actual canonical scene selection must support prefab extraction");
        var extraction = scene.PreparePrefabExtraction();
        var prefabPath = Path.Combine(Path.GetDirectoryName(path)!, "CapabilityBase.sdprefab");
        AssetFileSerializer.Save(prefabPath, extraction.Prefab, extraction.Metadata);
        var prefab = new PrefabDocument(prefabPath, history);
        var prefabState = registry.Register(prefab, prefab.Asset.Id);
        using var prefabLease = prefabState.AcquireView();
        var baseBytes = File.ReadAllBytes(prefabPath);
        var instance = scene.InsertPrefab(prefab, "CapabilityBase", null, "", -1, false, null);
        var target = instance.Roots.Single();
        scene.Selected = target;
        var instanceLight = target.Get<LightComponent>();
        ComponentField Field() => scene.GetComponentFields(instanceLight).Single(value => value.Name == "Intensity");
        IAssetMemberNode Member() => (IAssetMemberNode)Field().Node;
        var original = instanceLight.Intensity;
        Require(Member().BaseNode is IAssetMemberNode && !Member().IsContentOverridden()
            && scene.Asset.Hierarchy.Parts[target.Id].Base?.BasePartAsset.Id == prefab.Asset.Id,
            "Derived instance must use actual prefab base links without authored override");
        scene.Save();
        scene.EditInheritedScalar(Field(), original + 3);
        Require(Member().IsContentOverridden() && instanceLight.Intensity == original + 3, "Override must change derived graph only");
        scene.Save();
        VerifyPrefabReload(path, prefabPath, target.Id, original + 3, true);
        scene.ResetInheritedScalar(Field());
        Require(!Member().IsContentOverridden() && instanceLight.Intensity == original, "Reset must restore actual base value and clear override flag");
        scene.Undo();
        Require(Member().IsContentOverridden() && instanceLight.Intensity == original + 3 && !scene.IsDirty,
            "Undo reset must restore saved authored override revision");
        scene.Redo(); scene.Save();
        VerifyPrefabReload(path, prefabPath, target.Id, original, false);
        Require(File.ReadAllBytes(prefabPath).SequenceEqual(baseBytes)
            && prefab.Asset.Hierarchy.RootParts.Single().Get<LightComponent>().Intensity == original && !prefab.IsDirty,
            "Derived override/reset must preserve base file bytes and canonical base graph");
        });
    }

    private static void VerifyPrefabReload(string scenePath, string prefabPath, Guid instanceId, float intensity, bool overridden)
    {
        RunPrefabSession((registry, history) =>
        {
        history.HierarchyGraphs.Resolve = id => registry.Find(id)?.Document as EntityHierarchyDocument;
        var prefab = new PrefabDocument(prefabPath, history);
        var prefabState = registry.Register(prefab, prefab.Asset.Id);
        using var prefabLease = prefabState.AcquireView();
        var scene = new SceneDocument(scenePath, history);
        var sceneState = registry.Register(scene, scene.Asset.Id);
        using var sceneLease = sceneState.AcquireView();
        var entity = scene.Asset.Hierarchy.Parts[instanceId].Entity;
        scene.Selected = entity;
        var light = entity.Get<LightComponent>();
        var field = scene.GetComponentFields(light).Single(value => value.Name == "Intensity");
        Require(light.Intensity == intensity && ((IAssetMemberNode)field.Node).IsContentOverridden() == overridden
            && scene.Asset.Hierarchy.Parts[instanceId].Base?.BasePartAsset.Id == prefab.Asset.Id,
            "Independent registered reload must preserve base identity, effective value and actual override metadata");
        });
    }
}
