using Stride.Assets.Materials;

namespace Stride.Editor.ImGui;

internal partial class AssetMetadataDocument
{
    internal EditorPresenterTarget? AcquireMaterialRootPresenter()
    {
        var presentation = ExtensionAssetPresentation;
        return MaterialSettings == null ? null : presentation?.AcquireTarget(presentation.RootPresenter);
    }
    internal EditorPresenterTarget? AcquireMaterialAttributesPresenter()
        => MaterialSettings == null ? null : AcquireExtensionCommandTarget(AuthoringGraph.RootNode[nameof(MaterialAsset.Attributes)]);
}
