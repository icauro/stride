using Stride.Core.Assets;
using Stride.Core.Assets.Editor.Quantum.NodePresenters.Commands;
using Stride.Core.Assets.Editor.Quantum.NodePresenters.Keys;
using Stride.Core.Quantum;
using Stride.Core.Presentation.Quantum.Presenters;

namespace Stride.Editor.ImGui;

internal partial class AssetMetadataDocument
{
    internal virtual IEnumerable<AbstractNodeEntry>? ExtensionAbstractEntries(IMemberNode member) => ResolveExtensionPresentation(member)?.AbstractEntries(member);
    internal IEnumerable<AbstractNodeEntry>? ExtensionAbstractEntries(IObjectNode collection, NodeIndex index)
    {
        var presenter = FindExtensionItemPresenter(collection, index) ?? ExtensionVirtualProperties.FirstOrDefault(presenter =>
        { var accessor = presenter.GetNodeAccessor(); return ReferenceEquals(accessor.Node, collection) && accessor.Index == index; });
        return presenter != null && presenter.AttachedProperties.TryGetValue(AbstractNodeEntryData.Key, out var entries) ? entries : null;
    }
    internal IEnumerable<AbstractNodeEntry>? ExtensionAbstractEntries(VirtualNodePresenter presenter)
        => ExtensionVirtualProperties.Contains(presenter) && presenter.AttachedProperties.TryGetValue(AbstractNodeEntryData.Key, out var entries) ? entries : null;
    internal virtual EditorPresenterTarget? AcquireExtensionFactoryTarget(NodeAccessor accessor)
    {
        if (accessor.Node is IMemberNode member) return AcquireExtensionCommandTarget(member);
        if (accessor.Node is not IObjectNode collection) return null;
        if (accessor.Index.IsEmpty && ResolveExtensionPresentation(collection) is { } presentation
            && EditorPresenterItemMetadata.FindCollection(presentation.RootPresenter, collection) is { } root) return presentation.AcquireTarget(root);
        if (FindExtensionItemPresenter(collection, accessor.Index) is { } item) return ResolveExtensionPresentation(collection)?.AcquireTarget(item);
        var presenter = ExtensionVirtualProperties.FirstOrDefault(presenter =>
        { var current = presenter.GetNodeAccessor(); return ReferenceEquals(current.Node, accessor.Node) && current.Index == accessor.Index; });
        return presenter == null ? null : AcquireExtensionVirtualTarget(presenter);
    }
    internal IReadOnlyList<AbstractNodeEntry> ImportItemEntries(AssetImportSettingItem item)
        => NativeAbstractEntries.Available(ExtensionAbstractEntries(item.Collection, item.Index), [], item.AllowsNull);
    internal void ChangeImportItemEntry(AssetImportSettingItem item, AbstractNodeEntry entry)
    {
        CommitEdits();
        if (!CanEditImportItem(item) || !NativeAbstractEntries.Contains(ImportItemEntries(item), entry)) throw new InvalidOperationException("The native item entry is no longer available.");
        if (entry.IsMatchingValue(item.Value!)) return;
        var value = entry.GenerateValue(item.Value!);
        if (value != null) AssetCollectionItemIdHelper.GenerateMissingItemIds(value);
        UpdateImportItem(item, value); EndImportSettingItemEdit();
    }
    internal IReadOnlyList<AbstractNodeEntry> GameItemEntries(GameSettingItem item)
        => NativeAbstractEntries.Available(ExtensionAbstractEntries(item.Collection, item.Index) ?? ExtensionCollectionAbstractEntries(item.Collection), GameSettingItemTypes(item.Context), item.AllowsNull);
    internal void ChangeGameItemEntry(GameSettingItem item, AbstractNodeEntry entry)
    {
        CommitEdits();
        if (!CanEditPresentedGameSettingItem(item) || !NativeAbstractEntries.Contains(GameItemEntries(item), entry)) throw new InvalidOperationException("The native item entry is no longer available.");
        if (entry.IsMatchingValue(item.Value!)) return;
        var value = entry.GenerateValue(item.Value!);
        if (value == null ? !item.AllowsNull : !item.Type.IsInstanceOfType(value)) throw new ArgumentException("The item entry has an incompatible value.");
        if (value != null) AssetCollectionItemIdHelper.GenerateMissingItemIds(value);
        ChangeGameSettingCollectionItem(item, node => node.Update(value, item.Index), "Edit " + item.Field.Label, resize: false);
    }
    internal virtual IEnumerable<AbstractNodeEntry>? ExtensionCollectionAbstractEntries(IObjectNode collection)
        => ResolveExtensionPresentation(collection) is { } presentation && EditorPresenterItemMetadata.FindCollection(presentation.RootPresenter, collection) is { } presenter
            && presenter.AttachedProperties.TryGetValue(AbstractNodeEntryData.Key, out var entries) ? entries : null;
    internal IReadOnlyList<AbstractNodeEntry> ImportCollectionEntries(AssetImportSettingField field)
        => NativeAbstractEntries.Available(ExtensionCollectionAbstractEntries(field.Collection!), ImportCollectionItemTypes(field), field.CollectionRules?.NotNullItems != true
            && (!field.ElementType!.IsValueType || Nullable.GetUnderlyingType(field.ElementType) != null), true);
    internal void AddImportCollectionEntry(AssetImportSettingField field, AbstractNodeEntry entry, string? key)
    {
        CommitEdits();
        if (!CanAddImportCollection(field) || !NativeAbstractEntries.Contains(ImportCollectionEntries(field), entry))
            throw new InvalidOperationException("The native collection entry is no longer available.");
        var value = entry.GenerateValue(null!);
        if (value != null) AssetCollectionItemIdHelper.GenerateMissingItemIds(value);
        AddImportCollectionItem(field, null, value, key);
    }
    internal IReadOnlyList<AbstractNodeEntry> GameCollectionEntries(GameSettingCollection field)
        => NativeAbstractEntries.Available(ExtensionCollectionAbstractEntries(field.Node), GameSettingItemTypes(field), field.CollectionRules?.NotNullItems != true
            && (!field.ElementType!.IsValueType || Nullable.GetUnderlyingType(field.ElementType) != null), true);
    internal void AddGameCollectionEntry(GameSettingCollection field, AbstractNodeEntry entry, string? key)
    {
        CommitEdits();
        if (!CanAddGameSettingCollection(field) || !NativeAbstractEntries.Contains(GameCollectionEntries(field), entry))
            throw new InvalidOperationException("The native collection entry is no longer available.");
        var value = entry.GenerateValue(null!);
        if (value == null ? field.CollectionRules?.NotNullItems == true || field.ElementType!.IsValueType && Nullable.GetUnderlyingType(field.ElementType) == null
            : !field.ElementType!.IsInstanceOfType(value)) throw new ArgumentException("The collection entry has an incompatible value.");
        if (ContentAssetReference.Supports(field.ElementType!)) ContentAssetReference.Validate(field.ElementType!, value, field.CollectionRules?.NotNullItems != true);
        if (value != null) AssetCollectionItemIdHelper.GenerateMissingItemIds(value);
        AddPreparedGameSettingItem(field, value, key);
    }
    internal IReadOnlyList<AbstractNodeEntry> GameConfigurationEntries()
    {
        var node = GameConfigurationNode();
        return NativeAbstractEntries.Available(ExtensionCollectionAbstractEntries(node), AvailableGameConfigurations, false)
            .Where(entry => entry is not AbstractNodeType type || Stride.Assets.Presentation.NodePresenters.Rules.GameSettingsConfigurationRules.CanAdd(GameSettings!.Defaults, type.Type)).ToArray();
    }
    internal void AddGameConfigurationEntry(AbstractNodeEntry entry)
    {
        CommitEdits();
        if (!NativeAbstractEntries.Contains(GameConfigurationEntries(), entry)) throw new InvalidOperationException("The configuration entry is no longer available.");
        var value = entry.GenerateValue(null!) as Stride.Data.Configuration ?? throw new ArgumentException("The factory entry is not a configuration.");
        if (!Stride.Assets.Presentation.NodePresenters.Rules.GameSettingsConfigurationRules.CanAdd(GameSettings!.Defaults, value.GetType())) throw new InvalidOperationException("This configuration already exists.");
        AssetCollectionItemIdHelper.GenerateMissingItemIds(value);
        var member = gameSettingFields.First(field => field.Node.Name == nameof(Stride.Assets.GameSettingsAsset.Defaults));
        var collection = GetGameSettingCollection(member) ?? throw new InvalidOperationException("The configuration collection is unavailable.");
        AddPreparedGameSettingItem(collection, value, null, "Add configuration");
    }
    internal IReadOnlyList<AbstractNodeEntry> ImportSettingEntries(AssetImportSettingField field)
        => NativeAbstractEntries.Available(ExtensionAbstractEntries(field.Node), [], field.AllowsNull);
    internal void ChangeImportSettingEntry(AssetImportSettingField field, AbstractNodeEntry entry)
    {
        CommitEdits();
        if (!CanEditImportSetting(field) || !NativeAbstractEntries.Contains(ImportSettingEntries(field), entry))
            throw new InvalidOperationException("The native property entry is no longer available.");
        var current = field.Node.Retrieve();
        if (entry.IsMatchingValue(current!)) return;
        var value = entry.GenerateValue(current!);
        if (value != null) AssetCollectionItemIdHelper.GenerateMissingItemIds(value);
        UpdateImportSetting(field, value); EndImportSettingEdit();
    }
    internal IReadOnlyList<AbstractNodeEntry> GameSettingEntries(GameSettingField field)
        => NativeAbstractEntries.Available(ExtensionAbstractEntries(field.Node), field.Types, field.AllowsNull);
    internal void ChangeGameSettingEntry(GameSettingField field, AbstractNodeEntry entry)
    {
        CommitEdits();
        if (!gameSettingMembers.Contains(field) || !CanEditPresentedGameSetting(field) || !NativeAbstractEntries.Contains(GameSettingEntries(field), entry))
            throw new InvalidOperationException("The native property entry is no longer available.");
        var current = field.Node.Retrieve();
        if (entry.IsMatchingValue(current!)) return;
        var value = entry.GenerateValue(current!);
        if (value != null) AssetCollectionItemIdHelper.GenerateMissingItemIds(value);
        ChangeGameSettingOwnedValue(field, value);
    }
}
