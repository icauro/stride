using Stride.Assets;
using Stride.Assets.Presentation.NodePresenters.Rules;
using Stride.Core.Assets.Editor.Quantum.NodePresenters.Commands;
using Stride.Core.Assets.Quantum;
using Stride.Core.Presentation.Quantum.Presenters;
using Stride.Core.Quantum;
using Stride.Core.Reflection;

namespace Stride.Editor.ImGui;

internal partial class AssetMetadataDocument
{
    internal bool CanRemoveGameSettingItem(GameSettingItem item)
        => CanEditPresentedGameSettingItem(item) && item.Context.CollectionRules?.ReadOnly != true
            && (item.Context.CollectionDescriptor is DictionaryDescriptor
                || item.Context.CollectionDescriptor is CollectionDescriptor list && (list.HasRemoveAt || list.HasRemove)
                    && !list.IsReadOnly(item.Collection.Retrieve()!) && Stride.Core.Assets.AssetCollectionCommandRules.CanAdd(list.ElementType));
    internal bool CanAddGameSettingCollection(GameSettingCollection field)
        => CanEditGameSettingCollection(field) && field.CollectionRules?.ReadOnly != true
            && (field.CollectionDescriptor is DictionaryDescriptor dictionary
                ? NativeDictionaryKey.CanAdd(dictionary.KeyType) && Stride.Core.Assets.AssetCollectionCommandRules.CanAdd(dictionary.ValueType)
                : field.CollectionDescriptor is CollectionDescriptor { HasAdd: true } list
                    && !list.IsReadOnly(field.Node.Retrieve()!) && Stride.Core.Assets.AssetCollectionCommandRules.CanAdd(list.ElementType));

    internal EditorPresenterTarget? AcquireGameCollectionCommandTarget(GameSettingCollection field)
        => IsKnownGameSettingCollection(field) ? AcquireExtensionFactoryTarget(new NodeAccessor(field.Node, NodeIndex.Empty)) : null;

    internal bool IsCurrentGameCollectionTarget(GameSettingCollection field, INodePresenter presenter)
    {
        return IsKnownGameSettingCollection(field) && ResolveExtensionPresentation(field.Node) is { } presentation
            && ReferenceEquals(EditorPresenterItemMetadata.FindCollection(presentation.RootPresenter, field.Node), presenter);
    }

    private void AddPreparedGameSettingItem(GameSettingCollection field, object? value, string? key, string? title = null)
    {
        if (!CanAddGameSettingCollection(field)) throw new InvalidOperationException("This collection can't be edited.");
        var type = field.ElementType!;
        if (value == null ? field.CollectionRules?.NotNullItems == true || type.IsValueType && Nullable.GetUnderlyingType(type) == null
            : !(Nullable.GetUnderlyingType(type) ?? type).IsInstanceOfType(value))
            throw new ArgumentException("The collection entry has an incompatible value.");
        if (ContentAssetReference.Supports(type)) ContentAssetReference.Validate(type, value, field.CollectionRules?.NotNullItems != true);
        if (field.Parent == null && field.Origin.Node.Name == nameof(GameSettingsAsset.Defaults)
            && field.Origin.Node.MemberDescriptor.MemberInfo.DeclaringType == typeof(GameSettingsAsset)
            && value != null && !GameSettingsConfigurationRules.CanAdd(GameSettings!.Defaults, value.GetType()))
            throw new ArgumentException("This configuration already exists.");
        var node = field.Node;
        var index = field.CollectionDescriptor is DictionaryDescriptor dictionary
            ? new NodeIndex(NativeDictionaryKey.CreateForAdd(dictionary.KeyType, key))
            : node.Descriptor is SetDescriptor ? new NodeIndex(value) : new NodeIndex((node.Indices ?? []).Count());
        if (field.CollectionDescriptor is DictionaryDescriptor descriptor && descriptor.ContainsKey(node.Retrieve(), index.Value)) return;
        if (node.Descriptor is SetDescriptor set && set.Contains(node.Retrieve()!, value)) return;
        var collection = node.Retrieve();
        ApplyInheritedAssetMutation(() =>
        {
            if (!CanAddGameSettingCollection(field) || !ReferenceEquals(node.Retrieve(), collection))
                throw new InvalidOperationException("The settings collection changed before adding.");
            node.Add(value!, index);
        }, title ?? "Add " + field.Label);
    }
}
