using Stride.Assets;
using Stride.Core.Assets;
using Stride.Graphics;

namespace Stride.Editor.ImGui;

internal sealed partial class EngineViewport
{
    internal AssetId? LoadedCompositorId { get; private set; }
    private IDisposable? sceneSettingsLease;
    private EditorAuthoringState? sceneSettingsOwner;
    private GameSettingsAsset? sceneGameSettings, sceneNativeGameSettings, sceneDefaultGameSettings;
    private ProjectWorkspace? sceneSettingsWorkspace;
    private SolutionProject? sceneSettingsProject;
    private IReadOnlyList<AssetItem>? sceneSettingsInventory;
    private long sceneSettingsRevision = -1;
    private EditorAuthoringState? observedSceneSettingsOwner;
    private GameSettingsAsset? observedSceneSettings;
    private SolutionProject? observedSceneSettingsProject;
    private long observedSceneSettingsRevision = -1, sceneSettingsVersion;
    private readonly List<SceneViewportResources> pendingSceneSettingsRetirements = [];

    internal long SceneSettingsVersion
    {
        get
        {
            if (sceneSettingsWorkspace == null) return sceneSettingsVersion;
            var settings = ResolveSceneGameSettings(out var owner);
            var revision = (owner?.Document as IAssetTagDocument)?.EditRevision ?? -1;
            var project = sceneSettingsWorkspace.Session.CurrentProject;
            if (!ReferenceEquals(settings, observedSceneSettings) || !ReferenceEquals(owner, observedSceneSettingsOwner)
                || revision != observedSceneSettingsRevision || !ReferenceEquals(project, observedSceneSettingsProject))
            {
                observedSceneSettings = settings; observedSceneSettingsOwner = owner;
                observedSceneSettingsRevision = revision; observedSceneSettingsProject = project; sceneSettingsVersion++;
            }
            return sceneSettingsVersion;
        }
    }

    private GameSettingsAsset ResolveSceneGameSettings(out EditorAuthoringState? owner)
    {
        var workspace = sceneSettingsWorkspace!;
        if (sceneNativeGameSettings == null || !ReferenceEquals(sceneSettingsProject, workspace.Session.CurrentProject)
            || !ReferenceEquals(sceneSettingsInventory, workspace.AssetItems))
        {
            sceneNativeGameSettings = EditorSceneSettings.ResolveNativeGameSettings(workspace, ref sceneDefaultGameSettings);
            sceneSettingsProject = workspace.Session.CurrentProject;
            sceneSettingsInventory = workspace.AssetItems;
        }
        return EditorSceneSettings.ResolveCanonicalGameSettings(sceneNativeGameSettings, PresentedDocument?.SessionHistory, out owner);
    }

    private GameSettingsAsset InitializeSceneSettings(ProjectWorkspace workspace)
    {
        sceneSettingsWorkspace = workspace;
        var settings = ResolveSceneGameSettings(out var owner);
        sceneSettingsLease = owner?.AcquireView();
        sceneSettingsOwner = owner;
        sceneGameSettings = settings;
        sceneSettingsRevision = (owner?.Document as IAssetTagDocument)?.EditRevision ?? -1;
        return settings;
    }

    private void SynchronizeSceneSettings()
    {
        while (pendingSceneSettingsRetirements.Count > 0)
        { pendingSceneSettingsRetirements[0].Retire(); pendingSceneSettingsRetirements.RemoveAt(0); }
        if (sceneSettingsWorkspace == null || loadedScene == null) return;
        var next = ResolveSceneGameSettings(out var owner);
        var revision = (owner?.Document as IAssetTagDocument)?.EditRevision ?? -1;
        if (ReferenceEquals(next, sceneGameSettings) && ReferenceEquals(owner, sceneSettingsOwner) && revision == sceneSettingsRevision) return;
        var ownerChanged = !ReferenceEquals(owner, sceneSettingsOwner);
        var nextLease = ownerChanged ? owner?.AcquireView() : null;
        try { ApplyNavigationSettings(next); }
        catch (Exception failure)
        {
            if (nextLease != null)
                try { RetireSceneSettingsLease(nextLease); }
                catch (Exception recovery) { throw new AggregateException("Scene settings update and lease retirement failed.", failure, recovery); }
            throw;
        }
        var previousLease = ownerChanged ? sceneSettingsLease : null;
        if (ownerChanged) sceneSettingsLease = nextLease;
        sceneSettingsOwner = owner; sceneGameSettings = next; sceneSettingsRevision = revision;
        if (previousLease != null) RetireSceneSettingsLease(previousLease);
    }

    private float SceneGameAspect(float viewportAspect)
    {
        var rendering = sceneGameSettings?.TryGet<RenderingSettings>();
        return rendering == null ? viewportAspect : (float)rendering.DefaultBackBufferWidth / rendering.DefaultBackBufferHeight;
    }

    private void RetireSceneSettingsLease(IDisposable lease)
    {
        var retired = CreateSceneResources(SourceContent, null);
        retired.RetainLease(lease);
        pendingSceneSettingsRetirements.Add(retired);
        retired.Retire();
        pendingSceneSettingsRetirements.Remove(retired);
    }

    private void RetireSceneSettings(SceneViewportResources retired)
    {
        while (pendingSceneSettingsRetirements.Count > 0)
        { retired.Retain(pendingSceneSettingsRetirements[0]); pendingSceneSettingsRetirements.RemoveAt(0); }
        if (sceneSettingsLease != null) { retired.RetainLease(sceneSettingsLease); sceneSettingsLease = null; }
        sceneSettingsOwner = null; sceneGameSettings = sceneNativeGameSettings = sceneDefaultGameSettings = null;
        sceneSettingsWorkspace = null; sceneSettingsProject = null; sceneSettingsInventory = null; sceneSettingsRevision = -1;
        observedSceneSettingsOwner = null; observedSceneSettings = null; observedSceneSettingsProject = null; observedSceneSettingsRevision = -1;
        LoadedCompositorId = null;
    }
}
