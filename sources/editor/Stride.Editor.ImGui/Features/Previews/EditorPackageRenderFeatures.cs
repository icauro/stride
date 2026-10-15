using Stride.Assets.Rendering;
using Stride.Core.Assets;
using Stride.Rendering;
using Stride.Rendering.Compositing;

namespace Stride.Editor.ImGui;

/// <summary>Uses package render providers without loading their WPF editor assemblies.</summary>
internal static class EditorPackageRenderFeatures
{
    internal static void Add(GraphicsCompositor compositor, RenderStage opaque, RenderStage transparent, PackageTypeScope? scope = null)
    {
        var present = compositor.RenderFeatures.Select(feature => feature.GetType()).ToHashSet();
        foreach (var provider in RenderFeatureProviders.Enumerate(scope).Concat(LinkedProviders(scope)))
        {
            foreach (var feature in provider.CreateRenderFeatures(opaque, transparent))
            {
                if (present.Add(feature.GetType())) compositor.RenderFeatures.Add(feature);
                else feature.Dispose();
            }
        }
    }

    private static IEnumerable<IRenderFeatureProvider> LinkedProviders(PackageTypeScope? scope)
    {
        if (scope == null || scope.Contains(typeof(Stride.Engine.UIComponent)))
            yield return new Stride.UI.Editor.UIRenderFeatureProvider();
        if (scope == null || scope.Contains(typeof(Stride.Particles.Components.ParticleSystemComponent)))
            yield return new Stride.Particles.Editor.ParticleRenderFeatureProvider();
        if (scope == null || scope.Contains(typeof(Stride.Engine.SpriteStudioComponent)))
            yield return new Stride.SpriteStudio.Editor.SpriteStudioRenderFeatureProvider();
    }
}
