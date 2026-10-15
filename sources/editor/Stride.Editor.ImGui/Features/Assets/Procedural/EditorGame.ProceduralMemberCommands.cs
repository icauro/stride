using Stride.Core.Quantum;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawProceduralMemberPresenterCommands(ProceduralAssetDocument asset, IMemberNode member)
    {
        bool Current() => ReferenceEquals(assetDocument, asset) && asset.CanCopyProperty(member)
            && sessionHistory?.Registry.Find(asset.Asset.Id) is { IsPublished: true, IsDisposed: false } state
            && ReferenceEquals(state.Document, asset);
        DrawNativePresenterCommands(asset, Current,
            () => AcquireHierarchyFactoryTargets(1, _ => asset.AcquireExtensionCommandTarget(member)),
            () => new EditorAssetCommandMutation(asset.BeginExtensionCommandMutation()));
    }
}
