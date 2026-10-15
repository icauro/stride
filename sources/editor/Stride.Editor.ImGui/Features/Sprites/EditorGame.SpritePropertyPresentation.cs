namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private static string SpritePropertyLabel(AssetMetadataDocument document, SpriteSettingField field)
        => document.ExtensionPropertyLabel(field.Node) is { } presented
            ? Stride.Core.Presentation.Core.Utils.SplitCamelCase(presented)
            : EditorLocalization.Text(field.Label, "Property");
    private void DrawSpritePropertyLabel(AssetMetadataDocument document, SpriteSettingField field)
    {
        DrawNativeLabel(SpritePropertyLabel(document, field));
        PropertyDocumentation.Show(field.Node, document.AuthoredAsset.GetType());
    }
}
