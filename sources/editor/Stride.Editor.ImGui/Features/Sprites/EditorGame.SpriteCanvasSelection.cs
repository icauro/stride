using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;
using NVector2 = System.Numerics.Vector2;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawSpriteCanvasSelectionPrompt()
    {
        var text = EditorLocalization.Text("Select a single image from the left panel");
        var area = Gui.GetContentRegionAvail(); var origin = Gui.GetCursorPos();
        var bounds = MeasureNativeLabel(text);
        Gui.SetCursorPos(origin + NVector2.Max(NVector2.Zero, (area - bounds) * .5f));
        DrawNativeLabel(text);
    }
}
