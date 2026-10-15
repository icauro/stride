using NVector2 = System.Numerics.Vector2;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private bool TryHandleAssetBrowserFileDrop(string[] files, NVector2 screenPosition)
        => assetBrowserPanel?.TryHandleFileDrop(files, screenPosition) == true;
}
