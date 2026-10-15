using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed class NativeNullableRow : IDisposable
{
    internal bool Active { get; private set; }
    private static readonly NativeNullableRow empty = new("", false);
    internal static NativeNullableRow Begin(string identity, bool enabled) => enabled ? new(identity, true) : empty;
    internal NativeNullableRow(string identity, bool enabled)
    {
        Active = enabled && Gui.BeginTable(identity, 2, ImGuiTableFlags.SizingStretchProp,
            new System.Numerics.Vector2(PropertyGridRows.AvailableWidth(), 0));
        if (!Active) return;
        Gui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthStretch, 1f);
        var style = Gui.GetStyle();
        Gui.TableSetupColumn("Command", ImGuiTableColumnFlags.WidthFixed, 16 * style.FontScaleDpi + 2 * style.FramePadding.X);
        Gui.TableNextRow(); Gui.TableSetColumnIndex(1);
    }
    internal void ValueColumn() { if (Active) { Gui.TableSetColumnIndex(0); Gui.SetNextItemWidth(-1); } }
    public void Dispose() { if (Active) { Gui.EndTable(); Active = false; } }
}
