namespace Stride.Editor.ImGui;

internal sealed partial class UIEditorPanel
{
    private bool TryDrawUIRangedProperty(UIElementProperty field, object? value, bool canEdit)
    {
        if (!AssetSettingNumericMetadata.Supports(field.Type)) return false;
        var source = document!; var ids = selected.ToArray();
        var fields = ids.Select(id => source.Properties(id).Single(other => other.Name == field.Name)).ToArray();
        PropertyNumericRange? Read(UIElementProperty other) => source.ExtensionNumericRange(other.Node) ?? other.NumericRange;
        var ranges = fields.Select(Read).ToArray();
        if (ranges.Any(range => !NativeRangedNumericControl.Matches(field.Type, range))) return false;
        DrawUIValue(field.Name, field.Label, field.Type, value, fields.Select(other => other.Node.Retrieve()).ToArray(), canEdit, field.AllowsNull,
            transform => source.TransformProperties(ids, field.Name, transform), ranges[0],
            numericRangeProvider: () => NativeNumericInput.Merge(field.Type, fields.Select(Read)), rangedRangesProvider: () => fields.Select(Read).ToArray(),
            valueTarget: MemberValueTarget(source, ids, field.Name));
        return true;
    }
}
