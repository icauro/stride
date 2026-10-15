namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private nint GetCurrentDialogOwner()
        => platformViewports?.GetCurrentDialogOwner() ?? Window.NativeWindow.Handle;
}
