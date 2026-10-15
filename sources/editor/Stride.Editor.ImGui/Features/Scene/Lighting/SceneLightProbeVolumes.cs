using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Rendering.Compositing;
using Stride.Rendering.LightProbes;
using Stride.Rendering.Materials;
using Stride.Rendering.Materials.ComputeColors;

namespace Stride.Editor.ImGui;

// The native EditorGameLightProbeGizmoService face mesh, driven by the actual processor data.
internal sealed class SceneLightProbeVolumes : IDisposable
{
    private readonly GraphicsDevice device;
    private readonly Game game;
    private readonly Scene scene;
    private readonly Entity root = new("LightProbe Wireframe");
    private readonly Material material;
    private LightProbeVolumeResources? resources;
    private bool disposed;
    private LightProbeRuntimeData? current;
    private ModelComponent? model;
    private bool visible = true;
    internal bool Visible { get => visible; set { visible = value; if (model != null) model.Enabled = value; } }

    internal SceneLightProbeVolumes(Game game, Scene scene, GraphicsCompositor compositor)
    {
        this.game = game; device = game.GraphicsDevice; this.scene = scene;
        material = Material.New(device, new MaterialDescriptor
        {
            Attributes =
            {
                Diffuse = new MaterialDiffuseMapFeature(new ComputeColor()),
                DiffuseModel = new MaterialDiffuseLambertModelFeature(),
                Emissive = new MaterialEmissiveMapFeature(new ComputeColor()) { UseAlpha = true },
            },
        });
        var nativeColor = Color.Yellow; nativeColor.A = 0x9F;
        var color = new Color4(nativeColor).ToColorSpace(device.ColorSpace);
        material.Passes[0].Parameters.Set(MaterialKeys.DiffuseValue, color);
        material.Passes[0].Parameters.Set(MaterialKeys.EmissiveIntensity, 1f);
        material.Passes[0].Parameters.Set(MaterialKeys.EmissiveValue, color);
        material.Passes[0].HasTransparency = true;
        var stage = new RenderStage("LightProbe Wireframe", "Main");
        compositor.RenderStages.Add(stage);
        foreach (var mesh in compositor.RenderFeatures.OfType<MeshRenderFeature>())
        {
            mesh.RenderStageSelectors.Add(new SimpleGroupToRenderStageSelector
            { EffectName = "StrideEditorForwardShadingEffect", RenderGroup = RenderGroupMask.Group29, RenderStage = stage });
            mesh.PipelineProcessors.Add(new AntiAliasLinePipelineProcessor { RenderStage = stage });
        }
        ((EditorTopLevelCompositor)compositor.Game).PostGizmoCompositors.Add(new SingleStageRenderer { RenderStage = stage });
        scene.Entities.Add(root);
    }

    internal unsafe void Synchronize(SceneInstance instance)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!ReferenceEquals(device, game.GraphicsDevice)) throw new InvalidOperationException("The light probe volume device changed before synchronization.");
        scene.UpdateWorldMatrix();
        var offset = -scene.WorldMatrix.TranslationVector;
        var processor = instance.GetProcessor<LightProbeProcessor>();
        var data = processor?.VisibilityGroup.Tags.Get(LightProbeRenderer.CurrentLightProbes);
        if (data == null) { Clear(); current = null; root.Transform.Position = offset; return; }
        var changed = !ReferenceEquals(current, data);
        var positions = false; var coefficients = false;
        if (!changed)
            for (var index = 0; index < data.LightProbes.Length; index++)
            {
                if (data.LightProbes[index] is not LightProbeComponent probe) continue;
                positions |= probe.Entity.Transform.WorldMatrix.TranslationVector != data.Vertices[index];
                for (var coefficient = 0; coefficient < LightProbeGenerator.LambertHamonicOrder * LightProbeGenerator.LambertHamonicOrder; coefficient++)
                {
                    var value = probe.Coefficients != null && coefficient < probe.Coefficients.Count ? probe.Coefficients[coefficient] : default;
                    coefficients |= value != data.Coefficients[index * LightProbeGenerator.LambertHamonicOrder * LightProbeGenerator.LambertHamonicOrder + coefficient];
                }
            }
        if (positions)
        {
            processor!.UpdateLightProbePositions(); changed = true;
            data = processor.VisibilityGroup.Tags.Get(LightProbeRenderer.CurrentLightProbes);
            if (data == null) { Clear(); current = null; root.Transform.Position = offset; return; }
        }
        else if (coefficients) processor!.UpdateLightProbeCoefficients();
        if (!changed) { root.Transform.Position = offset; return; }
        if (data.Tetrahedra.Count == 0 || data.LightProbes.Length == 0)
        { Clear(); current = data; root.Transform.Position = offset; return; }
        var vertices = new VertexPositionNormalColor[data.LightProbes.Length];
        for (var index = 0; index < vertices.Length; index++) vertices[index] = new(data.Vertices[index], Vector3.Zero, Color.White);
        var indices = new List<int>(data.Faces.Count * 6);
        foreach (var face in data.Faces)
        {
            if (face.Vertices[0] >= data.UserVertexCount || face.Vertices[1] >= data.UserVertexCount || face.Vertices[2] >= data.UserVertexCount) continue;
            indices.Add(face.Vertices[0]); indices.Add(face.Vertices[1]);
            indices.Add(face.Vertices[1]); indices.Add(face.Vertices[2]);
            indices.Add(face.Vertices[2]); indices.Add(face.Vertices[0]);
        }
        if (indices.Count == 0) { Clear(); current = data; root.Transform.Position = offset; return; }
        var next = new LightProbeVolumeResources(game);
        ModelComponent candidate;
        try
        {
            var vertexBuffer = Stride.Graphics.Buffer.Vertex.New(device, vertices); next.Retain(vertexBuffer);
            var indexBuffer = Stride.Graphics.Buffer.Index.New(device, indices.ToArray()); next.Retain(indexBuffer);
            var draw = new MeshDraw
            {
                VertexBuffers = [new VertexBufferBinding(vertexBuffer, vertices[0].GetLayout(), vertices.Length)],
                IndexBuffer = new IndexBufferBinding(indexBuffer, true, indices.Count),
                DrawCount = indices.Count, PrimitiveType = PrimitiveType.LineList,
            };
            var bounds = BoundingBox.Empty;
            for (var index = 0; index < vertices.Length; index++) BoundingBox.Merge(ref bounds, ref vertices[index].Position, out bounds);
            candidate = new(new Model { material, new Mesh { Draw = draw, BoundingBox = bounds, BoundingSphere = BoundingSphere.FromBox(bounds) } })
            { RenderGroup = RenderGroup.Group29, Enabled = visible };
        }
        catch { next.Retire(); throw; }
        var previous = model; var previousPosition = root.Transform.Position;
        try
        {
            if (previous != null) root.Remove(previous);
            root.Add(candidate); root.Transform.Position = offset;
            resources?.Retire();
        }
        catch (Exception failure)
        {
            try { root.Remove(candidate); if (previous != null && !root.Components.Contains(previous)) root.Add(previous); root.Transform.Position = previousPosition; }
            catch (Exception recovery) { throw new AggregateException("Light probe volume replacement and restoration failed.", failure, recovery); }
            finally { next.Retire(); }
            throw;
        }
        resources = next; model = candidate; current = data;
    }

    private void Clear()
    {
        if (model != null) root.Remove(model);
        model = null;
        resources?.Retire(); resources = null;
    }
    public void Dispose() { if (disposed) return; Clear(); scene.Entities.Remove(root); current = null; disposed = true; }
}
