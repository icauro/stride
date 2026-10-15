using Hexa.NET.ImGui;
using Stride.Assets.Presentation.AssetEditors.Gizmos;
using Stride.Assets.Presentation.AssetEditors.GameEditor;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Engine.Gizmos;
using Stride.Rendering;
using Stride.Rendering.Compositing;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class SceneTransformGizmos : TransformGizmoHost, IDisposable
{
    private readonly EditorViewportCamera camera;
    private readonly Scene scene = new();
    private readonly TransformationGizmo[] gizmos = [new TranslationGizmo(), new RotationGizmo(), new ScaleGizmo()];
    private readonly HashSet<ModelComponent> models = [];
    private EntityHierarchyDocument? document;
    private IReadOnlyCollection<Entity> modifiedEntities = Array.Empty<Entity>();
    private sealed record TransformTarget(EntityHierarchyDocument Owner, Entity? Authored, Entity Runtime, SceneOffsetRuntimeTarget? SceneRoot = null);
    private readonly List<TransformTarget> transformTargets = [];
    private readonly Dictionary<EntityHierarchyDocument, (long Selection, long Structure)> ownerVersions = [];
    private readonly Dictionary<EntityHierarchyDocument, Entity[]> transformOwnerRoots = [];
    private long selectionPolicyVersion = -1;
    private EntityHierarchyDocument[] gestureOwners = [];
    internal Func<IReadOnlyList<EntityHierarchyDocument>>? SelectionDocuments { get; set; }
    internal Func<EntityHierarchyDocument, Entity[]>? SelectionRoots { get; set; }
    internal Func<long>? SelectionPolicyVersion { get; set; }
    internal Func<(EntityHierarchyDocument Document, Entity Entity)?>? SelectionPrimary { get; set; }
    internal Func<EntityHierarchyDocument?, Guid, Entity?>? ResolveRuntimeEntity { get; set; }
    internal Func<EntityHierarchyDocument, bool>? CanEditDocument { get; set; }
    internal Func<bool>? CanDuplicateSelectedEntities { get; set; }
    internal Action? DuplicateSelectedEntities { get; set; }
    private Entity? gestureEntity;
    private bool leftDown;
    private bool controlDown;
    private Vector2 mouse;
    private Vector2 viewportSize = Vector2.One;
    private int mode;
    private bool linearMovementForRotation;
    public override CameraComponent Component => camera.Component;
    public override Vector3 Position => camera.Position;
    public override Vector2 MousePosition => mouse;
    public override Vector2 ViewportSize => viewportSize;
    public override bool IsMoving => false;
    public override bool LeftButtonDown => leftDown;
    public override bool ControlDown => controlDown;
    public override bool CanDuplicateSelection => Captured && DuplicateSelectedEntities != null
        && CanDuplicateSelectedEntities?.Invoke() == true;

    public override Task DuplicateSelection()
    {
        if (!CanDuplicateSelection) return Task.CompletedTask;
        // The shared gizmo calls this after its native movement threshold, before
        // recording the initial transforms of the newly selected runtime copies.
        foreach (var owner in gestureOwners) owner.CompleteMultiTransformGroup = null;
        foreach (var owner in gestureOwners) { owner.CancelMultiTransformEdit(); owner.CancelSceneOffsetEdit(); }
        foreach (var target in transformTargets) RestoreTargetRuntime(target);
        gestureEntity = null; gestureOwners = []; sceneAnchorStarts.Clear();
        try
        {
            DuplicateSelectedEntities!();
            var owners = SelectionDocuments?.Invoke() ?? throw new InvalidOperationException("The duplicated selection has no canonical owners.");
            RebuildTransformTargets(owners);
            if (!CanTransformTargets()) throw new InvalidOperationException("The duplicated selection cannot be transformed.");
            var primary = ResolveTransformAnchor() ?? throw new InvalidOperationException("The duplicated selection has no primary entity.");
            gestureEntity = primary.Runtime;
            BeginGestureTargets();
            foreach (var gizmo in gizmos)
            {
                gizmo.AnchorEntity = primary.Runtime;
                gizmo.ModifiedEntities = modifiedEntities;
            }
            return Task.CompletedTask;
        }
        catch { Cancel(); throw; }
    }
    public override bool UseLinearMovementForRotation => linearMovementForRotation;
    public bool LinearMovementForRotation
    {
        get => linearMovementForRotation;
        set
        {
            if (linearMovementForRotation == value) return;
            Cancel();
            linearMovementForRotation = value;
        }
    }
    public bool Captured => gestureEntity != null;
    private TransformationSpace space = TransformationSpace.WorldSpace;
    public TransformationSpace Space
    {
        get => space;
        set
        {
            var requested = mode == 2 ? TransformationSpace.ObjectSpace : value;
            if (!Enum.IsDefined(requested)) throw new ArgumentOutOfRangeException(nameof(value));
            if (space == requested) return;
            Cancel(); space = requested;
        }
    }
    private readonly bool[] snapActive = new bool[3];
    private float userSize = 1;
    internal Func<bool>? DynamicSnapDown { get; set; }
    public bool Snap { get => snapActive[mode]; set => snapActive[mode] = value; }
    internal bool TranslationSnapActive => snapActive[0];
    public float TranslationSnap { get; set; } = 1;
    public float RotationSnap { get; set; } = 22.5f;
    public float ScaleSnap { get; set; } = 1.1f;
    internal SceneTransformSettings CaptureSettings() => new(snapActive[0], snapActive[1], snapActive[2], TranslationSnap, RotationSnap, ScaleSnap, userSize);
    internal void RestoreSettings(SceneTransformSettings settings)
    {
        if (!settings.Valid) throw new ArgumentException("Invalid scene transform preferences.");
        Cancel(); snapActive[0] = settings.TranslationActive; snapActive[1] = settings.RotationActive; snapActive[2] = settings.ScaleActive;
        TranslationSnap = settings.TranslationValue; RotationSnap = settings.RotationValue; ScaleSnap = settings.ScaleValue;
        userSize = settings.Size;
        foreach (var gizmo in gizmos) gizmo.SizeFactor = Stride.Engine.Design.EditorTransformGizmoSizing.ToSizeFactor(userSize);
    }
    public int Mode
    {
        get => mode;
        set
        {
            var requested = Math.Clamp(value, 0, 2);
            if (mode == requested) return;
            Cancel(); mode = requested;
            if (mode == 2) space = TransformationSpace.ObjectSpace;
        }
    }
    private TransformationGizmo Active => gizmos[mode];

    public SceneTransformGizmos(Game game, Scene parent, GraphicsCompositor compositor, EditorViewportCamera camera, Stride.Core.IServiceRegistry? services = null)
    {
        this.camera = camera;
        parent.Children.Add(scene);
        foreach (var gizmo in gizmos)
        {
            gizmo.Host = this;
            gizmo.Initialize(services ?? game.Services, scene);
            void Collect(Entity entity)
            {
                foreach (var model in entity.Components.OfType<ModelComponent>()) models.Add(model);
                foreach (var child in entity.Transform.Children) Collect(child.Entity);
            }
            Collect(gizmo.GizmoRootEntity);
            gizmo.IsEnabled = false;
        }
        var opaque = new RenderStage("TransformGizmoOpaque", "Main");
        var transparent = new RenderStage("TransformGizmoTransparent", "Main") { SortMode = new BackToFrontSortMode() };
        compositor.RenderStages.Add(opaque); compositor.RenderStages.Add(transparent);
        foreach (var mesh in compositor.RenderFeatures.OfType<MeshRenderFeature>())
        {
            mesh.RenderStageSelectors.Add(new GizmoStageSelector(models, opaque, transparent));
            mesh.PipelineProcessors.Add(new MeshPipelineProcessor { TransparentRenderStage = transparent });
        }
        var top = (EditorTopLevelCompositor)compositor.Game;
        top.AddOverlayGizmoCompositor(new SceneRendererCollection
        {
            new SingleStageRenderer { RenderStage = opaque },
            new SingleStageRenderer { RenderStage = transparent },
        }, EditorTopLevelCompositor.TopmostOverlayGizmoLayer);
    }

    public bool Handle(EntityHierarchyDocument current, System.Numerics.Vector2 origin, System.Numerics.Vector2 size, bool hovered)
    {
        var owners = CollectTargetOwners(SelectionDocuments?.Invoke() ?? (IReadOnlyList<EntityHierarchyDocument>)[current]);
        var changed = selectionContentVersion != (SelectionContentVersion?.Invoke() ?? 0)
            || selectionPolicyVersion != (SelectionPolicyVersion?.Invoke() ?? 0)
            || ownerVersions.Count != owners.Count || owners.Any(owner => !ownerVersions.TryGetValue(owner, out var version)
            || version.Selection != owner.SelectionVersion || version.Structure != owner.StructureVersion)
            || transformTargets.Any(TargetRuntimeChanged);
        if (Captured && (document != current || changed || gestureOwners.Any(owner => !owner.IsEditingMultiTransform && !owner.IsEditingSceneOffset))) Cancel();
        document = current;
        var io = Gui.GetIO();
        viewportSize = new(size.X, size.Y);
        camera.Component.AspectRatio = size.X / Math.Max(1, size.Y);
        camera.Component.Update();
        mouse = new((io.MousePos.X - origin.X) / size.X, (io.MousePos.Y - origin.Y) / size.Y);
        controlDown = io.KeyCtrl;
        if (changed) RebuildTransformTargets(owners);
        var primary = ResolveTransformAnchor();
        var editable = primary != null && CanTransformTargets();
        foreach (var gizmo in gizmos)
        {
            gizmo.AnchorEntity = primary?.Runtime!;
            gizmo.ModifiedEntities = modifiedEntities;
            gizmo.Space = Space;
            gizmo.UseSnap = Snap || (DynamicSnapDown?.Invoke() ?? io.KeyShift);
            gizmo.SnapValue = gizmo is RotationGizmo ? RotationSnap : gizmo is ScaleGizmo ? ScaleSnap : TranslationSnap;
            gizmo.IsEnabled = editable && ReferenceEquals(gizmo, Active);
        }
        if (!editable) { Cancel(); return false; }
        if (Captured && (Gui.IsKeyPressed(ImGuiKey.Escape) || !Gui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows)))
        { Cancel(); return true; }
        // Hover first; only a press on an actual gizmo handle may own the drag.
        leftDown = Captured && Gui.IsMouseDown(ImGuiMouseButton.Left);
        Active.Update().GetAwaiter().GetResult();
        if (!Captured && hovered && !io.KeyAlt && Gui.IsMouseClicked(ImGuiMouseButton.Left) && Active.IsUnderMouse())
        {
            BeforeGesture?.Invoke();
            RebuildTransformTargets(CollectTargetOwners(SelectionDocuments?.Invoke() ?? (IReadOnlyList<EntityHierarchyDocument>)[current]));
            primary = ResolveTransformAnchor();
            if (primary == null || !CanTransformTargets()) return false;
            foreach (var gizmo in gizmos) { gizmo.AnchorEntity = primary.Runtime; gizmo.ModifiedEntities = modifiedEntities; }
            gestureEntity = primary!.Runtime;
            try
            {
                BeginGestureTargets();
            }
            catch { Cancel(); throw; }
            leftDown = true;
            Active.Update().GetAwaiter().GetResult();
        }
        if (Captured)
            try { foreach (var target in transformTargets) ApplyTargetRuntime(target); }
            catch { Cancel(); throw; }
        var consumed = Captured;
        if (Captured && !Gui.IsMouseDown(ImGuiMouseButton.Left))
        CompleteGroup();
        return consumed;
    }

    private void RebuildTransformTargets(IReadOnlyList<EntityHierarchyDocument> owners)
    {
        ownerVersions.Clear(); transformTargets.Clear(); transformOwnerRoots.Clear();
        selectionPolicyVersion = SelectionPolicyVersion?.Invoke() ?? 0;
        selectionContentVersion = SelectionContentVersion?.Invoke() ?? 0;
        foreach (var owner in owners)
        {
            ownerVersions[owner] = (owner.SelectionVersion, owner.StructureVersion);
            var roots = SelectionRoots?.Invoke(owner) ?? owner.GetSelectedTransformRoots();
            if (roots.Length == 0) continue;
            var resolved = new List<Entity>();
            foreach (var entity in roots)
                if ((ResolveRuntimeEntity == null ? entity : ResolveRuntimeEntity(owner, entity.Id)) is { } runtime)
                { transformTargets.Add(new(owner, entity, runtime)); resolved.Add(entity); }
            if (resolved.Count > 0) transformOwnerRoots.Add(owner, resolved.ToArray());
        }
        foreach (var owner in SelectionSceneRoots?.Invoke() ?? [])
            if (ResolveSceneOffsetTarget?.Invoke(owner) is { } root) transformTargets.Add(new(owner, null, root.Anchor, root));
        modifiedEntities = transformTargets.Select(target => target.Runtime).ToArray();
    }

    private bool CanTransformTargets() => transformTargets.Count > 0
        && transformOwnerRoots.All(pair => pair.Key.CanTransformSubset(pair.Value) && CanEditDocument?.Invoke(pair.Key) != false)
        && transformTargets.Where(target => target.SceneRoot != null).All(target => target.Owner.CanEditSceneOffset(target.Owner.SceneOffsetNode)
            && CanEditDocument?.Invoke(target.Owner) != false);

    private TransformTarget? ResolveTransformAnchor()
    {
        if (SelectionSceneRootPrimary?.Invoke() is { } root)
            foreach (var target in transformTargets) if (target.SceneRoot != null && ReferenceEquals(target.Owner, root)) return target;
        if (SelectionPrimary?.Invoke() is { } primary)
            foreach (var target in transformTargets)
                if (ReferenceEquals(target.Owner, primary.Document) && ReferenceEquals(target.Authored, primary.Entity)) return target;
        return transformTargets.Count > 0 ? transformTargets[^1] : null;
    }

    public void Cancel()
    {
        leftDown = false;
        foreach (var gizmo in gizmos) gizmo.CancelTransformation();
        if (gestureEntity != null)
        {
            foreach (var owner in gestureOwners) owner.CompleteMultiTransformGroup = null;
            foreach (var owner in gestureOwners) { owner.CancelMultiTransformEdit(); owner.CancelSceneOffsetEdit(); }
            foreach (var target in transformTargets) RestoreTargetRuntime(target);
            gestureEntity = null;
            gestureOwners = [];
            sceneAnchorStarts.Clear();
        }
    }
    private void CompleteGroup()
    {
        if (!Captured) return;
        if (!CanTransformTargets() || transformTargets.Any(TargetRuntimeChanged)) { Cancel(); return; }
        foreach (var owner in gestureOwners) owner.CompleteMultiTransformGroup = null;
        try { foreach (var target in transformTargets) ApplyTargetRuntime(target); }
        catch { Cancel(); throw; }
        // Capture every owner's intended engine result before a source update
        // propagates into another selected owner. Then remove all live drafts
        // before publishing their genuine Quantum changes in the shared group.
        foreach (var owner in gestureOwners) owner.PrepareMultiTransformCommit();
        var history = gestureOwners[0].SessionHistory;
        try
        {
            if (history == null && gestureOwners.Length == 1) gestureOwners[0].EndMultiTransformEdit();
            else
            {
                if (history == null || gestureOwners.Any(owner => !ReferenceEquals(owner.SessionHistory, history)))
                    throw new InvalidOperationException("Transforming entities across scenes requires the canonical shared session history.");
                using var transaction = history.CreateTransaction("Transform entities");
                if (history.IsCapturingOperations) FinishGestureTargets();
                else
                {
                    using var capture = history.CaptureOperationsAtomic(FinishGestureTargets);
                    capture.Publish(history, "Transform entities");
                }
            }
        }
        catch { Cancel(); throw; }
        gestureEntity = null; gestureOwners = []; sceneAnchorStarts.Clear(); leftDown = false;
    }
    public void Hide()
    {
        Cancel();
        foreach (var gizmo in gizmos) gizmo.IsEnabled = false;
    }
    public void Dispose()
    {
        if (!hiddenForDisposal) { Hide(); hiddenForDisposal = true; }
        while (disposedGizmos < gizmos.Length) { gizmos[disposedGizmos].Dispose(); disposedGizmos++; }
        scene.Parent?.Children.Remove(scene);
    }
    private bool hiddenForDisposal;
    private int disposedGizmos;
    private sealed class GizmoStageSelector(HashSet<ModelComponent> models, RenderStage opaque, RenderStage transparent) : RenderStageSelector
    {
        public override void Process(RenderObject renderObject)
        {
            if (renderObject is not RenderMesh mesh || !models.Contains(mesh.Source)) return;
            Array.Clear(renderObject.ActiveRenderStages);
            var stage = mesh.MaterialPass.HasTransparency ? transparent : opaque;
            renderObject.ActiveRenderStages[stage.Index] = new ActiveRenderStage("StrideEditorForwardShadingEffect");
        }
    }
}



