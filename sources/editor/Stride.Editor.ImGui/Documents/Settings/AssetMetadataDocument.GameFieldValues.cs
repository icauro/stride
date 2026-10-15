using Stride.Core.Presentation.Quantum.Presenters;

namespace Stride.Editor.ImGui;

internal partial class AssetMetadataDocument
{
    internal bool IsCurrentGameSettingField(GameSettingField field) => gameSettingMembers.Contains(field);
    internal INodePresenter? GameFieldPresenter(GameSettingField field)
        => IsCurrentGameSettingField(field) ? ResolveExtensionPresentation(field.Node)?.Find(field.Node) : null;
    internal PropertyNumericRange? GameFieldNumericRange(GameSettingField field)
        => ExtensionNumericRange(field.Node) ?? field.Range;
    internal IReadOnlyList<PropertyNumericRange?> GameFieldAxisRanges(GameSettingField field)
        => GameFieldPresenter(field) is { } presenter && ResolveExtensionPresentation(presenter) is { } presentation
            ? presentation.NumericAxisRanges(presenter, field.AxisRanges) : field.AxisRanges;

    internal object? UpdateGameFieldValue(GameSettingField field, object? value)
    {
        void Validate()
        {
            if (!IsCurrentGameSettingField(field) || !CanEditPresentedGameSetting(field))
                throw new InvalidOperationException("The setting is read-only or changed.");
            if (value == null ? field.Type.IsValueType && Nullable.GetUnderlyingType(field.Node.Type) == null || !field.AllowsNull
                : !field.Type.IsInstanceOfType(value)) throw new ArgumentException("The setting value is not compatible.");
        }
        Validate();
        if (ContentAssetReference.Supports(field.Node.Type)) ContentAssetReference.Validate(field.Node.Type, value, field.AllowsNull);
        var presenter = GameFieldPresenter(field) ?? throw new InvalidOperationException("The setting presenter changed.");
        NativeNumericAxes.AssertEditable(presenter, field.Node.Retrieve(), value);
        value = PropertyValueValidation.Normalize(value, GameFieldNumericRange(field));
        value = NativeNumericAxes.Normalize(value, field.Node.Retrieve(), GameFieldAxisRanges(field));
        if (ContentAssetReference.AreEqual(field.Node.Retrieve(), value)) return field.Node.Retrieve();
        ApplyInheritedAssetMutation(() => { Validate(); field.Node.Update(value); }, "Edit " + field.Label);
        return value;
    }
}
