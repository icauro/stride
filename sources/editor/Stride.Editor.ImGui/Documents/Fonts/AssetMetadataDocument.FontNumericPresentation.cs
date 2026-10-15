namespace Stride.Editor.ImGui;

internal partial class AssetMetadataDocument
{
    internal PropertyNumericRange? FontNumericRange(FontSettingField field)
        => ExtensionNumericRange(field.Node) ?? (field.Type == typeof(decimal) ? null : AssetSettingNumericMetadata.Read(field.Node));
    private object? NormalizeFontSetting(FontSettingField field, object? value)
    {
        if (ExtensionAssetPresentation?.Find(field.Node) is { } presenter)
            NativeNumericAxes.AssertEditable(presenter, field.Node.Retrieve(), value);
        value = PropertyValueValidation.Normalize(value, FontNumericRange(field));
        return NativeNumericAxes.Normalize(value, field.Node.Retrieve(), ExtensionAxisNumericRanges(field.Node));
    }
}
