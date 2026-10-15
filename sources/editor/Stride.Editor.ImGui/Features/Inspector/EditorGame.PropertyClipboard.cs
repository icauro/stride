using Stride.Core.Quantum;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawPropertyClipboardCommands(EntityHierarchyDocument owner, ComponentField field)
    {
        if (field.IsContentReference) DrawContentReferenceFetchActions([field.Node.Retrieve()]);
        if (DrawNativeMenuItem(EditorLocalization.Widget("Copy", "Menu"), "", false, owner.CanCopyProperty(field))) Execute(() => { ValidatePendingPropertyStrings(); Gui.SetClipboardText(owner.CopyProperty(field)); });
        var text = Gui.GetClipboardTextS() ?? "";
        var canPaste = owner.CanPasteProperties([field], text);
        if (DrawNativeMenuItem(EditorLocalization.Widget("Paste", "Menu"), "", false, canPaste)) pendingCollectionAction = () => owner.PasteProperties([field], text, false);
        if (DrawNativeMenuItem("Replace", "", false, canPaste)) pendingCollectionAction = () => owner.PasteProperties([field], text, true);
    }
    private void DrawPropertyClipboardCommands(AssetMetadataDocument owner, IMemberNode member)
    {
        if (ContentAssetReference.Supports(member.Type)) DrawContentReferenceFetchActions([member.Retrieve()]);
        if (DrawNativeMenuItem(EditorLocalization.Widget("Copy", "Menu"), "", false, owner.CanCopyAssetProperty(member))) Execute(() => Gui.SetClipboardText(owner.CopyAssetProperty(member)));
        var text = Gui.GetClipboardTextS() ?? "";
        var canPaste = owner.CanPasteAssetProperties([member], text);
        if (DrawNativeMenuItem(EditorLocalization.Widget("Paste", "Menu"), "", false, canPaste)) Execute(() => owner.PasteAssetProperties([member], text, false));
        if (DrawNativeMenuItem("Replace", "", false, canPaste)) Execute(() => owner.PasteAssetProperties([member], text, true));
    }
    private void DrawAggregatePropertyClipboardCommands(CommonComponentProperty property)
    {
        DrawCommonMemberPresenterCommands(property, aggregate: true);
        var fields = property.Fields.ToArray();
        if (fields.All(field => field.IsContentReference)) DrawContentReferenceFetchActions(fields.Select(field => field.Node.Retrieve()));
        var groups = fields.GroupBy(field => aggregatePropertyOwners[field]).ToArray();
        // CopyProperty is DoNotCombine in the original presenter command.
        if (DrawNativeMenuItem(EditorLocalization.Widget("Copy", "Menu"), "", false, fields.Length == 1 && groups[0].Key.CanCopyProperty(fields[0])))
            Execute(() => { ValidatePendingPropertyStrings(); Gui.SetClipboardText(groups[0].Key.CopyProperty(fields[0])); });
        var text = Gui.GetClipboardTextS() ?? "";
        var canPaste = groups.All(group => group.Key.CanPasteProperties(group.ToArray(), text));
        if (DrawNativeMenuItem(EditorLocalization.Widget("Paste", "Menu"), "", false, canPaste)) Queue(false);
        if (DrawNativeMenuItem("Replace", "", false, canPaste)) Queue(true);
        void Queue(bool replace) => pendingCollectionAction = () =>
        {
            if (sessionHistory == null) throw new InvalidOperationException("The selection has no shared history.");
            using var capture = sessionHistory.CaptureOperationsAtomic(() =>
            {
                foreach (var group in groups) group.Key.PasteProperties(group.ToArray(), text, replace);
            });
            capture.Publish(sessionHistory, replace ? "Replace property" : "Paste property");
        };
    }
    private void DrawCollectionItemClipboardCommands(EntityHierarchyDocument owner, CollectionItem item)
    {
        DrawHierarchyItemPresenterCommands([owner], [item]);
        if (item.IsContentReference) DrawContentReferenceFetchActions([item.Value]);
        if (DrawNativeMenuItem(EditorLocalization.Widget("Copy", "Menu"), "", false, owner.CanCopyPropertyItem(item))) Execute(() => { ValidatePendingPropertyStrings(); Gui.SetClipboardText(owner.CopyPropertyItem(item)); });
        var text = Gui.GetClipboardTextS() ?? ""; var canPaste = owner.CanPastePropertyItems([item], text);
        if (DrawNativeMenuItem(EditorLocalization.Widget("Paste", "Menu"), "", false, canPaste)) pendingCollectionAction = () => owner.PastePropertyItems([item], text, false);
        if (DrawNativeMenuItem("Replace", "", false, canPaste)) pendingCollectionAction = () => owner.PastePropertyItems([item], text, true);
        bool CanReset() => ReferenceEquals(document, owner) && item.Collection.HasId(item.Id)
            && ReferenceEquals(item.Field.Node.Target, item.Collection)
            && item.Field.Component.Entity is { } entity && owner.IsSelected(entity.Id)
            && owner.CanResetHierarchyItem(item.Collection, item.Index);
        if (DrawNativeMenuItem(EditorLocalization.Widget("Reset to base value", "Menu"), "", false, CanReset()))
            pendingCollectionAction = () =>
            {
                ValidatePendingPropertyStrings();
                if (!CanReset()) throw new InvalidOperationException("The inherited collection item changed before resetting.");
                owner.ResetHierarchyItem(item.Collection, item.Index);
            };
    }
    private void DrawAggregateItemClipboardCommands(IReadOnlyList<IHierarchyCollectionValue> items)
    {
        DrawHierarchyItemPresenterCommands(items.Select(item => aggregatePropertyOwners[item.Field]).ToArray(), items);
        if (items.All(item => item.IsContentReference)) DrawContentReferenceFetchActions(items.Select(item => item.Value));
        var groups = items.GroupBy(item => aggregatePropertyOwners[item.Field]).ToArray();
        if (DrawNativeMenuItem(EditorLocalization.Widget("Copy", "Menu"), "", false, items.Count == 1 && groups[0].Key.CanCopyPropertyItem(items[0])))
            Execute(() => { ValidatePendingPropertyStrings(); Gui.SetClipboardText(groups[0].Key.CopyPropertyItem(items[0])); });
        var text = Gui.GetClipboardTextS() ?? "";
        var canPaste = groups.All(group => group.Key.CanPastePropertyItems(group.ToArray(), text));
        if (DrawNativeMenuItem(EditorLocalization.Widget("Paste", "Menu"), "", false, canPaste)) Queue(false);
        if (DrawNativeMenuItem("Replace", "", false, canPaste)) Queue(true);
        void Queue(bool replace) => pendingCollectionAction = () =>
        {
            if (sessionHistory == null) throw new InvalidOperationException("The selection has no shared history.");
            using var capture = sessionHistory.CaptureOperationsAtomic(() =>
            {
                foreach (var group in groups) group.Key.PastePropertyItems(group.ToArray(), text, replace);
            });
            capture.Publish(sessionHistory, replace ? "Replace property" : "Paste property");
        };
    }
}
