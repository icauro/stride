using Stride.Core.Presentation.Quantum.Presenters;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawNativeVirtualFlagActions(VirtualNodePresenter presenter, bool editable, Func<object?> read, Action<object?> assign)
    {
        var type = Nullable.GetUnderlyingType(presenter.Type) ?? presenter.Type;
        if (!type.IsEnum || !type.IsDefined(typeof(FlagsAttribute), true)) return;
        Gui.BeginDisabled(!editable);
        try
        {
            if (DrawFlagBulkActions() is { } action)
                Execute(() => assign(ApplyFlagBulkAction((Enum)(read() ?? Enum.ToObject(type, 0)), action)));
        }
        finally { Gui.EndDisabled(); }
    }
}
