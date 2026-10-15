using Hexa.NET.ImGui;
using Stride.Assets;
using Stride.Assets.Entities;
using Stride.Core;
using Stride.Core.Assets;
using Stride.Core.IO;
using Stride.Core.Mathematics;
using Stride.Core.Serialization;
using Stride.Core.Serialization.Contents;
using Stride.Core.Storage;
using Stride.Engine.Design;
using Stride.Engine;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Rendering.Compositing;

namespace Stride.Editor.ImGui;

/// <summary>The real SceneSystem/compositor draws into this target; ImGui only presents it.</summary>
internal sealed partial class EngineViewport : IDisposable, IEditorResourceRetirementDrain
{
    internal SceneTransformGizmos? Gizmos { get; private set; }
    internal SceneComponentGizmos? ComponentGizmos { get; private set; }
    internal SceneLightProbeVolumes? LightProbeVolumes { get; private set; }
    internal SceneViewportGrid? Grid { get; private set; }
    internal SceneCameraOrientation? CameraOrientation { get; private set; }
    private SceneSpaceMarker? spaceMarker;
    private SceneMeshEffects? sceneMeshEffects;
    private readonly Game game;
    private readonly ImGuiRenderer renderer;
    private readonly Dictionary<Guid, Entity> runtimeEntities = [];
    private long synchronizedStructureVersion = -1;
    internal int StructuralSynchronizationPasses { get; private set; }
    internal double StructuralSynchronizationMilliseconds { get; private set; }
    internal bool HasRuntimeEntity(Guid id, Guid? parentId) => runtimeEntities.TryGetValue(id, out var entity)
        && entity.Transform.Parent?.Entity.Id == parentId && entity.Scene == loadedScene
        && ownedSceneInstance?.Contains(entity) == true;
    internal bool EntityReferencesMatch(EntityHierarchyDocument document)
    {
        object? Map(object? value) => value switch
        {
            Entity entity => document.Asset.Hierarchy.Parts.ContainsKey(entity.Id) && runtimeEntities.TryGetValue(entity.Id, out var target) ? target : null,
            EntityComponent component => document.Asset.Hierarchy.Parts.ContainsKey(component.Entity.Id) && runtimeEntities.TryGetValue(component.Entity.Id, out var target)
                ? target.Components.FirstOrDefault(candidate => candidate.Id == component.Id) : null,
            _ => null,
        };
        foreach (var field in document.GetEntityReferenceFields())
        {
            var runtime = runtimeEntities[field.Component.Entity.Id].Components.Single(component => component.Id == field.Component.Id);
            var source = field.Node.Retrieve(); var target = field.ReadRuntime(runtime);
            if (source is System.Collections.IEnumerable values && source is not Entity && source is not EntityComponent)
            {
                if (target is not System.Collections.IEnumerable runtimeValues) return false;
                var expected = values.Cast<object?>().Select(Map).ToArray(); var actual = runtimeValues.Cast<object?>().ToArray();
                if (expected.Length != actual.Length || expected.Where((value, index) => !ReferenceEquals(value, actual[index])).Any()) return false;
            }
            else if (!ReferenceEquals(Map(source), target)) return false;
        }
        return true;
    }
    private readonly Dictionary<ModelComponent, string> modelUrls = [];
    private ContentManager? content;
    internal CompiledProjectContent? SourceContent { get; private set; }
    internal string? CompiledSceneUrl { get; private set; }
    internal EngineViewportCompiledInputs? CompiledInputs { get; private set; }
    private Scene? loadedScene;
    private Prefab? loadedPrefab;
    private GraphicsCompositor? loadedCompositor;
    private SceneInstance? ownedSceneInstance;
    private SceneViewportAuthority? sceneAuthority;
    internal bool IsSceneRetirementStarted => sceneAuthority?.IsStopping == true;
    private readonly List<SceneViewportAuthority> drainingAuthorities = [];
    private readonly List<IDisposable> sceneLeases = [];
    private readonly List<SceneViewportResources> sceneResourceOwners = [];
    private readonly List<PreviewTargetResources> targetResourceOwners = [];
    internal bool IsResourceRetirementComplete
    {
        get
        {
            sceneResourceOwners.RemoveAll(owner => owner.IsDisposed);
            targetResourceOwners.RemoveAll(owner => owner.IsDisposed);
            return targetOwnerDisposed && sceneAuthority == null && sceneResourceOwners.Count == 0 && targetResourceOwners.Count == 0;
        }
    }
    private SceneViewportResources CreateSceneResources(CompiledProjectContent? source, IDisposable? mount)
    {
        sceneResourceOwners.RemoveAll(owner => owner.IsDisposed);
        var owner = new SceneViewportResources(game, source, mount); sceneResourceOwners.Add(owner); return owner;
    }
    internal SceneInstance? RuntimeScene => ownedSceneInstance;
    private bool visible;
    internal bool Visible
    {
        get => visible;
        set { visible = value; if (sceneAuthority != null) sceneAuthority.SceneSystem.Visible = value; }
    }
    internal void Update(Stride.Games.GameTime time) => sceneAuthority?.Update(time);
    internal void RetainSceneLease(IDisposable lease)
    { ArgumentNullException.ThrowIfNull(lease); sceneLeases.Add(lease); }
    internal IDisposable AcquireSceneContentRecoveryLease()
        => SourceContent != null && sceneContentLease != null ? SourceContent.DuplicateRetainedLease(sceneContentLease)
            : throw new InvalidOperationException("The actual scene content lease is unavailable for recovery.");
    internal bool IsSceneRetirementDrainComplete => (sceneAuthority == null || sceneAuthority.IsShutdownComplete)
        && drainingAuthorities.All(authority => authority.IsShutdownComplete);
    bool IEditorResourceRetirementDrain.IsRetirementDrainComplete => IsSceneRetirementDrainComplete;
    bool IEditorResourceRetirementDrain.PumpRetirementDrain() => PumpSceneRetirementDrain();
    internal Task BeginSceneRetirementDrain()
    {
        var workers = drainingAuthorities.Select(authority => authority.BeginShutdown()).ToList();
        if (sceneAuthority != null) workers.Add(sceneAuthority.BeginShutdown());
        return Task.WhenAll(workers);
    }
    internal bool PumpSceneRetirementDrain()
    {
        var complete = true;
        if (sceneAuthority != null) complete = sceneAuthority.PumpShutdown();
        foreach (var authority in drainingAuthorities) if (!authority.PumpShutdown()) complete = false;
        drainingAuthorities.RemoveAll(authority => authority.IsReleased);
        return complete;
    }
    internal void Render(Stride.Games.GameTime time)
    {
        if (!Visible || sceneAuthority == null || Target == null) return;
        sceneAuthority.Render(time, Depth, Target);
        RecordVerificationSceneRender(Target);
    }
    internal void ActivateOwnedScene()
    {
        if (sceneAuthority != null && !ReferenceEquals(sceneAuthority.SceneSystem.SceneInstance, ownedSceneInstance))
            throw new InvalidOperationException("The viewport's native scene authority changed.");
    }
    internal EditorPickingRenderer? Picker { get; private set; }
    private readonly HashSet<Entity> selectedModels = [];
    private readonly List<Stride.Rendering.EditorWireframeRenderFeature> selectionFeatures = [];
    private long synchronizedSelectionVersion = -1;
    internal bool ShowSelection { get; set; } = true;
    internal Stride.Rendering.EditorSelectionStageRenderer? SelectionRenderer { get; private set; }
    internal int SelectedModelCount => selectedModels.Count;
    internal int PreparedSelectionNodes => selectionFeatures.Sum(feature => feature.PreparedNodes);
    internal Vector2 EntityScreenPosition(string name)
    {
        var entity = runtimeEntities.Values.Single(entity => entity.Name == name);
        var point = entity.Get<ModelComponent>().BoundingSphere.Center;
        var clip = Vector3.TransformCoordinate(point, Camera.Component.ViewProjectionMatrix);
        return new Vector2((clip.X + 1) / 2, (1 - clip.Y) / 2);
    }
    public EditorViewportCamera Camera { get; private set; } = new();
    public Texture? Target { get; private set; }
    public Texture? Depth { get; private set; }
    public ImTextureRef TextureRef { get; private set; }
    public int ModelCount { get; private set; }
    public int BackgroundCount { get; private set; }
    public bool UsesAuthoredCompositor => loadedCompositor != null;
    public List<string> Gaps { get; } = [];
    public EngineViewport(Game game, ImGuiRenderer renderer) { this.game = game; this.renderer = renderer; }

    public void Load(EntityHierarchyDocument document, ProjectWorkspace workspace, CompiledProjectContent projectContent,
        IDisposable? retainedContentLease = null, EngineViewportCompiledInputs? retainedInputs = null)
    {
        ClearScene(); Gaps.Clear();
        try
        {
            PresentedDocument = document;
            SourceContent = projectContent;
            sceneContentLease = retainedContentLease == null ? projectContent.AcquireLease() : projectContent.DuplicateRetainedLease(retainedContentLease);
            presentedDocumentLease = document.SessionHistory?.Registry.Find(document.Asset.Id)?.AcquireView();
            content = projectContent.CreateObjectCacheForRetainedLease(sceneContentLease);
            sceneAuthority = new SceneViewportAuthority(game, content);
            var sceneAsset = workspace.AssetItems.Single(item => string.Equals(item.FullPath.ToOSPath(), document.Path, StringComparison.OrdinalIgnoreCase));
            if (sceneAsset.Id != document.Asset.Id) throw new InvalidOperationException("The compiled scene asset identity no longer matches its canonical document.");
            if (retainedInputs != null && retainedContentLease == null)
                throw new InvalidOperationException("A retained compiled scene URL requires its actual bundle lease.");
            CompiledSceneUrl = retainedInputs?.SceneUrl ?? sceneAsset.Location.ToString();
            CompiledSceneUrl = (game as EditorGame)?.ConsumeSceneLoadVerificationUrl(this, document, CompiledSceneUrl) ?? CompiledSceneUrl;
            if (document.Asset is SceneAsset) loadedScene = content.Load<Scene>(CompiledSceneUrl);
            else if (document.Asset is PrefabAsset)
            {
                loadedPrefab = content.Load<Prefab>(CompiledSceneUrl);
                loadedScene = new Scene();
                foreach (var root in loadedPrefab.Entities) loadedScene.Entities.Add(root);
            }
            else throw new InvalidOperationException("This entity hierarchy has no editor runtime adapter.");
            void Register(Entity entity)
            {
                runtimeEntities.Add(entity.Id, entity);
                if (entity.Get<ModelComponent>() is { Model: not null } model)
                {
                    ModelCount++;
                    if (content.TryGetAssetUrl(model.Model, out var url)) modelUrls[model] = url;
                }
                if (entity.Get<BackgroundComponent>() is { Enabled: true, Texture: not null }) BackgroundCount++;
                foreach (var camera in entity.Components.OfType<CameraComponent>()) camera.Enabled = false;
                foreach (var child in entity.Transform.Children) Register(child.Entity);
            }
            foreach (var entity in loadedScene.Entities) Register(entity);
            if (document.Asset is PrefabAsset) AddPrefabEditorLights(loadedScene);
            var settings = InitializeSceneSettings(workspace);
            var compositorReference = AttachedReferenceManager.GetAttachedReference(settings.GraphicsCompositor);
            if (retainedInputs == null && compositorReference == null)
                throw new InvalidOperationException("The project game settings have no graphics compositor reference.");
            string compositorUrl;
            if (retainedInputs is { } retained) compositorUrl = retained.CompositorUrl;
            else
            {
                // Serialized references may carry a package-local URL. Resolve
                // the canonical asset identity before opening compiled content,
                // whose key includes the owning package's asset namespace.
                var compositorAsset = workspace.FindAsset(compositorReference!.Id);
                if (compositorAsset.Asset is not Stride.Assets.Rendering.GraphicsCompositorAsset)
                    throw new InvalidOperationException("The game settings graphics compositor identity does not refer to a graphics compositor asset.");
                compositorUrl = NativeAssetUrls.Qualify(compositorAsset);
            }
            loadedCompositor = content.Load<GraphicsCompositor>(compositorUrl);
            LoadedCompositorId = retainedInputs is { } captured ? captured.CompositorId : compositorReference!.Id;
            CompiledInputs = new(CompiledSceneUrl, compositorUrl, LoadedCompositorId);
            if (loadedCompositor.Editor == null) throw new InvalidOperationException("The authored compositor has no editor renderer.");
            var cameraEntity = new Entity("Editor camera");
            Camera = new EditorViewportCamera();
            var camera = Camera.Component;
            cameraEntity.Add(camera); loadedScene.Entities.Add(cameraEntity);
            // Same wrapper as the WPF entity editor; retain authored stages/features.
            loadedCompositor.Game = new EditorTopLevelCompositor
            {
                ExternalCamera = camera, Child = loadedCompositor.Editor, PreviewGame = loadedCompositor.Game,
            };
            var pickingStage = new RenderStage("Picking", "Picking");
            loadedCompositor.RenderStages.Add(pickingStage);
            Picker = new EditorPickingRenderer { PickingRenderStage = pickingStage, IsPickable = CanPickSceneEntity };
            foreach (var entity in runtimeEntities.Values) Picker.CacheEntity(entity);
            foreach (var meshFeature in loadedCompositor.RenderFeatures.OfType<Stride.Rendering.MeshRenderFeature>())
            {
                meshFeature.RenderFeatures.Add(new EditorPickingRenderFeature { IsEnabled = () => Picker?.IsPending == true });
                meshFeature.RenderStageSelectors.Add(new Stride.Rendering.SimpleGroupToRenderStageSelector
                { EffectName = "StrideEditorForwardShadingEffect.Picking", RenderStage = pickingStage, RenderGroup = Stride.Rendering.RenderGroupMask.All });
            }
            ((EditorTopLevelCompositor)loadedCompositor.Game).PostGizmoCompositors.Add(Picker);
            var selectionStage = new Stride.Rendering.RenderStage("Editor selection", "Wireframe") { Filter = new SelectionFilter(selectedModels) };
            loadedCompositor.RenderStages.Add(selectionStage);
            foreach (var meshFeature in loadedCompositor.RenderFeatures.OfType<Stride.Rendering.MeshRenderFeature>())
            {
                var feature = new Stride.Rendering.EditorWireframeRenderFeature
                { IsEnabled = () => ShowSelection && (selectedModels.Count != 0 || ComponentGizmos?.HasSelectedLightProbes == true), DisplaySelectionMask = () => true };
                selectionFeatures.Add(feature); meshFeature.RenderFeatures.Add(feature);
                meshFeature.RenderStageSelectors.Add(new Stride.Rendering.SimpleGroupToRenderStageSelector
                { EffectName = "StrideEditorForwardShadingEffect.Wireframe", RenderStage = selectionStage, RenderGroup = Stride.Rendering.RenderGroupMask.All });
            }
            SelectionRenderer = new Stride.Rendering.EditorSelectionStageRenderer
            { RenderStage = selectionStage, IsEnabled = () => ShowSelection && selectedModels.Count != 0 };
            ConfigureSelectionMaskRendering();
            sceneMeshEffects = new SceneMeshEffects(game, loadedCompositor);
            ((EditorTopLevelCompositor)loadedCompositor.Game).PostGizmoCompositors.Add(SelectionRenderer);
            InitializeMaterialHighlight();
            Grid = new SceneViewportGrid(game, loadedScene, loadedCompositor, Camera, sceneAuthority.Services);
            ComponentGizmos = new SceneComponentGizmos(game, loadedScene, loadedCompositor, Camera, pickingStage, Picker, SceneGameAspect(1.7778f), sceneAuthority.Services);
            LightProbeVolumes = new SceneLightProbeVolumes(game, loadedScene, loadedCompositor);
            Gizmos = new SceneTransformGizmos(game, loadedScene, loadedCompositor, Camera, sceneAuthority.Services) { ResolveRuntimeEntity = ResolveRuntimeEntity };
            spaceMarker = new SceneSpaceMarker(game, loadedScene, loadedCompositor, Camera, sceneAuthority.Services);
            CameraOrientation = new SceneCameraOrientation(game, loadedScene, loadedCompositor, Camera, sceneAuthority.Services);
            sceneAuthority.Initialize(loadedScene, loadedCompositor);
            ownedSceneInstance = sceneAuthority.SceneSystem.SceneInstance;
            sceneAuthority.SceneSystem.Visible = Visible;
            InitializeSceneRenderModes(document);
            InitializeCameraPreview();
            InitializeNavigation(workspace, settings);
            Synchronize(document);
        }
        catch { ClearScene(); throw; }
    }

    public void Synchronize(EntityHierarchyDocument document)
    {
        activeHierarchyDocument = document;
        var presented = PresentedDocument ?? document;
        if (SynchronizePresentedRootLoading(presented)) SynchronizePresentedHierarchy(presented);
        RefreshPresentedHierarchyOwners();
        SynchronizeActiveSelection(document);
        SynchronizeSceneSettings();
        if (ComponentGizmos != null) ComponentGizmos.CameraAspectRatio = SceneGameAspect(1.7778f);
        ComponentGizmos?.RefreshRegistry(document.SessionHistory?.Extensions?.Version ?? 0);
        ComponentGizmos?.Synchronize(document, runtimeEntities.Values.Where(entity => entity.Scene == loadedScene)
            .Concat(childScenes.Values.SelectMany(entry => entry.Entities.Values)), RuntimeOwner, CanSelectSceneEntity, IsWorldSelectionEntity);
        if (ownedSceneInstance is { } liveScene) LightProbeVolumes?.Synchronize(liveScene);
        SynchronizeNavigation();
        RefreshSceneRenderModes(document); SynchronizeRenderModeCameras();
    }

    private void SynchronizePresentedHierarchy(EntityHierarchyDocument document)
    {
        var structureChanged = synchronizedStructureVersion != document.StructureVersion;
        if (structureChanged)
        {
            var structuralTimer = System.Diagnostics.Stopwatch.StartNew();
            foreach (var entity in runtimeEntities.Values)
                if (!document.Asset.Hierarchy.Parts.ContainsKey(entity.Id) && entity.Scene == loadedScene
                    && (entity.Transform.Parent == null || document.Asset.Hierarchy.Parts.ContainsKey(entity.Transform.Parent.Entity.Id)))
                {
                    // Retain the compiled instance/resources for undo, while
                    // removing the whole subtree from the actual entity manager.
                    entity.Scene = null;
                }
            Entity? EnsureRuntimeEntity(Entity source)
            {
                runtimeEntities.TryGetValue(source.Id, out var existing);
                if (existing?.Scene == loadedScene && existing.Transform.Parent?.Entity.Id == source.Transform.Parent?.Entity.Id) return existing;
                var parent = source.Transform.Parent == null ? null : EnsureRuntimeEntity(source.Transform.Parent.Entity);
                if (source.Transform.Parent != null && parent == null) return null;
                var entity = existing ?? new Entity(source.Name) { Id = source.Id };
                if (existing == null)
                {
                    entity.Transform.Id = source.Transform.Id;
                    TransformState.Read(source).Apply(entity);
                    runtimeEntities.Add(entity.Id, entity);
                }
                entity.Transform.Parent = parent?.Transform;
                if (parent == null && entity.Scene != loadedScene) loadedScene!.Entities.Add(entity);
                return entity;
            }
            foreach (var design in document.Asset.Hierarchy.Parts.Values) EnsureRuntimeEntity(design.Entity);
            SynchronizeComponentCollections(document);
            // Ordering changes are editor operations too; keep actual engine
            // collections consistent while leaving the external camera intact.
            for (var index = 0; index < document.Asset.Hierarchy.RootParts.Count; index++)
            {
                if (!runtimeEntities.TryGetValue(document.Asset.Hierarchy.RootParts[index].Id, out var entity)) continue;
                if (loadedScene!.Entities.IndexOf(entity) != index) { loadedScene.Entities.Remove(entity); loadedScene.Entities.Insert(index, entity); }
            }
            foreach (var design in document.Asset.Hierarchy.Parts.Values)
            {
                if (!runtimeEntities.TryGetValue(design.Entity.Id, out var parent)) continue;
                for (var index = 0; index < design.Entity.Transform.Children.Count; index++)
                {
                    if (!runtimeEntities.TryGetValue(design.Entity.Transform.Children[index].Entity.Id, out var entity)) continue;
                    if (parent.Transform.Children.IndexOf(entity.Transform) != index)
                    { parent.Transform.Children.Remove(entity.Transform); parent.Transform.Children.Insert(index, entity.Transform); }
                }
            }
            foreach (var field in document.GetEntityReferenceFields())
            {
                if (!runtimeEntities.TryGetValue(field.Component.Entity.Id, out var owner)) continue;
                var component = owner.Components.FirstOrDefault(candidate => candidate.Id == field.Component.Id);
                if (component == null) continue;
                object? MapReference(object? value) => value switch
                {
                    Entity entity => document.Asset.Hierarchy.Parts.ContainsKey(entity.Id) && runtimeEntities.TryGetValue(entity.Id, out var runtime) ? runtime : null,
                    EntityComponent target => document.Asset.Hierarchy.Parts.ContainsKey(target.Entity.Id) && runtimeEntities.TryGetValue(target.Entity.Id, out var runtime)
                        ? runtime.Components.FirstOrDefault(candidate => candidate.Id == target.Id) : null,
                    _ => null,
                };
                var authored = field.Node.Retrieve();
                if (authored is System.Collections.IEnumerable values && authored is not Entity && authored is not EntityComponent && authored is not string)
                {
                    var runtimeCollection = field.ReadRuntime(component);
                    if (runtimeCollection == null) continue;
                    var descriptor = Stride.Core.Reflection.TypeDescriptorFactory.Default.Find(runtimeCollection.GetType()) as Stride.Core.Reflection.CollectionBaseDescriptor;
                    if (descriptor == null || descriptor is Stride.Core.Reflection.CollectionDescriptor collection && !collection.HasIndexerAccessors)
                        throw new InvalidOperationException($"Entity reference collection {field.Name} ({runtimeCollection.GetType().FullName}) needs writable index access.");
                    var index = 0;
                    foreach (var value in values)
                    {
                        var key = index++; var mapped = MapReference(value);
                        if (!ReferenceEquals(descriptor.GetValue(runtimeCollection, key), mapped)) descriptor.SetValue(runtimeCollection, key, mapped);
                    }
                }
                else if (field.Node.MemberDescriptor.HasSet && !ReferenceEquals(field.ReadRuntime(component), MapReference(authored)))
                    field.WriteRuntime(component, MapReference(authored));
            }
            ModelCount = runtimeEntities.Values.Count(entity => entity.Scene == loadedScene && entity.Get<ModelComponent>()?.Model != null);
            BackgroundCount = runtimeEntities.Values.Count(entity => entity.Scene == loadedScene && entity.Get<BackgroundComponent>() is { Enabled: true, Texture: not null });
            Picker?.Cancel("Scene hierarchy changed");
            synchronizedStructureVersion = document.StructureVersion;
            selectionRuntimeVersion++;
            StructuralSynchronizationPasses++;
            StructuralSynchronizationMilliseconds += structuralTimer.Elapsed.TotalMilliseconds;
        }
        if (structureChanged) document.PruneSelection();
        foreach (var source in document.Asset.Hierarchy.Parts.Values)
            if (runtimeEntities.TryGetValue(source.Entity.Id, out var target))
            {
                var state = TransformState.Read(source.Entity);
                if (state != TransformState.Read(target)) { Picker?.Cancel("Scene transform changed"); state.Apply(target); }
                target.Name = source.Entity.Name;
            }
        foreach (var field in document.ObservedComponentFields)
        {
            if (!runtimeEntities.TryGetValue(field.Component.Entity.Id, out var entity)) continue;
            var component = FindObservedRuntimeComponent(entity, field.Component.Id);
            var value = field.Node.Retrieve();
            if (field.IsContentReference) value = ResolveRuntimeContentReference(value);
            if (field.IsSceneReference)
            {
                value = value switch
                {
                    Entity referenced => document.Asset.Hierarchy.Parts.ContainsKey(referenced.Id)
                        ? runtimeEntities.GetValueOrDefault(referenced.Id) : null,
                    EntityComponent referenced => document.Asset.Hierarchy.Parts.ContainsKey(referenced.Entity.Id)
                        && runtimeEntities.TryGetValue(referenced.Entity.Id, out var owner)
                        ? FindObservedRuntimeComponent(owner, referenced.Id) : null,
                    _ => null,
                };
            }
            if (component is CameraComponent && field.Name == nameof(CameraComponent.Enabled)) continue;
            if (component != null && !Equals(field.ReadRuntime(component), value))
            {
                Picker?.Cancel("Scene component changed"); field.WriteRuntime(component, value);
                NotifyRuntimeComponentPropertyChanged(field, component);
                if (field.IsContentReference) NotifyRuntimeContentReferenceChanged(field, component, value);
            }
        }
        Camera.Component.Update();
        SynchronizeChildScenes(document);
        RefreshMaterialHighlight();
        Grid?.Update();
        CameraOrientation?.Update();
        spaceMarker?.Update();
    }
    internal void ReplaceRuntimeModel(string url, Model model)
    {
        Picker?.Cancel("Scene geometry changed");
        foreach (var entry in modelUrls.Where(entry => string.Equals(entry.Value, url, StringComparison.Ordinal)))
        {
            if (!ReferenceEquals(entry.Key.Model, model)) selectionRuntimeVersion++;
            entry.Key.Model = model;
            if (entry.Key.Entity != null) Picker?.CacheEntity(entry.Key.Entity);
        }
        CacheRefreshedRuntimeModel(url, model);
    }
    internal float RuntimeModelHalfExtent(string url)
    {
        var box = modelUrls.First(entry => entry.Value == url).Key.Model.BoundingBox;
        var extent = (box.Maximum - box.Minimum) / 2;
        return Math.Max(extent.X, Math.Max(extent.Y, extent.Z));
    }
    internal Vector3 RuntimeModelSize(string url)
    {
        var box = modelUrls.First(entry => entry.Value == url).Key.Model.BoundingBox;
        return box.Maximum - box.Minimum;
    }
    internal (string? Url, bool CastsShadows) RuntimeModelMaterial(string url)
    {
        var material = modelUrls.First(entry => entry.Value == url).Key.Model.Materials.FirstOrDefault();
        return (AttachedReferenceManager.GetAttachedReference(material?.Material)?.Url, material?.IsShadowCaster == true);
    }
    internal bool MatchesName(Entity source) => runtimeEntities.TryGetValue(source.Id, out var target) && target.Name == source.Name;
    internal bool MatchesComponent(ComponentField entry)
    {
        if (!runtimeEntities.TryGetValue(entry.Component.Entity.Id, out var entity)) return false;
        var component = entity.Components.FirstOrDefault(component => component.Id == entry.Component.Id);
        if (component == null) return false;
        var authored = entry.Node.Retrieve();
        var actual = entry.ReadRuntime(component);
        if (!entry.IsSceneReference) return Equals(authored, actual);
        object? expected = authored switch
        {
            Entity referenced => runtimeEntities.GetValueOrDefault(referenced.Id),
            EntityComponent referenced => runtimeEntities.TryGetValue(referenced.Entity.Id, out var owner)
                ? owner.Components.FirstOrDefault(candidate => candidate.Id == referenced.Id) : null,
            _ => null,
        };
        return (authored == null || expected != null) && ReferenceEquals(expected, actual);
    }
    internal ImportedModelEvidence InspectImportedFixture(EntityHierarchyDocument document, ProjectWorkspace workspace)
    {
        var source = document.Asset.Hierarchy.Parts.Values.FirstOrDefault(part => part.Entity.Name == "Imported cube")?.Entity;
        if (source == null) return new(false, false, 0, 0, 0, 0, null);
        var sourceModel = source.Get<ModelComponent>()?.Model;
        var reference = sourceModel == null ? null : AttachedReferenceManager.GetAttachedReference(sourceModel);
        var imported = reference != null && workspace.AssetItems.Any(item => item.Id == reference.Id && item.Asset is Stride.Assets.Models.ModelAsset);
        var model = runtimeEntities.GetValueOrDefault(source.Id)?.Get<ModelComponent>()?.Model;
        var meshes = model?.Meshes.ToArray() ?? [];
        return new(true, imported, meshes.Length,
            meshes.Sum(mesh => mesh.Draw.VertexBuffers.Sum(binding => (long)binding.Buffer.SizeInBytes)),
            meshes.Sum(mesh => (long)(mesh.Draw.IndexBuffer?.Buffer?.SizeInBytes ?? 0)),
            model?.Materials.Sum(material => material.Material?.Passes.Count ?? 0) ?? 0, reference?.Url);
    }
    internal bool MatchesTransform(Entity source) => runtimeEntities.TryGetValue(source.Id, out var runtime)
        && TransformState.Read(source) == TransformState.Read(runtime);
    internal bool MatchesWorldTransform(Entity source)
    {
        source.Transform.UpdateWorldMatrix();
        if (!runtimeEntities.TryGetValue(source.Id, out var runtime)) return false;
        runtime.Transform.UpdateWorldMatrix();
        return source.Transform.WorldMatrix.ToArray().Zip(runtime.Transform.WorldMatrix.ToArray()).All(pair => Math.Abs(pair.First - pair.Second) < 0.0001f);
    }
    internal bool MatchesHierarchyOrder(EntityHierarchyDocument document)
    {
        if (!loadedScene!.Entities.Where(entity => runtimeEntities.ContainsKey(entity.Id)).Select(entity => entity.Id)
            .SequenceEqual(document.Asset.Hierarchy.RootParts.Select(entity => entity.Id))) return false;
        foreach (var part in document.Asset.Hierarchy.Parts.Values)
            if (!runtimeEntities.TryGetValue(part.Entity.Id, out var runtime)
                || !runtime.Transform.Children.Select(child => child.Entity.Id).SequenceEqual(part.Entity.Transform.Children.Select(child => child.Entity.Id))) return false;
        return true;
    }
    internal bool SelectedFitsCamera(Entity source) => runtimeEntities[source.Id].Get<ModelComponent>().BoundingBox.GetCorners().All(corner =>
    {
        var clip = Vector3.TransformCoordinate(corner, Camera.Component.ViewProjectionMatrix);
        return float.IsFinite(clip.X) && float.IsFinite(clip.Y) && float.IsFinite(clip.Z)
            && Math.Abs(clip.X) <= 1.001f && Math.Abs(clip.Y) <= 1.001f && clip.Z >= 0 && clip.Z <= 1;
    });
    public void Focus(Entity source)
        => Focus(activeHierarchyDocument, source);
    internal void Focus(EntityHierarchyDocument? owner, Entity source)
    {
        if (!TryFocusHighlightedMaterial()) Focus(owner, source, -1);
    }
    internal void Focus(EntityHierarchyDocument? owner, Entity source, int meshIndex)
    {
        var entity = ResolveRuntimeEntity(owner, source.Id);
        if (entity == null) return;
        entity.Transform.UpdateWorldMatrix();
        var model = entity.Get<ModelComponent>()?.Model;
        var validMesh = meshIndex >= 0 && model != null && meshIndex < model.Meshes.Count;
        var bounds = validMesh
            ? Stride.Editor.Engine.EntityExtensions.CalculateBoundSphere(entity, false, current => current == model ? [current.Meshes[meshIndex]] : [])
            : Stride.Editor.Engine.EntityExtensions.CalculateBoundSphere(entity);
        Camera.Focus(bounds.Center, bounds.Radius);
    }
    public void Resize(int width, int height)
    {
        ObjectDisposedException.ThrowIf(targetOwnerDisposed, this);
        width = Math.Clamp(width, 1, 4096); height = Math.Clamp(height, 1, 4096);
        if (Target?.Width == width && Target.Height == height) return;
        var format = game.GraphicsDevice.ColorSpace == ColorSpace.Linear ? PixelFormat.R8G8B8A8_UNorm_SRgb : PixelFormat.R8G8B8A8_UNorm;
        var next = new PreviewTargetResources(game, renderer);
        targetResourceOwners.RemoveAll(owner => owner.IsDisposed);
        targetResourceOwners.Add(next);
        try { next.Allocate(width, height, format, format, depth: true); ReleaseOwnedTarget(); }
        catch { next.Retire(); throw; }
        targetResources = next; Target = next.Target; Depth = next.Depth; TextureRef = next.Reference;
    }
    private PreviewTargetResources? targetResources;
    private bool targetOwnerDisposed;
    private void ReleaseOwnedTarget()
    {
        if (targetResources == null) return;
        targetResources.Retire(); targetResources = null;
        Target = Depth = null; TextureRef = default;
    }
    internal void ClearScene()
    {
        if (clearingSceneResources == null && SourceContent == null && content == null && ownedSceneInstance == null && presentedDocumentLease == null && sceneAuthority == null && sceneLeases.Count == 0) return;
        var retired = clearingSceneResources;
        if (retired == null)
        { retired = clearingSceneResources = CreateSceneResources(SourceContent, sceneContentLease); sceneContentLease = null; }
        sceneAuthority?.BeginShutdown();
        if (game is EditorGame editor) editor.CancelSceneCameraNavigation();
        ClearSceneAnchors();
        ClearSelectedAssetHighlights();
        ClearSceneRenderModes();
        ClearMaterialHighlight();
        CancelLightProbeBake(retired);
        ClearCameraPreview(retired);
        ClearNavigation(retired);
        ClearChildScenes(retired);
        if (Gizmos != null) { retired.Retain(Gizmos); Gizmos = null; }
        if (ComponentGizmos != null) { retired.Retain(ComponentGizmos); ComponentGizmos = null; }
        if (LightProbeVolumes != null) { retired.Retain(LightProbeVolumes); LightProbeVolumes = null; }
        if (Grid != null) { retired.Retain(Grid); Grid = null; }
        if (CameraOrientation != null) { retired.Retain(CameraOrientation); CameraOrientation = null; }
        if (spaceMarker != null) { retired.Retain(spaceMarker); spaceMarker = null; }
        if (sceneMeshEffects != null) { retired.Retain(sceneMeshEffects); sceneMeshEffects = null; }
        if (Picker != null) { Picker.Cancel("Scene retired"); retired.Retain(Picker); Picker = null; }
        if (SelectionRenderer != null) { retired.Retain(SelectionRenderer); SelectionRenderer = null; }
        selectedModels.Clear(); ClearWorldSelection(); synchronizedSelectionVersion = -1; selectionFeatures.Clear();
        observedFailureSelectionVersions.Clear(); selectionSourceVersion++;
        if (sceneAuthority != null)
        { sceneAuthority.RetainForRetirement(retired); drainingAuthorities.Add(sceneAuthority); sceneAuthority = null; }
        ownedSceneInstance = null; Visible = false;
        retired.RetainCache(content);
        retired.RetainTextureConsumer(loadedScene);
        foreach (var value in componentContent.Values) retired.RetainTextureConsumer(value);
        foreach (var value in ImportRuntimeEntities(PresentedDocument)) retired.RetainTextureConsumer(value);
        ClearComponentContent(retired);
        loadedScene = null; loadedPrefab = null; loadedCompositor = null;
        runtimeEntities.Clear();
        synchronizedStructureVersion = -1;
        StructuralSynchronizationPasses = 0; StructuralSynchronizationMilliseconds = 0;
        modelUrls.Clear();
        ModelCount = 0; BackgroundCount = 0;
        if (presentedDocumentLease != null) { retired.RetainLease(presentedDocumentLease); presentedDocumentLease = null; }
        foreach (var lease in sceneLeases) retired.RetainLease(lease);
        sceneLeases.Clear();
        RetireSceneSettings(retired);
        retired.Retire(); clearingSceneResources = null;
        content = null; SourceContent = null;
        CompiledSceneUrl = null;
        CompiledInputs = null;
        PresentedDocument = activeHierarchyDocument = selectedHierarchyDocument = null;
        selectionRuntimeVersion++; selectedRuntimeVersion = -1;
        presentedHierarchyDocuments.Clear(); selectedOwnerVersions.Clear(); PresentedOwnerVersion++;
    }
    private SceneViewportResources? clearingSceneResources;
    private IDisposable? sceneContentLease;
    public void Dispose()
    {
        if (targetOwnerDisposed) return;
        ClearScene();
        ReleaseOwnedTarget(); targetOwnerDisposed = true;
    }
    private sealed class SelectionFilter(HashSet<Entity> selected) : Stride.Rendering.RenderStageFilter
    {
        public override bool IsVisible(Stride.Rendering.RenderObject renderObject, Stride.Rendering.RenderView renderView, Stride.Rendering.RenderViewStage renderViewStage)
            => (renderObject as Stride.Rendering.RenderMesh)?.Source is ModelComponent model && selected.Contains(model.Entity);
    }
}

internal sealed record ImportedModelEvidence(bool FixturePresent, bool SourceIsImportedModel, int Meshes, long VertexBufferBytes, long IndexBufferBytes, int MaterialPasses, string? AssetUrl);
internal readonly record struct EngineViewportCompiledInputs(string SceneUrl, string CompositorUrl, Stride.Core.Assets.AssetId? CompositorId);



