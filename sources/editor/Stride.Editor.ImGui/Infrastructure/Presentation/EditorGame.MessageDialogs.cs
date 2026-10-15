using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;
using Vector2 = System.Numerics.Vector2;

namespace Stride.Editor.ImGui;

internal enum EditorMessageDialogImage { None, Information, Error, Question }

internal sealed partial class EditorGame
{
    private void DrawMessageDialogBody(string id, string text, EditorMessageDialogImage image)
    {
        Gui.BeginGroup();
        try
        {
            if (icons != null && image != EditorMessageDialogImage.None)
            {
                // These are the exact ThemeSelector ImageInformationDialog/ImageErrorDialog resources.
                // EditorIcons retains their textures through the existing renderer/device generation.
                var key = image switch
                {
                    EditorMessageDialogImage.Information => "dialog-information",
                    EditorMessageDialogImage.Error => "dialog-error",
                    EditorMessageDialogImage.Question => "dialog-question",
                    _ => throw new ArgumentOutOfRangeException(nameof(image)),
                };
                var size = icons.Size(key) * Gui.GetStyle().FontScaleDpi;
                var margin = new Vector2(UiScale(5));
                var origin = Gui.GetCursorScreenPos();
                Gui.Dummy(size + margin * 2);
                Gui.GetWindowDrawList().AddImage(icons.Get(key), origin + margin, origin + margin + size);
                Gui.SameLine(0, 0);
            }
            DrawSelectableText(id, text);
        }
        finally { Gui.EndGroup(); }
    }
}
