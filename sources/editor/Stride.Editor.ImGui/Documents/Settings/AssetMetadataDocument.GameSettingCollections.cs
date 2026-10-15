using System.Globalization;
using Stride.Core;
using Stride.Core.Assets;
using Stride.Core.Assets.Quantum;
using Stride.Core.Assets.Quantum.Visitors;
using Stride.Core.Extensions;
using Stride.Core.Quantum;
using Stride.Core.Reflection;

namespace Stride.Editor.ImGui;

internal partial class AssetMetadataDocument
{
    private readonly Dictionary<GameSettingCollection, IReadOnlyList<GameSettingItem>> gameSettingItems = [];
    private readonly HashSet<GameSettingItem> gameSettingItemMembers = new(ReferenceEqualityComparer.Instance);
    public bool CanEditGameSettingCollection(GameSettingCollection field) => CanEditGameSettings && !IsPresentedGameSettingCollectionReadOnly(field) && IsKnownGameSettingCollection(field)
        && field.IsCollection && field.Node is IAssetObjectNode && field.Node.Retrieve() != null && CanEditAssetSettingNode(field.Node)
        && (field.CollectionDescriptor is not DictionaryDescriptor dictionary || !dictionary.IsReadOnly(field.Node.Retrieve()!));
    public bool CanResizeGameSettingCollection(GameSettingCollection field) => CanEditGameSettingCollection(field) && field.CollectionRules?.ReadOnly != true
        && (field.CollectionDescriptor is DictionaryDescriptor || field.CollectionDescriptor is CollectionDescriptor { HasAdd: true } list && (list.HasRemoveAt || list.HasRemove) && !list.IsReadOnly(field.Node.Retrieve()!));
    public bool CanReorderGameSettingCollection(GameSettingCollection field) => CanResizeGameSettingCollection(field)
        && field.CollectionRules?.CanReorderItems == true && field.CollectionDescriptor is CollectionDescriptor { HasInsert: true };
    public IReadOnlyList<GameSettingItem> GetGameSettingItems(GameSettingCollection field)
    {
        EnsureGameSettingsGraph();
        if (!IsKnownGameSettingCollection(field)) return [];
        if (gameSettingItems.TryGetValue(field, out var cached)) return cached;
        if (field.Node is not IAssetObjectNode collection) return [];
        var items = (collection.Indices ?? []).Select(index =>
        {
            var id = collection.TryIndexToId(index, out var actualId) ? actualId : (ItemId?)null;
            var value = collection.Retrieve(index);
            var fields = value != null && !IsGameSettingItemScalar(value.GetType()) && !ContentAssetReference.Supports(value.GetType()) && !SceneObjectReference.Supports(value.GetType())
                && !gameSettingsGraph!.Definition.IsTargetItemObjectReference(collection, index, value)
                && collection.ItemReferences != null && collection.IndexedTarget(index) is { IsEnumerable: false } target
                ? ReadGameSettingsFields(target, field.Key + "/" + (id?.ToString() ?? index.ToString()), 0, new HashSet<IObjectNode>()) : [];
            return new GameSettingItem(field, collection, index, id, fields);
        }).ToArray();
        foreach (var item in items) gameSettingItemMembers.Add(item);
        gameSettingItems.Add(field, items); return items;
    }
    public IReadOnlyList<Type> GameSettingItemTypes(GameSettingCollection field) => field.ElementType is { } declared
        && !ContentAssetReference.Supports(declared) && !SceneObjectReference.Supports(declared)
        ? (Nullable.GetUnderlyingType(declared) ?? declared).GetInheritedInstantiableTypes().Append(Nullable.GetUnderlyingType(declared) ?? declared).Distinct()
            .Where(type => !type.IsAbstract && !type.IsInterface && !type.IsDefined(typeof(Stride.Core.Annotations.NonInstantiableAttribute), true)
                && (type.IsValueType || type == typeof(string) || ObjectFactoryRegistry.CanCreateInstance(type))).OrderBy(type => type.Name).ToArray() : [];
    private IAssetObjectNode EditableGameSettingCollection(GameSettingCollection field, bool resize)
    {
        EnsureGameSettingsGraph(); CommitEdits();
        if (resize ? !CanResizeGameSettingCollection(field) : !CanEditGameSettingCollection(field)) throw new InvalidOperationException("This collection can't be edited.");
        return field.Node;
    }
    private object? CreateGameSettingItem(GameSettingCollection field, Type? type)
    {
        if (type == null)
        {
            if (field.CollectionRules?.NotNullItems == true || field.ElementType!.IsValueType && Nullable.GetUnderlyingType(field.ElementType) == null)
                throw new ArgumentException("This collection does not allow null items.");
            return null;
        }
        if (field.Parent == null && field.Origin.Node.Name == nameof(Stride.Assets.GameSettingsAsset.Defaults) && field.Origin.Node.MemberDescriptor.MemberInfo.DeclaringType == typeof(Stride.Assets.GameSettingsAsset)
            && !Stride.Assets.Presentation.NodePresenters.Rules.GameSettingsConfigurationRules.CanAdd(GameSettings!.Defaults, type))
            throw new ArgumentException("This configuration already exists.");
        if (!GameSettingItemTypes(field).Contains(type)) throw new ArgumentException("The item type is not compatible.");
        var value = type == typeof(string) ? "" : type.IsValueType ? Activator.CreateInstance(type) : ObjectFactoryRegistry.NewInstance(type);
        if (value == null || value.GetType() != type) throw new InvalidOperationException("The item factory returned an incompatible value.");
        return value;
    }
    private object ParseGameSettingKey(GameSettingCollection field, string text)
    {
        if (field.CollectionDescriptor is not DictionaryDescriptor dictionary) throw new InvalidOperationException("This property is not a dictionary.");
        return NativeDictionaryKey.Parse(dictionary.KeyType, text);
    }
    public bool CanEditGameSettingKeys(GameSettingCollection field) => CanResizeGameSettingCollection(field) && field.CollectionDescriptor is DictionaryDescriptor dictionary
        && (dictionary.KeyType == typeof(string) || System.ComponentModel.TypeDescriptor.GetConverter(dictionary.KeyType).CanConvertFrom(typeof(string)));
    public void AddGameSettingItem(GameSettingCollection field, Type? type, string? keyText = null, object? reference = null)
    {
        var value = reference ?? CreateGameSettingItem(field, type);
        if (reference != null) ContentAssetReference.Validate(field.ElementType!, reference, allowsNull: false);
        AddPreparedGameSettingItem(field, value, keyText);
    }
    public void UpdateGameSettingItem(GameSettingItem item, object? value)
    {
        if (!CanCopyGameSettingItem(item)) throw new InvalidOperationException("This item no longer exists.");
        var presenter = FindExtensionItemPresenter(item.Collection, item.Index)
            ?? throw new InvalidOperationException("The settings item presenter changed.");
        UpdateGameItemScalarValue(item, presenter, value);
    }
    public void ChangeGameSettingItemType(GameSettingItem item, Type? type)
    {
        var value = CreateGameSettingItem(item.Context, type);
        ChangeGameSettingCollectionItem(item, node => node.Update(value, item.Index), "Edit " + item.Field.Label, resize: false);
    }
    public void RemoveGameSettingItem(GameSettingItem item)
        => ChangeGameSettingCollectionItem(item, node => node.Remove(item.Value!, item.Index), "Remove " + item.Field.Label, resize: true);
    private void ChangeGameSettingCollectionItem(GameSettingItem item, Action<IAssetObjectNode> mutation, string name, bool resize)
    {
        var node = EditableGameSettingCollection(item.Context, resize: false);
        if (!CanEditPresentedGameSettingItem(item) || resize && !CanRemoveGameSettingItem(item)) throw new InvalidOperationException("This item no longer exists or cannot be edited.");
        var ids = new HashSet<Guid>();
        if (item.Value is { } value && !gameSettingsGraph!.Definition.IsTargetItemObjectReference(node, item.Index, value))
        {
            if (node.ItemReferences != null && node.IndexedTarget(item.Index) is { } target) ids.UnionWith(IdentifiableObjectCollector.Collect(gameSettingsGraph.Definition, target).Keys);
            if (value is IIdentifiable identifiable) ids.Add(identifiable.Id);
        }
        ApplyInheritedAssetMutation(() =>
        {
            if (!CanEditPresentedGameSettingItem(item) || (resize ? !CanRemoveGameSettingItem(item) : !CanEditGameSettingCollection(item.Context)))
                throw new InvalidOperationException("This item no longer exists or cannot be edited.");
            if (ids.Count != 0) gameSettingsGraph!.ClearReferencesToObjects(ids);
            mutation(node);
        }, name);
    }
    public void MoveGameSettingItem(GameSettingItem item, int destination)
    {
        if (!CanReorderGameSettingCollection(item.Context)) throw new InvalidOperationException("This collection can't be reordered.");
        var node = EditableGameSettingCollection(item.Context, resize: true);
        if (!CanEditPresentedGameSettingItem(item) || destination < 0 || destination >= (node.Indices ?? []).Count()) throw new ArgumentOutOfRangeException(nameof(destination));
        var source = item.Index.Int; if (source == destination) return;
        var value = item.Value; var itemOverride = node.GetItemOverride(item.Index); var keyOverride = node.GetKeyOverride(item.Index);
        ApplyInheritedAssetMutation(() =>
        {
            if (!CanReorderGameSettingCollection(item.Context) || !CanEditPresentedGameSettingItem(item) || item.Index.Int != source)
                throw new InvalidOperationException("The settings item changed before moving.");
            if (item.Id is { } id) { node.RemoveAndDiscard(value!, item.Index, id); node.Restore(value!, new NodeIndex(destination), id); }
            else { node.Remove(value!, item.Index); node.Add(value!, new NodeIndex(destination)); }
            node.RestoreItemOverrides(new NodeIndex(destination), itemOverride, keyOverride);
        }, "Move " + item.Field.Label);
    }
    public void RenameGameSettingKey(GameSettingItem item, string text)
    {
        if (!CanEditGameSettingKeys(item.Context)) throw new InvalidOperationException("This dictionary can't be edited.");
        var node = EditableGameSettingCollection(item.Context, resize: true); var key = ParseGameSettingKey(item.Context, text);
        if (!CanEditPresentedGameSettingItem(item)) throw new InvalidOperationException("This item no longer exists or cannot be edited.");
        if (Equals(key, item.Index.Value)) return;
        if (((DictionaryDescriptor)item.Context.CollectionDescriptor).ContainsKey(node.Retrieve(), key)) throw new ArgumentException("This dictionary already contains that key.");
        var index = item.Index; var value = item.Value;
        ApplyInheritedAssetMutation(() =>
        {
            if (!CanEditGameSettingKeys(item.Context) || !CanEditPresentedGameSettingItem(item) || item.Index != index)
                throw new InvalidOperationException("The dictionary item changed before renaming.");
            node.Remove(value!, index); node.Add(value!, new NodeIndex(key));
        }, "Rename key");
    }
}
