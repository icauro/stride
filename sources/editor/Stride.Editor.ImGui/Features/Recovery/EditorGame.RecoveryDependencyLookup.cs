namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private bool TryReadNewAssetRecoveryDependency(EditorRecoveryOpenDocument descriptor, out EditorRecoveryRecord? record)
    {
        record = null;
        return ownedRecovery?.TryReadNewAssetDependency(descriptor, out record) == true;
    }
}
