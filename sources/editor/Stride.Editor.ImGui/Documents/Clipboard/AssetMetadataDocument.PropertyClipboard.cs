using Stride.Core.Assets.Quantum;
using Stride.Core.Quantum;
using Stride.Core.Reflection;
using Stride.Core.Annotations;

namespace Stride.Editor.ImGui;

internal partial class AssetMetadataDocument
{
    internal bool CanCopyAssetProperty(IMemberNode member)
    {
        _ = AuthoringGraph;
        return member is IAssetNode node && authoringNodes.Contains(node);
    }
    private bool CanMutateClipboardCollection(IObjectNode collection)
        => CanEditAssetSettingNode(collection) && !authoringNodes.OfType<IMemberNode>()
            .Any(member => ReferenceEquals(member.Target, collection) && PropertyClipboardRules.IsReadOnly(member));
    internal bool CanEditAssetCollection(IObjectNode collection) => CanMutateClipboardCollection(collection);
    private NativePropertyClipboard CreateAssetPropertyClipboard(Func<IMemberNode, object, object>? normalize = null)
        => new(AuthoringGraph, AuthoredAsset,
            member => CanEditAssetSettingNode(member) && CanAssignAuthoringNode(member) && !IsExtensionPropertyReadOnly(member),
            (collection, index) => collection is IAssetObjectNode asset && CanMutateClipboardCollection(collection) && CanEditAssetSettingItem(asset, index),
            collection => CanMutateClipboardCollection(collection) && PropertyClipboardRules.CanAdd(collection),
            collection => CanMutateClipboardCollection(collection) && PropertyClipboardRules.CanRemove(collection), normalize ?? NormalizeAssetClipboardMember,
            afterMemberUpdate: (_, _) => RefreshAuthoringNodeAccess(),
            acceptItem: (collection, _, value) => value != null || !authoringNodes.OfType<IMemberNode>()
                .Any(member => ReferenceEquals(member.Target, collection) && PropertyClipboardRules.NotNullItems(member)));
    private static object NormalizeAssetClipboardMember(IMemberNode member, object value)
    {
        var type = Nullable.GetUnderlyingType(member.Type) ?? member.Type;
        var metadata = TypeDescriptorFactory.Default.AttributeRegistry.GetAttribute<DataMemberRangeAttribute>(member.MemberDescriptor.MemberInfo);
        var range = metadata != null && (type.IsPrimitive || type == typeof(decimal)) ? new PropertyNumericRange(metadata, type) : null;
        value = PropertyValueValidation.Normalize(value, range)!;
        return VectorPropertyNumericMetadata.Normalize(value, member.Retrieve(), VectorPropertyNumericMetadata.Create(member))!;
    }
    internal string CopyAssetProperty(IMemberNode member)
    {
        CommitEdits();
        return CanCopyAssetProperty(member) ? CreateAssetPropertyClipboard().Copy(new(member, NodeIndex.Empty)) : "";
    }
    internal string CopyAssetPropertyAccessor(NodeAccessor accessor)
    {
        CommitEdits(); _ = AuthoringGraph;
        if (accessor.Node is not IAssetNode node || !authoringNodes.Contains(node)) throw new InvalidOperationException("The property has no canonical authored owner.");
        return CreateAssetPropertyClipboard().Copy(accessor);
    }
    internal bool CanPrepareAssetPropertyValue(string text, Type type) => CreateAssetPropertyClipboard().CanPaste(text, type);
    internal object? PrepareAssetPropertyValue(string text, Type type) => CreateAssetPropertyClipboard().PrepareValue(text, type);
    internal bool CanPasteAssetAccessors(IReadOnlyList<NodeAccessor> accessors, string text)
    {
        _ = AuthoringGraph;
        var clipboard = CreateAssetPropertyClipboard();
        return accessors.Count != 0 && accessors.All(accessor => accessor.Node is IAssetNode node && authoringNodes.Contains(node)
            && (accessor.Node is IMemberNode member ? CanEditAssetSettingNode(member) && !PropertyClipboardRules.IsReadOnly(member)
                && (member.Target?.IsEnumerable == true || CanAssignAuthoringNode(member))
                : accessor.Node is IAssetObjectNode collection && CanMutateClipboardCollection(collection)
                    && (collection.Indices ?? []).Contains(accessor.Index) && CanEditAssetSettingItem(collection, accessor.Index))
            && clipboard.CanPaste(text, accessor.Node.Type));
    }
    internal void PasteAssetAccessors(IReadOnlyList<NodeAccessor> accessors, string text, bool replace)
    {
        CommitEdits();
        if (!CanPasteAssetAccessors(accessors, text)) throw new InvalidOperationException("The clipboard cannot be pasted into these authored properties.");
        var clipboard = CreateAssetPropertyClipboard();
        ApplyInheritedAssetMutation(() =>
        {
            foreach (var accessor in accessors.Distinct()) clipboard.Paste(text, accessor.Node.Type, accessor, replace);
        }, replace ? "Replace property" : "Paste property");
    }
    internal bool CanPasteAssetProperties(IReadOnlyList<IMemberNode> members, string text)
    {
        if (members.Count == 0) return false;
        var clipboard = CreateAssetPropertyClipboard();
        return members.All(member => CanCopyAssetProperty(member) && CanEditAssetSettingNode(member)
            && !IsExtensionPropertyReadOnly(member) && !PropertyClipboardRules.IsReadOnly(member)
            && (member.Target?.IsEnumerable == true || CanAssignAuthoringNode(member)) && clipboard.CanPaste(text, member.Type));
    }
    internal void PasteAssetProperties(IReadOnlyList<IMemberNode> members, string text, bool replace,
        Func<IMemberNode, object, object>? normalize = null)
    {
        CommitEdits();
        if (!CanPasteAssetProperties(members, text)) throw new InvalidOperationException("The clipboard cannot be pasted into these properties.");
        var clipboard = CreateAssetPropertyClipboard(normalize);
        ApplyInheritedAssetMutation(() =>
        {
            foreach (var member in members.Distinct()) clipboard.Paste(text, member.Type, new(member, NodeIndex.Empty), replace);
        }, replace ? "Replace property" : "Paste property");
    }
    internal bool CanCopyImportItem(AssetImportSettingItem item) => CanCopyAssetProperty(item.Field.Node)
        && ReferenceEquals(item.Field.Collection, item.Collection)
        && (item.Id is { } id ? item.Collection.HasId(id) : (item.Collection.Indices ?? []).Contains(item.Index));
    internal string CopyImportItem(AssetImportSettingItem item)
    {
        CommitEdits();
        return CanCopyImportItem(item) ? CreateAssetPropertyClipboard().Copy(new(item.Collection, item.Index)) : "";
    }
    internal bool CanPasteImportItem(AssetImportSettingItem item, string text)
        => CanCopyImportItem(item) && CanEditImportItem(item) && !PropertyClipboardRules.IsReadOnly(item.Field.Node)
            && CreateAssetPropertyClipboard().CanPaste(text, item.Collection.Type);
    internal void PasteImportItem(AssetImportSettingItem item, string text, bool replace)
    {
        CommitEdits();
        if (!CanPasteImportItem(item, text)) throw new InvalidOperationException("The clipboard cannot be pasted into this collection item.");
        var clipboard = CreateAssetPropertyClipboard();
        ApplyInheritedAssetMutation(() => clipboard.Paste(text, item.Collection.Type, new(item.Collection, item.Index), replace),
            replace ? "Replace property" : "Paste property");
    }
}
