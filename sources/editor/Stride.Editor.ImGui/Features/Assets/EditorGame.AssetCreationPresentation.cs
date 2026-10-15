using System.Numerics;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void PaintAssetTemplateRow(AssetCreationPanel.Choice choice, Vector2 minimum, Vector2 maximum)
        => PaintTemplateRow(choice.Name, choice.Template, minimum, maximum);

    private void PaintDroppedAssetTemplateRow(AssetDropTemplatePanel.Entry entry, int total, Vector2 minimum, Vector2 maximum)
        => PaintTemplateRow(entry.Choice.Name, entry.Choice.Template, minimum, maximum,
            total > 0 ? entry.Files.Length + " " + string.Format(EditorLocalization.Text("out of {0} file(s)"), total) : null);
}
