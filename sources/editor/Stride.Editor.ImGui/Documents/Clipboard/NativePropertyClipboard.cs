using Stride.Core;
using Stride.Core.Assets;
using Stride.Core.Assets.Editor.Services;
using Stride.Core.Assets.Editor.ViewModel.CopyPasteProcessors;
using Stride.Core.Assets.Quantum;
using Stride.Core.Yaml;
using Stride.Core.Quantum;
using Stride.Core.Reflection;

namespace Stride.Editor.ImGui;

/// <summary>Original property clipboard format/processors with canonical owner mutation policies.
/// Call Paste inside the caller's actual graph journal; the service does not publish history.</summary>
internal sealed class NativePropertyClipboard
{
    private readonly AssetPropertyGraph graph;
    private readonly Asset asset;
    private readonly CopyPasteService service;
    public NativePropertyClipboard(AssetPropertyGraph graph, Asset asset,
        Func<IMemberNode, bool> canAssignMember, Func<IObjectNode, NodeIndex, bool> canEditItem,
        Func<IObjectNode, bool> canAddItems, Func<IObjectNode, bool> canRemoveItems,
        Func<IMemberNode, object, object>? normalizeMember = null,
        Func<IObjectNode, NodeIndex, object, object>? normalizeItem = null,
        Action<IMemberNode, object>? afterMemberUpdate = null,
        Func<IObjectNode, NodeIndex, object, bool>? acceptItem = null)
    {
        this.graph = graph; this.asset = asset;
        var settings = AssetYamlSerializer.Default.GetSerializerSettings();
        settings.RegisterTagMapping("!CopyPasteData", typeof(CopyPasteData));
        settings.RegisterTagMapping("!CopyPasteItem", typeof(CopyPasteItem));
        service = new CopyPasteService(graph.Container);
        service.RegisterProcessor(new Processor(canAssignMember, canEditItem, canAddItems, canRemoveItems,
            normalizeMember, normalizeItem, afterMemberUpdate, acceptItem));
    }
    public string Copy(NodeAccessor accessor)
    {
        return CopyValue(accessor.RetrieveValue(), accessor);
    }
    public string CopyValue(object? value, NodeAccessor source = default)
        => service.CopyFromAsset(graph, asset.Id, value, source.Node != null && value != null && graph.Definition.IsObjectReference(source, value));
    public bool CanPaste(string text, Type targetType) => !string.IsNullOrWhiteSpace(text) && text.Length <= 16 * 1024 * 1024
        && service.CanPaste(text, asset.GetType(), targetType);
    public object? PrepareValue(string text, Type declaredType)
    {
        if (!CanPaste(text, declaredType)) throw new InvalidOperationException("The clipboard is not compatible with this property.");
        var result = service.DeserializeCopiedData(text, asset, declaredType);
        if (result.Items.Count == 0) return null;
        var item = result.Items[0];
        if (item.Processor is not Processor) throw new InvalidOperationException("The clipboard has no canonical property paste processor.");
        // Conversion, actual identifiable-ID cloning and reference/override fixups
        // have already run in the original processor's ProcessDeserializedData.
        if (item.Data != null && !declaredType.IsInstanceOfType(item.Data))
            throw new InvalidOperationException("The copied value is not of the declared property type.");
        return item.Data;
    }
    public void Paste(string text, Type targetType, NodeAccessor accessor, bool replace)
    {
        var result = service.DeserializeCopiedData(text, asset, targetType);
        if (result.Items.Count == 0) return;
        var item = result.Items[0];
        if (item.Processor is not Processor processor) throw new InvalidOperationException("The clipboard has no canonical property paste processor.");
        // Match PastePropertyCommandBase's scalar-vs-collection boundary exactly.
        if (item.Data is System.Collections.ICollection && !CollectionDescriptor.IsCollection(accessor.Node.Type)) return;
        var options = new PropertyContainer { { AssetPropertyPasteProcessor.IsReplaceKey, replace } };
        var task = processor.Paste(item, graph, ref accessor, ref options);
        // The actual production AssetPropertyPasteProcessor mutates synchronously.
        if (!task.IsCompleted) throw new InvalidOperationException("The property paste processor did not complete its graph mutation synchronously.");
        task.GetAwaiter().GetResult();
    }
    public static bool HasAdd(IObjectNode node) => node.Descriptor is DictionaryDescriptor dictionary ? !dictionary.IsReadOnly(node.Retrieve())
        : node.Descriptor is CollectionDescriptor collection && collection.HasAdd && !collection.IsReadOnly(node.Retrieve());
    public static bool HasRemove(IObjectNode node) => node.Descriptor is DictionaryDescriptor dictionary ? !dictionary.IsReadOnly(node.Retrieve())
        : node.Descriptor is CollectionDescriptor collection && (collection.HasRemove || collection.HasRemoveAt) && !collection.IsReadOnly(node.Retrieve());

    private sealed class Processor(Func<IMemberNode, bool> canAssign, Func<IObjectNode, NodeIndex, bool> canEdit,
        Func<IObjectNode, bool> canAdd, Func<IObjectNode, bool> canRemove,
        Func<IMemberNode, object, object>? normalizeMember, Func<IObjectNode, NodeIndex, object, object>? normalizeItem,
        Action<IMemberNode, object>? afterMemberUpdate,
        Func<IObjectNode, NodeIndex, object, bool>? acceptItem) : AssetPropertyPasteProcessor
    {
        protected override bool CanUpdateMember(IMemberNode member, object newValue) => base.CanUpdateMember(member, newValue) && canAssign(member);
        protected override bool CanRemoveItem(IObjectNode collection, NodeIndex index) => canRemove(collection) && canEdit(collection, index);
        protected override bool CanReplaceItem(IObjectNode collection, NodeIndex index, object newItem) => canEdit(collection, index) && Accept(collection, index, newItem);
        protected override bool CanInsertItem(IObjectNode collection, NodeIndex index, object newItem) => canAdd(collection) && Accept(collection, index, newItem);
        protected override void UpdateMember(IMemberNode member, object newValue)
        {
            var value = normalizeMember != null ? normalizeMember(member, newValue) : newValue;
            base.UpdateMember(member, value); afterMemberUpdate?.Invoke(member, value);
        }
        protected override void ReplaceItem(IObjectNode collection, NodeIndex index, object newItem)
            => base.ReplaceItem(collection, index, normalizeItem != null ? normalizeItem(collection, index, newItem) : newItem);
        protected override void InsertItem(IObjectNode collection, NodeIndex index, object newItem)
            => base.InsertItem(collection, index, normalizeItem != null ? normalizeItem(collection, index, newItem) : newItem);
        private bool Accept(IObjectNode collection, NodeIndex index, object value)
        {
            if (acceptItem == null || acceptItem(collection, index, value)) return true;
            // Reject the entire caller-owned journal rather than removing an old
            // collection entry and silently skipping its invalid replacement.
            throw new InvalidOperationException("The copied value is not valid for this property collection.");
        }
    }
}
