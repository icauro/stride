using Stride.Core.Presentation.Quantum.Presenters;
using Stride.Core.Reflection;

namespace Stride.Editor.ImGui;

internal partial class AssetMetadataDocument
{
    private static bool IsGameSettingItemScalar(Type declared)
    {
        var type = Nullable.GetUnderlyingType(declared) ?? declared;
        return IsGameSettingScalar(type) || type == typeof(Guid) || type == typeof(TimeSpan)
            || type == typeof(DateTime) || type == typeof(Stride.Core.Mathematics.AngleSingle)
            || type == typeof(Stride.Core.Mathematics.Rectangle) || type == typeof(Stride.Core.Mathematics.RectangleF);
    }

    internal bool IsCurrentGameItemValue(GameSettingItem item, INodePresenter presenter)
    {
        if (!CanCopyGameSettingItem(item)) return false;
        var accessor = presenter.GetNodeAccessor();
        return ReferenceEquals(accessor.Node, item.Collection) && accessor.Index == item.Index
            && ReferenceEquals(FindExtensionItemPresenter(item.Collection, item.Index), presenter);
    }

    internal PropertyNumericRange? GameItemNumericRange(INodePresenter presenter)
        => ResolveExtensionPresentation(presenter)?.NumericRange(presenter);

    internal IReadOnlyList<PropertyNumericRange?> GameItemAxisRanges(INodePresenter presenter)
        => ResolveExtensionPresentation(presenter)?.NumericAxisRanges(presenter, []) ?? [];

    internal object? UpdateGameItemScalarValue(GameSettingItem item, INodePresenter presenter, object? value)
    {
        void Validate()
        {
            if (!IsCurrentGameItemValue(item, presenter) || !CanEditPresentedGameSettingItem(item))
                throw new InvalidOperationException("The settings item is read-only or changed.");
            for (var current = presenter; current != null; current = current.Parent)
                if (current.IsReadOnly) throw new InvalidOperationException("The settings item is read-only.");
            if (value == null ? !item.AllowsNull : !item.Type.IsInstanceOfType(value))
                throw new ArgumentException("The item value is not compatible.");
        }
        Validate();
        if (ContentAssetReference.Supports(item.DeclaredType)) ContentAssetReference.Validate(item.DeclaredType, value, item.AllowsNull);
        NativeNumericAxes.AssertEditable(presenter, presenter.Value, value);
        value = PropertyValueValidation.Normalize(value, GameItemNumericRange(presenter));
        value = NativeNumericAxes.Normalize(value, presenter.Value, GameItemAxisRanges(presenter));
        if (Equals(item.Value, value)) return item.Value;
        if (item.Collection.Descriptor is SetDescriptor set && set.Contains(item.Collection.Retrieve()!, value)) return item.Value;
        ApplyInheritedAssetMutation(() => { Validate(); presenter.UpdateValue(value!); }, "Edit " + item.Field.Label);
        return value;
    }
}
