namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private long spriteSourceCleanupRetryAt;
    private string? spriteSourceCleanupFailure;
    private void PumpSpriteSourceCleanup()
        => TryCleanupEditorOwnedWork(() => { PumpSpriteSourceEdits(); PumpRetiredSpriteSourceWork(); },
            ref spriteSourceCleanupRetryAt, ref spriteSourceCleanupFailure, "Sprite source cleanup");
}
