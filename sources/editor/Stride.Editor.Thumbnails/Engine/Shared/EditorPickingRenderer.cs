using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Rendering.Compositing;

namespace Stride.Engine.Design;

public enum EditorPickingPurpose { Selection, MaterialHover, MaterialClick }

/// <summary>On-demand mesh ID/depth picking through the actual scene render view.</summary>
public sealed class EditorPickingRenderer : SceneRendererBase
{
    private readonly Dictionary<int, Entity> entities = new();
    private readonly EditorPickingData[] readback = new EditorPickingData[1];
    private Texture staging;
    private Vector2 position;
    private bool pending;
    private int attempts;
    private Matrix requestedView;
    private Vector2 requestedSize;
    private readonly HashSet<Entity> cycleExcluded = new();
    private readonly CandidateFilter candidateFilter;
    private RenderStage pickingRenderStage;
    private RenderStageFilter originalStageFilter;
    private bool cycling, cycleQueryValid, cycleWrapped;
    private Vector2 cyclePosition, cycleSize;
    private Matrix cycleView;
    private Entity lastCycleResult;
    private long lastCycleRequestTimestamp;
    public EditorPickingRenderer() { candidateFilter = new CandidateFilter(this); }
    public Matrix DisplayedViewProjection { get; private set; }
    public Vector2 DisplayedSize { get; private set; }
    public int CancelledRequests { get; private set; }
    public string LastCancellation { get; private set; }
    public RenderStage PickingRenderStage
    {
        get => pickingRenderStage;
        set
        {
            if (pickingRenderStage == value) return;
            if (pickingRenderStage?.Filter == candidateFilter) pickingRenderStage.Filter = originalStageFilter;
            pickingRenderStage = value;
            originalStageFilter = value?.Filter;
            if (value != null) value.Filter = candidateFilter;
        }
    }
    public Func<Entity, bool> IsPickable { get; set; }
    public int CycleStep => cycleExcluded.Count;
    public bool HasResult { get; private set; }
    public bool IsPending => pending;
    public Entity Result { get; private set; }
    public int ComponentId { get; private set; }
    public int MeshNodeIndex { get; private set; } = -1;
    public int MaterialIndex { get; private set; } = -1;
    public int InstanceId { get; private set; } = -1;
    public EditorPickingPurpose Purpose { get; private set; }
    public string Error { get; private set; }
    public int ReadbackCount { get; private set; }
    public double LastReadbackMilliseconds { get; private set; }
    public void CacheEntity(Entity entity)
    {
        foreach (var component in entity.Components) entities[RuntimeIdHelper.ToRuntimeId(component)] = entity;
    }
    public void CacheGizmo(Entity root, Entity contentEntity)
    {
        foreach (var component in root.Components) CacheGizmoComponent(component, contentEntity);
        foreach (var child in root.Transform.Children) CacheGizmo(child.Entity, contentEntity);
    }
    public void CacheGizmoComponent(EntityComponent component, Entity contentEntity) => entities[RuntimeIdHelper.ToRuntimeId(component)] = contentEntity;
    public void ForgetGizmoComponent(EntityComponent component) => entities.Remove(RuntimeIdHelper.ToRuntimeId(component));
    public void ForgetGizmo(Entity root)
    {
        foreach (var component in root.Components) entities.Remove(RuntimeIdHelper.ToRuntimeId(component));
        foreach (var child in root.Transform.Children) ForgetGizmo(child.Entity);
    }
    public void Request(Vector2 normalizedPosition, Matrix view, Vector2 viewSize, bool cycle = false,
        EditorPickingPurpose purpose = EditorPickingPurpose.Selection)
    {
        if (!float.IsFinite(normalizedPosition.X) || !float.IsFinite(normalizedPosition.Y)
            || normalizedPosition.X < 0 || normalizedPosition.X >= 1 || normalizedPosition.Y < 0 || normalizedPosition.Y >= 1)
            throw new ArgumentOutOfRangeException(nameof(normalizedPosition));
        if (!float.IsFinite(viewSize.X) || !float.IsFinite(viewSize.Y) || viewSize.X <= 0 || viewSize.Y <= 0)
            throw new ArgumentOutOfRangeException(nameof(viewSize));
        var offset = (normalizedPosition - cyclePosition) * viewSize;
        var continuing = cycle && cycleQueryValid && cycleView == view && cycleSize == viewSize && offset.LengthSquared() <= 9
            && Stopwatch.GetElapsedTime(lastCycleRequestTimestamp).TotalSeconds <= 2;
        if (!continuing) ResetCycle();
        else if (lastCycleResult != null) cycleExcluded.Add(lastCycleResult);
        CancelRequest("Superseded by a new click", false); requestedView = view; requestedSize = viewSize;
        cycling = cycle; cycleQueryValid = cycle; cycleWrapped = false;
        cyclePosition = normalizedPosition; cycleSize = viewSize; cycleView = view;
        lastCycleRequestTimestamp = Stopwatch.GetTimestamp();
        position = normalizedPosition; pending = true; HasResult = false; Error = null; attempts = 0;
        Purpose = purpose; ComponentId = 0; MeshNodeIndex = MaterialIndex = InstanceId = -1;
    }
    public void Cancel(string reason)
    {
        CancelRequest(reason, true);
    }
    private void ResetCycle()
    {
        cycleExcluded.Clear(); lastCycleResult = null; cycleQueryValid = false; cycling = false; cycleWrapped = false;
    }
    private void CancelRequest(string reason, bool resetCycle)
    {
        if (resetCycle) ResetCycle();
        if (!pending && !HasResult) return;
        pending = HasResult = false; Result = null; Error = null;
        LastCancellation = reason; CancelledRequests++;
    }
    public void ValidateView(Matrix view, Vector2 viewSize)
    {
        if ((pending || HasResult || cycleQueryValid) && (view != requestedView || viewSize != requestedSize)) Cancel("Scene camera or viewport changed");
    }
    public void Consume() => HasResult = false;
    protected override void CollectCore(RenderContext context)
    {
        if (!pending) return;
        PickingRenderStage.Output = new RenderOutputDescription(PixelFormat.R32G32_Float, PixelFormat.D32_Float) { ScissorTestEnable = true };
        context.RenderView.RenderStages.Add(PickingRenderStage);
    }
    protected override void DrawCore(RenderContext context, RenderDrawContext drawContext)
    {
        DisplayedViewProjection = context.RenderView.ViewProjection; DisplayedSize = context.RenderView.ViewSize;
        ValidateView(DisplayedViewProjection, DisplayedSize);
        if (!pending) return;
        var effects = context.RenderSystem.RenderFeatures.OfType<MeshRenderFeature>().SelectMany(feature => feature.RenderNodes)
            .Select(node => node.RenderEffect).Where(effect => effect.EffectSelector.EffectName.EndsWith(".Picking", StringComparison.Ordinal));
        if (effects.Any(effect => effect.State != RenderEffectState.Normal || effect.Effect == null))
        {
            if (++attempts > 120) { Error = "Picking shaders did not become ready."; pending = false; HasResult = true; }
            return;
        }
        staging ??= Texture.New2D(drawContext.GraphicsDevice, 1, 1, PixelFormat.R32G32_Float, TextureFlags.None, 1, GraphicsResourceUsage.Staging);
        var width = Math.Max(1, (int)context.RenderView.ViewSize.X); var height = Math.Max(1, (int)context.RenderView.ViewSize.Y);
        var target = PushScopedResource(context.Allocator.GetTemporaryTexture2D(width, height, PixelFormat.R32G32_Float));
        var depth = PushScopedResource(context.Allocator.GetTemporaryTexture2D(width, height, PixelFormat.D32_Float, TextureFlags.DepthStencil));
        var x = Math.Clamp((int)(position.X * width), 0, width - 1); var y = Math.Clamp((int)(position.Y * height), 0, height - 1);
        using (drawContext.PushRenderTargetsAndRestore())
        {
            drawContext.CommandList.Clear(target, Color.Transparent);
            drawContext.CommandList.Clear(depth, DepthStencilClearOptions.DepthBuffer);
            drawContext.CommandList.ResourceBarrierTransition(target, BarrierLayout.RenderTarget);
            drawContext.CommandList.ResourceBarrierTransition(depth, BarrierLayout.DepthStencilWrite);
            drawContext.CommandList.SetRenderTargetAndViewport(depth, target);
            drawContext.CommandList.SetScissorRectangle(new Rectangle(x, y, 1, 1));
            context.RenderSystem.Draw(drawContext, context.RenderView, PickingRenderStage);
            drawContext.CommandList.SetScissorRectangle(new Rectangle());
        }
        drawContext.CommandList.CopyRegion(target, 0, new ResourceRegion(x, y, 0, x + 1, y + 1, 1), staging, 0);
        var started = Stopwatch.GetTimestamp(); staging.GetData(drawContext.CommandList, readback);
        LastReadbackMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds; ReadbackCount++;
        entities.TryGetValue((int)Math.Floor(readback[0].ModelComponentId), out var entity);
        if (entity != null && IsPickable?.Invoke(entity) == false) entity = null;
        if (cycling && entity == null && cycleExcluded.Count > 0 && !cycleWrapped)
        {
            cycleExcluded.Clear(); lastCycleResult = null; cycleWrapped = true; attempts = 0;
            return;
        }
        if (cycling) { lastCycleResult = entity; if (entity == null) cycleQueryValid = false; }
        Result = entity; HasResult = true; pending = false;
        ComponentId = entity == null ? 0 : (int)Math.Floor(readback[0].ModelComponentId);
        MeshNodeIndex = entity == null ? -1 : (int)Math.Floor(readback[0].MeshMaterialIndex);
        MaterialIndex = entity == null ? -1 : (int)Math.Round((readback[0].MeshMaterialIndex - Math.Floor(readback[0].MeshMaterialIndex)) * 1024);
        InstanceId = entity == null ? -1 : (int)Math.Round((readback[0].ModelComponentId - Math.Floor(readback[0].ModelComponentId)) * 1024);
    }
    private sealed class CandidateFilter(EditorPickingRenderer owner) : RenderStageFilter
    {
        public override bool IsVisible(RenderObject renderObject, RenderView renderView, RenderViewStage renderViewStage)
        {
            if (renderObject.Source is not EntityComponent component
                || !owner.entities.TryGetValue(RuntimeIdHelper.ToRuntimeId(component), out var entity)
                || owner.cycleExcluded.Contains(entity) || owner.IsPickable?.Invoke(entity) == false) return false;
            return owner.originalStageFilter?.IsVisible(renderObject, renderView, renderViewStage) != false;
        }
    }
    protected override void Destroy()
    {
        if (pickingRenderStage?.Filter == candidateFilter) pickingRenderStage.Filter = originalStageFilter;
        staging?.Dispose(); staging = null; entities.Clear(); ResetCycle(); base.Destroy();
    }
}
