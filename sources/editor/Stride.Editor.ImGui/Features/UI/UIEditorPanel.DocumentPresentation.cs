namespace Stride.Editor.ImGui;

internal sealed partial class UIEditorPanel
{
    internal void HideDocumentPresentation()
    {
        ResetVisibility();
        StopCameraInput();
        CancelCanvasPointer();
        StopLayoutDrag();
    }
}
