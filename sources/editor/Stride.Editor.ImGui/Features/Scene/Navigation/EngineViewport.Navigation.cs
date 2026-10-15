using Stride.Assets;
using Stride.Navigation.Assets;
using Stride.Core.Assets;
using Stride.Core.Mathematics;
using Stride.Core.Serialization;
using Stride.Engine;
using Stride.Navigation;

namespace Stride.Editor.ImGui;

internal sealed partial class EngineViewport
{
    private sealed record NavigationAssetVisual(AssetId SceneId, NavigationMesh Mesh, SceneNavigationVisual Visual);
    private readonly List<NavigationAssetVisual> navigationAssets = [];
    private SceneViewportResources? navigationAssetResources;
    private NavigationSettings? navigationSettings;
    private IReadOnlyList<AssetItem>? navigationAssetInventory;
    private readonly Dictionary<AssetId, long> navigationRevisions = [];
    private ProjectWorkspace? navigationWorkspace;
    private DynamicNavigationMeshSystem? dynamicNavigation;
    private SceneNavigationVisual? dynamicNavigationVisual;
    private SceneNavigationVisual? navigationControlVisual;
    private NavigationMesh? displayedDynamicMesh;
    private readonly HashSet<Guid> visibleNavigationGroups = [];
    private long navigationPresentationVersion = -1;
    internal IReadOnlyCollection<SceneNavigationVisual.Group> NavigationGroups => navigationControlVisual?.Groups ?? Array.Empty<SceneNavigationVisual.Group>();

    internal void InitializeNavigation(ProjectWorkspace workspace, GameSettingsAsset settings)
    {
        ClearNavigation(); navigationWorkspace = workspace;
        navigationSettings = AssetCloner.Clone(settings.GetOrDefault<NavigationSettings>());
        if (loadedScene != null)
        { navigationControlVisual = new(game, loadedScene); navigationControlVisual.Configure(navigationSettings.Groups, visibleNavigationGroups); }
    }
    private void RefreshNavigationAssets()
    {
        if (navigationWorkspace == null || content == null || loadedScene == null || navigationSettings == null || SourceContent == null) return;
        loadedScene.UpdateWorldMatrix();
        var inventory = navigationWorkspace.AssetItems;
        var next = new List<NavigationAssetVisual>();
        var revisions = new Dictionary<AssetId, long>();
        var resources = CreateSceneResources(SourceContent, SourceContent.AcquireLease());
        try
        {
        var cache = SourceContent.CreateObjectCache(); resources.RetainCache(cache);
        foreach (var item in inventory)
        {
            if (item.Asset is not NavigationMeshAsset asset) continue;
            if (PresentedDocument?.SessionHistory?.Registry.Find(item.Id)?.Document is IAssetTagDocument canonical
                && canonical.AuthoredAsset is NavigationMeshAsset authored)
            { asset = authored; revisions[item.Id] = canonical.EditRevision; }
            else revisions[item.Id] = -1;
            var source = AttachedReferenceManager.GetAttachedReference(asset.Scene);
            if (source == null || PresentedDocument?.Asset.Id != source.Id && !childScenes.ContainsKey(source.Id)) continue;
            var url = item.Location.ToString();
            if (!cache.Exists(url)) continue;
            var mesh = cache.Load<NavigationMesh>(url);
            var visual = new SceneNavigationVisual(game, loadedScene); resources.Retain(visual);
            next.Add(new(source.Id, mesh, visual));
            visual.Configure(navigationSettings.Groups, visibleNavigationGroups);
            visual.Replace(mesh, null, SceneOffset(source.Id));
        }
        foreach (var entry in navigationAssets) entry.Visual.Detach();
        navigationAssetResources?.Retire();
        }
        catch (Exception failure)
        {
            try { foreach (var entry in navigationAssets) entry.Visual.Attach(); foreach (var entry in next) entry.Visual.Detach(); }
            catch (Exception recovery) { throw new AggregateException("Navigation asset replacement and restoration failed.", failure, recovery); }
            finally { resources.Retire(); }
            throw;
        }
        navigationAssetResources = resources; navigationAssets.Clear(); navigationAssets.AddRange(next);
        navigationAssetInventory = inventory; navigationRevisions.Clear();
        foreach (var revision in revisions) navigationRevisions.Add(revision.Key, revision.Value);
    }
    internal void SetNavigationGroupVisible(Guid id, bool visible)
    {
        if (visible) visibleNavigationGroups.Add(id); else visibleNavigationGroups.Remove(id);
        navigationControlVisual?.SetVisible(id, visible);
        foreach (var entry in navigationAssets) entry.Visual.SetVisible(id, visible && dynamicNavigation?.Enabled != true);
        dynamicNavigationVisual?.SetVisible(id, visible);
    }
    internal Guid[] CaptureVisibleNavigationGroups() => visibleNavigationGroups.Order().ToArray();
    private void SuppressNavigationForBake(bool suppressed)
    {
        foreach (var entry in navigationAssets) entry.Visual.SuppressRendering(suppressed || dynamicNavigation?.Enabled == true);
        dynamicNavigationVisual?.SuppressRendering(suppressed);
    }
    internal void RestoreVisibleNavigationGroups(IEnumerable<Guid> ids)
    {
        visibleNavigationGroups.Clear(); foreach (var id in ids) visibleNavigationGroups.Add(id);
        foreach (var group in NavigationGroups) SetNavigationGroupVisible(group.Id, visibleNavigationGroups.Contains(group.Id));
    }
    internal void SynchronizeNavigation()
    {
        if (navigationWorkspace == null || loadedScene == null || navigationSettings == null) return;
        var presentationVersion = ScenePresentationVersion?.Invoke() ?? 0;
        var referencesChanged = !ReferenceEquals(navigationAssetInventory, navigationWorkspace.AssetItems);
        if (!referencesChanged)
            foreach (var entry in navigationRevisions)
                if ((PresentedDocument?.SessionHistory?.Registry.Find(entry.Key)?.Document as IAssetTagDocument)?.EditRevision is { } revision && revision != entry.Value)
                { referencesChanged = true; break; }
        if (navigationPresentationVersion != presentationVersion || referencesChanged)
        { RefreshNavigationAssets(); navigationPresentationVersion = presentationVersion; }
        var system = sceneAuthority?.GameSystems.OfType<DynamicNavigationMeshSystem>().FirstOrDefault();
        var systemChanged = !ReferenceEquals(system, dynamicNavigation);
        if (systemChanged) system?.InitializeSettingsFromNavigationSettings(navigationSettings);
        var dynamicMesh = system?.Enabled == true ? system.CurrentNavigationMesh : null;
        if (systemChanged || !ReferenceEquals(displayedDynamicMesh, dynamicMesh))
        {
            SceneNavigationVisual? next = null;
            try
            {
                if (dynamicMesh != null)
                {
                    next = new(game, loadedScene);
                    next.Configure(navigationSettings.Groups, visibleNavigationGroups);
                    next.Replace(dynamicMesh, systemChanged ? null : displayedDynamicMesh, -loadedScene.WorldMatrix.TranslationVector);
                }
                dynamicNavigationVisual?.Detach(); dynamicNavigationVisual?.Dispose();
            }
            catch { next?.Detach(); next?.Dispose(); throw; }
            dynamicNavigationVisual = next; dynamicNavigation = system; displayedDynamicMesh = dynamicMesh;
        }
        foreach (var entry in navigationAssets)
        {
            foreach (var group in entry.Visual.Groups) entry.Visual.SetVisible(group.Id, dynamicMesh == null && visibleNavigationGroups.Contains(group.Id));
            entry.Visual.Update((float)game.UpdateTime.Elapsed.TotalSeconds);
        }
        dynamicNavigationVisual?.Update((float)game.UpdateTime.Elapsed.TotalSeconds);
    }
    private Vector3 SceneOffset(AssetId id) => (PresentedDocument?.Asset.Id == id
        ? ResolveSceneWorldOffset?.Invoke(PresentedDocument) ?? loadedScene?.Offset ?? Vector3.Zero
        : childScenes.TryGetValue(id, out var child) ? ResolveSceneWorldOffset?.Invoke(child.Document) ?? child.Scene.Offset : Vector3.Zero)
        - (loadedScene?.WorldMatrix.TranslationVector ?? Vector3.Zero);
    internal void ClearNavigation(SceneViewportResources? retirement = null)
    {
        try { foreach (var entry in navigationAssets) entry.Visual.Detach(); }
        catch (Exception failure)
        {
            try { foreach (var entry in navigationAssets) entry.Visual.Attach(); }
            catch (Exception recovery) { throw new AggregateException("Navigation detachment and restoration failed.", failure, recovery); }
            throw;
        }
        if (navigationAssetResources != null)
        {
            if (retirement != null) retirement.Retain(navigationAssetResources); else navigationAssetResources.Retire();
            navigationAssetResources = null;
        }
        navigationAssets.Clear();
        if (dynamicNavigationVisual != null)
        { if (retirement != null) retirement.Retain(dynamicNavigationVisual); else dynamicNavigationVisual.Dispose(); dynamicNavigationVisual = null; }
        if (navigationControlVisual != null)
        { if (retirement != null) retirement.Retain(navigationControlVisual); else navigationControlVisual.Dispose(); navigationControlVisual = null; }
        displayedDynamicMesh = null; dynamicNavigation = null; navigationWorkspace = null; navigationSettings = null;
        navigationAssetInventory = null; navigationRevisions.Clear();
        navigationPresentationVersion = -1;
    }
}
