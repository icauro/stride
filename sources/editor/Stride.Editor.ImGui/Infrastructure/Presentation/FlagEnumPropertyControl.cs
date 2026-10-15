using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal static class FlagEnumPropertyControl
{
    internal static FlagBulkAction? DrawBulkActions(Action<string>? capture = null)
    {
        Gui.Separator();
        FlagBulkAction? action = null;
        if (Gui.SmallButton(EditorLocalization.Widget("All", "Button"))) action = FlagBulkAction.All;
        capture?.Invoke("all");
        Gui.SameLine();
        if (Gui.SmallButton(EditorLocalization.Widget("None", "Button"))) action = FlagBulkAction.None;
        capture?.Invoke("none");
        Gui.SameLine();
        if (Gui.SmallButton(EditorLocalization.Widget("Invert", "Button"))) action = FlagBulkAction.Invert;
        capture?.Invoke("invert");
        return action;
    }
}
