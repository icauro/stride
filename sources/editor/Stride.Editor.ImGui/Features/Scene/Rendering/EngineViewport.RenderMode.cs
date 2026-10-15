using Stride.Assets.Materials;
using Stride.Assets.Presentation.AssetEditors.EntityHierarchyEditor;
using Stride.Assets.Presentation.AssetEditors.GameEditor;
using Stride.Assets.Presentation.SceneEditor;
using Stride.Engine;
using Stride.Core.Reflection;
using Stride.Rendering;
using Stride.Rendering.Compositing;

namespace Stride.Editor.ImGui;

internal sealed partial class EngineViewport
{
    private readonly List<EditorRenderMode> sceneRenderModes = [];
    private readonly List<MaterialFilterRenderFeature> materialStreamFilters = [];
    private EditorRenderMode sceneRenderMode = EditorRenderMode.DefaultEditor;
    private long sceneRenderModeRegistryVersion = -1;
    private int sceneRenderModesChanged = 1;
    private bool sceneRenderModeRegistrySubscribed;
    internal IReadOnlyList<EditorRenderMode> AvailableSceneRenderModes => sceneRenderModes;
    internal EditorRenderMode SceneRenderMode => sceneRenderMode;
    internal bool IsGamePreview => sceneRenderMode.PreviewGameGraphicsCompositor;
    internal string ActiveMaterialStream => sceneRenderMode.StreamDescriptor?.Name ?? "";
    internal RenderMode NativeRenderMode => sceneRenderMode.Mode;

    private void InitializeSceneRenderModes(EntityHierarchyDocument owner)
    {
        if (!sceneRenderModeRegistrySubscribed)
        {
            AssemblyRegistry.AssemblyRegistered += OnSceneRenderModeAssemblyChanged;
            AssemblyRegistry.AssemblyUnregistered += OnSceneRenderModeAssemblyChanged;
            sceneRenderModeRegistrySubscribed = true;
        }
        foreach (var mesh in loadedCompositor!.RenderFeatures.OfType<MeshRenderFeature>())
        {
            var filter = new MaterialFilterRenderFeature
            { IsContentObject = renderObject => renderObject.Source is EntityComponent component && component.Entity is { Scene: not null } entity && RuntimeOwner(entity) != null };
            materialStreamFilters.Add(filter); mesh.RenderFeatures.Add(filter);
        }
        RefreshSceneRenderModes(owner);
    }
    private void OnSceneRenderModeAssemblyChanged(object? sender, AssemblyRegisteredEventArgs args) => Interlocked.Exchange(ref sceneRenderModesChanged, 1);
    private void RefreshSceneRenderModes(EntityHierarchyDocument owner)
    {
        var version = owner.SessionHistory?.Extensions.Version ?? 0;
        if (Interlocked.Exchange(ref sceneRenderModesChanged, 0) == 0 && sceneRenderModeRegistryVersion == version) return;
        var activeName = ActiveMaterialStream; var preview = IsGamePreview;
        var next = MaterialStreamFactory.GetAvailableStreams().Select(stream => new EditorRenderMode(stream)).ToArray();
        sceneRenderModes.Clear(); sceneRenderModes.Add(EditorRenderMode.DefaultEditor); sceneRenderModes.Add(EditorRenderMode.DefaultGamePreview);
        sceneRenderModes.AddRange(next); sceneRenderModeRegistryVersion = version;
        SetSceneRenderMode(preview ? EditorRenderMode.DefaultGamePreview
            : sceneRenderModes.FirstOrDefault(mode => mode.StreamDescriptor?.Name == activeName) ?? EditorRenderMode.DefaultEditor);
    }
    internal void RestoreSceneRenderMode(string? activeStream)
        => SetSceneRenderMode(string.IsNullOrEmpty(activeStream) ? EditorRenderMode.DefaultEditor
            : sceneRenderModes.FirstOrDefault(mode => mode.StreamDescriptor?.Name == activeStream) ?? EditorRenderMode.DefaultEditor);
    internal void SetSceneRenderMode(EditorRenderMode mode)
    {
        if (!sceneRenderModes.Contains(mode)) throw new InvalidOperationException("The requested native render mode is unavailable.");
        if (!ReferenceEquals(sceneRenderMode, mode))
        {
            Picker?.Cancel("Scene render mode changed"); Gizmos?.Cancel(); CameraOrientation?.CancelInteraction();
            ClearMaterialHighlight(); sceneRenderMode = mode;
        }
        if (loadedCompositor?.Game is EditorTopLevelCompositor top) top.EnablePreviewGame = IsGamePreview;
        foreach (var filter in materialStreamFilters)
            filter.MaterialFilter = mode.Mode == RenderMode.SingleStream ? mode.StreamDescriptor?.Filter : null;
        SynchronizeRenderModeCameras();
    }
    private void SynchronizeRenderModeCameras()
    {
        void Update(Entity entity)
        {
            var owner = RuntimeOwner(entity);
            if (owner == null || !owner.Asset.Hierarchy.Parts.TryGetValue(entity.Id, out var design)) return;
            foreach (var camera in entity.Components.OfType<CameraComponent>())
                camera.Enabled = IsGamePreview && design.Entity.Components.OfType<CameraComponent>().Any(source => source.Id == camera.Id && source.Enabled);
        }
        foreach (var entity in runtimeEntities.Values) if (entity.Scene == loadedScene) Update(entity);
        foreach (var child in childScenes.Values)
            foreach (var entity in child.Entities.Values) if (entity.Scene == child.Scene) Update(entity);
    }
    private void ClearSceneRenderModes()
    {
        if (sceneRenderModeRegistrySubscribed)
        {
            AssemblyRegistry.AssemblyRegistered -= OnSceneRenderModeAssemblyChanged;
            AssemblyRegistry.AssemblyUnregistered -= OnSceneRenderModeAssemblyChanged;
            sceneRenderModeRegistrySubscribed = false;
        }
        foreach (var filter in materialStreamFilters) { filter.MaterialFilter = null; filter.IsContentObject = null; }
        materialStreamFilters.Clear(); sceneRenderModes.Clear(); sceneRenderModeRegistryVersion = -1; sceneRenderModesChanged = 1;
        sceneRenderMode = EditorRenderMode.DefaultEditor;
    }
}
