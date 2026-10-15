using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal readonly record struct EditorTreeRowState(uint WindowId, uint RowId, int Frame);

/// <summary>Managed row identity; native storage is resolved anew for each action.</summary>
internal static class EditorTreeStateAdapter
{
    public static EditorTreeRowState RememberCurrentRow(string label)
    {
        var window = ImGuiP.GetCurrentWindowRead();
        return new EditorTreeRowState(window.IsNull ? 0 : window.ID, Gui.GetID(label), Gui.GetFrameCount());
    }

    public static unsafe bool TrySetExpanded(EditorTreeRowState row, bool expanded, out bool actual)
    {
        actual = false;
        if (row.WindowId == 0 || Gui.GetFrameCount() - row.Frame is < 0 or > 1) return false;
        var window = ImGuiP.FindWindowByID(row.WindowId);
        if (window.IsNull || window.Hidden || window.LastFrameActive < row.Frame) return false;
        var storage = new ImGuiStoragePtr(window.Handle->DC.StateStorage);
        if (storage.IsNull) return false;
        Gui.SetInt(storage, row.RowId, expanded ? 1 : 0);
        actual = Gui.GetInt(storage, row.RowId, 0) != 0;
        return true;
    }
}
