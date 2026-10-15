using Stride.Engine;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Rendering.Compositing;

namespace Stride.Editor.ImGui;

/// <summary>Owns native editor shader bindings until their real scene GPU generation retires.</summary>
internal sealed class SceneMeshEffects : IDisposable
{
    private readonly MeshFallbackEffects fallback;
    private readonly List<Binding> bindings = [];
    private readonly RootEffectRenderFeature.ComputeFallbackEffectDelegate compute;

    internal SceneMeshEffects(Game game, GraphicsCompositor compositor)
    {
        fallback = new MeshFallbackEffects(game.GraphicsDevice, game.EffectSystem);
        compute = ComputeFallback;
        try
        {
            foreach (var mesh in compositor.RenderFeatures.OfType<MeshRenderFeature>())
            {
                var selector = new EditorMeshRenderStageSelector();
                bindings.Add(new Binding(mesh, selector, mesh.ComputeFallbackEffect));
                mesh.RenderStageSelectors.Add(selector);
                mesh.ComputeFallbackEffect = compute;
            }
        }
        catch { Dispose(); throw; }
    }

    private Effect ComputeFallback(RenderObject renderObject, RenderEffect effect, RenderEffectState state)
        => fallback.ComputeFallbackEffect(renderObject, effect, state, EditorMeshEffects.SetFallbackParameters);

    public void Dispose()
    {
        // The scene generation's certificate authorizes this detach; loaded effects remain
        // borrowed from the actual EffectSystem and must not be disposed by this adapter.
        while (bindings.Count != 0)
        {
            var binding = bindings[0];
            binding.Feature.RenderStageSelectors.Remove(binding.Selector);
            if (binding.Feature.ComputeFallbackEffect == compute) binding.Feature.ComputeFallbackEffect = binding.Previous;
            bindings.RemoveAt(0);
        }
    }

    private sealed record Binding(MeshRenderFeature Feature, EditorMeshRenderStageSelector Selector,
        RootEffectRenderFeature.ComputeFallbackEffectDelegate? Previous);
}
