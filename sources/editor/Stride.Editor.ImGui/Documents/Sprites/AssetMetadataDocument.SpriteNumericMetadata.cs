namespace Stride.Editor.ImGui;

internal partial class AssetMetadataDocument
{
    internal PropertyNumericRange? SpriteNumericRange(SpriteSettingField field)
        => ExtensionNumericRange(field.Node) ?? field.DeclarationNumericRange;
    internal IReadOnlyList<PropertyNumericRange?> SpriteAxisNumericRanges(SpriteSettingField field)
    {
        var effective = ExtensionAxisNumericRanges(field.Node);
        return effective.Count > 0 ? effective : field.DeclarationAxisRanges;
    }
    private object NormalizeSpriteSetting(SpriteSettingField field, object value, object? previous)
    {
        if (ExtensionAssetPresentation?.Find(field.Node) is { } presenter)
            NativeNumericAxes.AssertEditable(presenter, previous, value);
        value = PropertyValueValidation.Normalize(value, SpriteNumericRange(field))!;
        return NativeNumericAxes.Normalize(value, previous, SpriteAxisNumericRanges(field))!;
    }
}
