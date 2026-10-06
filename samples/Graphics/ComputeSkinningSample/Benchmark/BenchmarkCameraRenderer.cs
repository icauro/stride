using Stride.Engine;
using Stride.Rendering;
using Stride.Rendering.Compositing;

sealed class BenchmarkCameraRenderer(bool disableCulling) : SceneCameraRenderer
{
    public int VisibleEntities { get; private set; }
    public string[] CulledEntities { get; private set; } = [];

    protected override void CollectInner(RenderContext context)
    {
        if (disableCulling) RenderView.CullingMode = CameraCullingMode.None;
        base.CollectInner(context);
    }

    protected override void DrawInner(RenderDrawContext context)
    {
        bool IsBenchmark(RenderMesh mesh) => mesh.Source is ModelComponent model && model.Entity.Name.StartsWith("Morph benchmark ", StringComparison.Ordinal);
        var visible = RenderView.RenderObjects.OfType<RenderMesh>().Where(IsBenchmark).Select(mesh => mesh.Source).Distinct().ToHashSet();
        VisibleEntities = visible.Count;
        CulledEntities = context.RenderContext.VisibilityGroup.RenderObjects.OfType<RenderMesh>().Where(IsBenchmark)
            .Where(mesh => !visible.Contains(mesh.Source)).Select(mesh => ((ModelComponent)mesh.Source).Entity.Name).Distinct().ToArray();
        base.DrawInner(context);
    }
}
