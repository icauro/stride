using Stride.Engine;
using Stride.Graphics;
using Stride.Graphics.GeometricPrimitives;

namespace Stride.Editor.ImGui;

/// <summary>Actual returned navigation primitives from one published geometry generation.</summary>
internal sealed class NavigationGeometryResources(Game game) : IDisposable
{
    private readonly EditorGame owner = game as EditorGame ?? throw new NotSupportedException("Navigation retirement requires the editor actor.");
    private readonly GraphicsDevice device = game.GraphicsDevice;
    private readonly List<GeometricPrimitive> primitives = [];
    internal void Retain(GeometricPrimitive primitive) => primitives.Add(primitive);
    internal void Retire() => owner.RetirePreviewTargets(this, device);
    public void Dispose()
    { while (primitives.Count != 0) { primitives[^1].Dispose(); primitives.RemoveAt(primitives.Count - 1); } }
}
