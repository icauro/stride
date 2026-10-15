using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawSpriteDiscreteSetting(AssetMetadataDocument owner, SpriteSettingField field)
        => DrawSpriteDiscreteValue(field, () => ReadSpriteSettingDraft(owner, field),
            () => CanEditSpriteSettingDraft(owner, field), () => AcquireSpriteSettingDraft(owner, field),
            value => CommitSpriteSettingDraft(owner, field, value!), SpritePropertyLabel(owner, field));

    private void DrawSpriteCommonDiscreteSetting(AssetMetadataDocument owner, SpriteCommonSetting setting)
        => DrawSpriteDiscreteValue(setting.First, () => ReadSpriteCommonNumericDraft(owner, setting),
            () => CanEditSpriteCommonNumericDraft(owner, setting), () => AcquireSpriteCommonNumericDraft(owner, setting), value =>
            {
                if (!CanEditSpriteCommonNumericDraft(owner, setting)) throw new InvalidOperationException("The sprite selection changed.");
                if (setting.Fields.All(field => Equals(field.Node.Retrieve(), value))) return;
                UseTextureSettingsCommandContext();
                owner.UpdateSpriteCommonValue(setting, _ => value!);
                owner.EndSpriteCommonEdit();
            }, SpritePropertyLabel(owner, setting.First), setting.Fields.Length);

    private void DrawSpriteDiscreteValue(SpriteSettingField field, Func<object?[]> read, Func<bool> editable,
        Func<IDisposable> acquire, Action<object?> commit, string label, int valueCount = 1)
    {
        void Apply(Func<object?, object?> create)
        {
            if (!editable()) throw new InvalidOperationException("The sprite property changed.");
            using var lease = new RetainedPathLease(this, acquire());
            commit(create(read()[0]));
        }
        if (TryDrawSpritePrimitive(field.Type, label, read, editable, Apply, acquire, valueCount)) return;
        var expected = read(); var value = expected[0];
        var mixed = expected.Take(valueCount).Any(other => !Equals(other, value));
        void Choose(object? selected)
        {
            Execute(() =>
            {
                if (!editable() || !expected.SequenceEqual(read())) throw new InvalidOperationException("The sprite property source changed.");
                if (!mixed && Equals(value, selected)) return;
                ValidatePendingPropertyStrings();
                if (!editable() || !expected.SequenceEqual(read())) throw new InvalidOperationException("The sprite property source changed.");
                Apply(_ => selected);
            });
        }
        using var disabled = ImGuiScopes.Disabled(!editable());
        if (field.Type == typeof(bool))
        {
            var enabled = !mixed && value is true;
            Gui.PushItemFlag((ImGuiItemFlags)ImGuiItemFlagsPrivate.MixedValue, mixed);
            try { if (DrawNativeCheckbox("##Value", ref enabled)) Choose(enabled); }
            finally { Gui.PopItemFlag(); }
        }
        else if (icons?.HasEnumImages(field.Type) == true)
        {
            if (NativeEnumImageControl.Draw(icons, field.Node, field.Type, value, mixed,
                    Enum.GetValues(field.Type).Cast<Enum>(), UiScale(1), out var selected)) Choose(selected);
        }
        else
        {
            var current = mixed ? -1 : Array.IndexOf(field.EnumNames, value?.ToString());
            if (Gui.BeginCombo("##Value", mixed ? DifferentPropertyValues : current >= 0 ? field.EnumLabels[current] : value?.ToString()))
            {
                try
                {
                    for (var index = 0; index < field.EnumNames.Length; index++)
                        if (Gui.Selectable(field.EnumLabels[index], current == index)) Choose(Enum.Parse(field.Type, field.EnumNames[index]));
                }
                finally { Gui.EndCombo(); }
            }
        }
    }
}
