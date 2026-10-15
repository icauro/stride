using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Extensions;
using Stride.Graphics;
using Stride.Graphics.GeometricPrimitives;
using Stride.Navigation;
using Stride.Rendering;
using Stride.Rendering.Materials;
using Stride.Rendering.Materials.ComputeColors;

namespace Stride.Editor.ImGui;

// Native EditorGameNavigationMeshService tile geometry and updated-tile highlight policy.
internal sealed class SceneNavigationVisual : IDisposable
{
    internal sealed class Group(NavigationMeshGroup source, int index, GraphicsDevice device)
    {
        internal readonly Guid Id = source.Id;
        internal readonly string Name = source.ToString();
        internal readonly int Index = index;
        internal bool Visible;
        internal readonly Color4 Color = new ColorHSV((index * 80f + 90f) % 360f, .95f, .75f, 1f).ToColor();
        internal readonly Material Material = CreateMaterial(device, new ColorHSV((index * 80f + 90f) % 360f, .95f, .75f, 1f).ToColor());
        internal readonly Material Highlight = CreateMaterial(device, new ColorHSV((index * 80f + 90f) % 360f, .95f, .75f, 1f).ToColor());
        internal float Remaining;
    }
    private readonly GraphicsDevice device;
    private readonly Game game;
    private readonly Scene scene;
    private readonly Entity root = new("Debug entity for navigation mesh");
    private NavigationGeometryResources? geometry;
    private readonly Dictionary<Guid, ModelComponent> models = [];
    private readonly Dictionary<Guid, Group> groups = [];
    private bool suppressed;
    internal IReadOnlyCollection<Group> Groups => groups.Values;
    internal SceneNavigationVisual(Game game, Scene scene)
    { this.game = game; device = game.GraphicsDevice; this.scene = scene; scene.Entities.Add(root); }
    internal void Detach() => scene.Entities.Remove(root);
    internal void Attach() { if (!scene.Entities.Contains(root)) scene.Entities.Add(root); }

    internal void Configure(IReadOnlyList<NavigationMeshGroup> settings, IReadOnlySet<Guid>? visible)
    {
        var previous = groups.Values.Where(group => group.Visible).Select(group => group.Id).ToHashSet();
        var next = new Dictionary<Guid, Group>();
        for (var index = 0; index < settings.Count; index++)
            if (settings[index] is { } source) next.Add(source.Id, new(source, index, device) { Visible = (visible ?? previous).Contains(source.Id) });
        groups.Clear(); foreach (var pair in next) groups.Add(pair.Key, pair.Value);
    }
    internal void SetVisible(Guid id, bool value)
    {
        if (!groups.TryGetValue(id, out var group)) return;
        group.Visible = value;
        if (models.TryGetValue(id, out var model)) model.Enabled = value && !suppressed;
    }
    internal void SuppressRendering(bool value)
    {
        suppressed = value;
        foreach (var pair in models) pair.Value.Enabled = !value && groups.TryGetValue(pair.Key, out var group) && group.Visible;
    }
    internal void Replace(NavigationMesh mesh, NavigationMesh? previous, Vector3 sceneOffset)
    {
        if (!ReferenceEquals(device, game.GraphicsDevice))
            throw new InvalidOperationException("The navigation geometry device changed before replacement.");
        var next = new NavigationGeometryResources(game);
        var nextModels = new Dictionary<Guid, ModelComponent>();
        var nextEntities = new List<Entity>();
        try
        {
        foreach (var layer in mesh.Layers)
        {
            if (!groups.TryGetValue(layer.Key, out var group)) continue;
            var model = new Model { group.Material, group.Highlight };
            foreach (var pair in layer.Value.Tiles)
            {
                var positions = new List<Vector3>(); var indices = new List<int>();
                if (!pair.Value.GetTileVertices(positions, indices) || positions.Count == 0 || indices.Count == 0) continue;
                var changed = previous == null || !previous.Layers.TryGetValue(layer.Key, out var oldLayer)
                    || oldLayer.FindTile(pair.Key) is not { } oldTile || oldTile.Data != pair.Value.Data;
                var vertices = new VertexPositionNormalTexture[positions.Count];
                var bounds = BoundingBox.Empty;
                for (var index = 0; index < vertices.Length; index++)
                {
                    var position = positions[index] + new Vector3(0, .05f * group.Index, 0);
                    BoundingBox.Merge(ref bounds, ref position, out bounds);
                    vertices[index] = new(position, Vector3.UnitY, new Vector2(.5f, .5f));
                }
                using var data = new GeometricMeshData<VertexPositionNormalTexture>(vertices, indices.ToArray(), true);
                var primitive = new GeometricPrimitive(device, data); next.Retain(primitive);
                model.Add(new Mesh { Draw = primitive.ToMeshDraw(), MaterialIndex = changed ? 1 : 0, BoundingBox = bounds });
            }
            var component = new ModelComponent(model) { Enabled = group.Visible && !suppressed };
            var entity = new Entity($"Navigation group {layer.Key}") { component };
            nextEntities.Add(entity); nextModels.Add(layer.Key, component);
        }
        }
        catch { next.Retire(); throw; }
        var oldChildren = root.Transform.Children.ToArray(); var oldPosition = root.Transform.Position;
        try
        {
            root.Transform.Children.Clear();
            foreach (var entity in nextEntities) entity.Transform.Parent = root.Transform;
            root.Transform.Position = sceneOffset;
            geometry?.Retire();
        }
        catch (Exception failure)
        {
            try { root.Transform.Children.Clear(); foreach (var child in oldChildren) child.Parent = root.Transform; root.Transform.Position = oldPosition; }
            catch (Exception recovery) { throw new AggregateException("Navigation geometry replacement and restoration failed.", failure, recovery); }
            finally { next.Retire(); }
            throw;
        }
        geometry = next; models.Clear();
        foreach (var pair in nextModels) { models.Add(pair.Key, pair.Value); groups[pair.Key].Remaining = 1f; }
    }
    internal void Update(float elapsed)
    {
        foreach (var group in groups.Values)
        {
            if (group.Remaining <= 0) continue;
            group.Remaining = Math.Max(0, group.Remaining - elapsed);
            var color = Color4.Lerp(group.Color, new Color4(2f, .1f, .1f, 1f), group.Remaining).ToColorSpace(device.ColorSpace); color.A = .33f;
            group.Highlight.Passes[0].Parameters.Set(MaterialKeys.DiffuseValue, color);
            group.Highlight.Passes[0].Parameters.Set(MaterialKeys.EmissiveValue, color);
            group.Highlight.Passes[0].Parameters.Set(MaterialKeys.EmissiveIntensity, 1f);
        }
    }
    private static Material CreateMaterial(GraphicsDevice device, Color4 color)
    {
        var material = Material.New(device, new MaterialDescriptor
        {
            Attributes =
            {
                Diffuse = new MaterialDiffuseMapFeature(new ComputeColor()),
                DiffuseModel = new MaterialDiffuseLambertModelFeature(),
                Emissive = new MaterialEmissiveMapFeature(new ComputeColor()) { UseAlpha = true },
            },
        });
        color = color.ToColorSpace(device.ColorSpace); color.A = .33f;
        material.Passes[0].Parameters.Set(MaterialKeys.DiffuseValue, color);
        material.Passes[0].Parameters.Set(MaterialKeys.EmissiveValue, color);
        material.Passes[0].Parameters.Set(MaterialKeys.EmissiveIntensity, 1f);
        material.Passes[0].HasTransparency = true;
        return material;
    }
    private void Clear()
    {
        geometry?.Retire(); geometry = null;
        root.Transform.Children.Clear(); models.Clear();
    }
    public void Dispose() { Clear(); scene.Entities.Remove(root); groups.Clear(); }
}
