using Stride.Assets.Materials;
using Stride.Core.Assets;
using Stride.Core.Serialization.Contents;
using Stride.Graphics;
using Stride.Rendering.Materials;

namespace Stride.Editor.ImGui;

/// <summary>Runs the native authored material generation stage for an editor preview.</summary>
internal static class MaterialPreviewGeneration
{
    public static MaterialShaderResult Generate(MaterialAsset asset, IAssetFinder finder, ContentManager content,
        GraphicsProfile graphicsProfile, ColorSpace colorSpace, string friendlyName)
    {
        var context = new MaterialGeneratorContext { GraphicsProfile = graphicsProfile, Content = content, ColorSpace = colorSpace };
        context.AddLoadingFromSession(finder);
        var clone = AssetCloner.Clone(asset);
        return MaterialGenerator.Generate(new MaterialDescriptor { MaterialId = clone.Id, Attributes = clone.Attributes, Layers = clone.Layers }, context, friendlyName);
    }
}
