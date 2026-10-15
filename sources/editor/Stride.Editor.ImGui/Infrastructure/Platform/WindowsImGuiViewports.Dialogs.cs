using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed unsafe partial class WindowsImGuiViewports
{
    public nint GetCurrentDialogOwner()
    {
        var viewport = Gui.GetWindowViewport();
        return !viewport.IsNull && windows.TryGetValue(viewport.ID, out var window) ? window.Handle : mainWindow;
    }
}
