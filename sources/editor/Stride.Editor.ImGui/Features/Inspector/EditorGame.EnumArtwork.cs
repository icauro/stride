namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private bool DrawComponentEnumArtwork(ComponentField field, object? value, bool mixed, Action<object?> commit)
    {
        if (field.IsNullable || icons?.HasEnumImages(field.Type) != true) return false;
        if (NativeEnumImageControl.Draw(icons, field.Node, field.Type, value, mixed,
            field.EnumOptions.Select(option => (Enum)option.Value), UiScale(1), out var updated)) commit(updated);
        return true;
    }
}
