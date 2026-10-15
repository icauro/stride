using Stride.Core.Assets.Quantum;

namespace Stride.Editor.ImGui;

internal partial class AssetMetadataDocument
{
    private readonly Dictionary<GameSettingField, GameSettingCollection> rootGameSettingCollections = [];
    private readonly Dictionary<GameSettingItem, GameSettingCollection> nestedGameSettingCollections = [];
    internal GameSettingCollection? GetGameSettingCollection(GameSettingField field)
    {
        EnsureGameSettingsGraph();
        if (!gameSettingMembers.Contains(field) || !field.IsCollection || field.Node.Target is not IAssetObjectNode node) return null;
        if (!rootGameSettingCollections.TryGetValue(field, out var context))
            rootGameSettingCollections[field] = context = new(field, node, null);
        return context;
    }
    internal GameSettingCollection? GetNestedGameSettingCollection(GameSettingItem item)
    {
        if (!gameSettingItemMembers.Contains(item) || !IsKnownGameSettingCollection(item.Context) || item.Value == null
            || item.Collection.ItemReferences == null
            || gameSettingsGraph!.Definition.IsTargetItemObjectReference(item.Collection, item.Index, item.Value)
            || item.Collection.IndexedTarget(item.Index) is not IAssetObjectNode { IsEnumerable: true } node) return null;
        var depth = 0;
        for (var ancestor = item.Context; ancestor != null; ancestor = ancestor.Parent?.Context)
            if (++depth > 16 || ReferenceEquals(ancestor.Node, node)) return null;
        if (!nestedGameSettingCollections.TryGetValue(item, out var context))
            nestedGameSettingCollections[item] = context = new(item.Field, node, item);
        return context.IsCollection && ReferenceEquals(context.Node, node) ? context : null;
    }
    private bool IsKnownGameSettingCollection(GameSettingCollection context)
    {
        if (!gameSettingMembers.Contains(context.Origin)) return false;
        if (context.Parent is not { } parent)
            return ReferenceEquals(context.Origin.Node.Target, context.Node);
        return gameSettingItemMembers.Contains(parent) && IsKnownGameSettingCollection(parent.Context)
            && parent.Value != null && parent.Collection.ItemReferences != null
            && ReferenceEquals(parent.Collection.IndexedTarget(parent.Index), context.Node);
    }
    internal IReadOnlyList<GameSettingItem> GetGameSettingItems(GameSettingField field)
        => GetGameSettingCollection(field) is { } collection ? GetGameSettingItems(collection) : [];
}
