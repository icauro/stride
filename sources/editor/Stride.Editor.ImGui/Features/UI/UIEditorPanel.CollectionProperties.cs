using Hexa.NET.ImGui;
using Stride.Core;
using Stride.Core.Quantum;
using Stride.Core.Reflection;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class UIEditorPanel
{
    private void DrawUICollectionProperties(UIDocument source, Guid[] ids)
    {
        if (ids.Length == 0) return;
        var choices = ids.Select(source.PropertyCollections).ToArray();
        foreach (var first in choices[0])
        {
            var bindings = choices.Select(fields => fields.FirstOrDefault(field => field.Path == first.Path && field.ElementType == first.ElementType)).ToArray();
            if (bindings.Any(binding => binding == null)) continue;
            var collections = bindings.Select(binding => binding!).ToArray();
            Gui.PushID("collection/" + first.Path);
            try
            {
                var open = Gui.TreeNodeEx("##Collection", ImGuiTreeNodeFlags.DefaultOpen); Gui.SameLine(); Gui.TextUnformatted(first.Label);
                host.ShowDocumentation(first.Member, source.Find(ids[0])!.GetType());
                if (Gui.BeginPopupContextItem("Collection actions"))
                {
                    try
                    {
                        if (host.DrawMenuItem("Copy", "", false, ids.Length == 1))
                            QueueUIPropertyCommand(source, ids, () => Gui.SetClipboardText(source.CopyAssetPropertyAccessor(new(first.Collection, NodeIndex.Empty))), writable: false);
                        var text = Gui.GetClipboardTextS() ?? "";
                        var canPaste = collections.All(collection => source.CanPasteUICollection(collection, text));
                        if (host.DrawMenuItem("Paste", "", false, canPaste)) QueueUIPropertyCommand(source, ids, () => source.PasteUICollection(collections, text, false));
                        if (host.DrawMenuItem("Replace", "", false, canPaste)) QueueUIPropertyCommand(source, ids, () => source.PasteUICollection(collections, text, true));
                        var canReset = collections.All(source.CanEditCollection) && collections.Any(collection => collection.AttachedRoot
                            ? source.CanResetDependencyProperty(collection.ElementId, collection.AttachedKey!)
                            : source.CanResetAssetSettingNode(collection.Collection));
                        if (host.DrawMenuItem("Reset to base value", "", false, canReset)) QueueUIPropertyCommand(source, ids, () => source.BatchCollectionEdits(() =>
                        {
                            foreach (var collection in collections.DistinctBy(collection => collection.Collection))
                            {
                                if (collection.AttachedRoot)
                                {
                                    if (source.CanResetDependencyProperty(collection.ElementId, collection.AttachedKey!))
                                        source.ResetDependencyProperties([collection.ElementId], collection.AttachedKey!);
                                }
                                else if (source.CanResetAssetSettingNode(collection.Collection)) source.ResetAssetSettingNode(collection.Collection);
                            }
                        }, "Reset to base value"));
                        host.DrawUICollectionProviderCommands(source, ids, collections);
                    }
                    finally { Gui.EndPopup(); }
                }
                if (!open) continue;
                try
                {
                    foreach (var item in first.Items)
                    {
                        var items = collections.Select(collection => collection.Items.FirstOrDefault(candidate => candidate.Index == item.Index)).ToArray();
                        if (items.Any(current => current == null)) continue;
                        var actual = items.Select(current => current!).DistinctBy(current => (current.Owner.Collection, current.Identity)).ToArray();
                        if (actual.All(current => !source.IsExtensionItemVisible(current.Owner.Collection, current.Index))) continue;
                        Gui.PushID(item.Identity);
                        try
                        {
                            using var row = new PropertyRowScope("Item", source.ExtensionItemLabel(item.Owner.Collection, item.Index) ?? "[" + item.Index + "]");
                            if (!row.DrawContents) continue;
                            var values = actual.Select(current => current.Value).ToArray();
                            var entries = source.CommonUICollectionItemEntries(actual);
                            if (first.ElementType == typeof(string) && entries == null)
                                host.DrawRetainedStringProperty(source, item.Owner.Collection, values.Skip(1).Any(value => !Equals(value, values[0])),
                                    () => actual.All(source.CanEditCollectionItem),
                                    () => actual.Select(current => current.Value).Concat(actual.SelectMany(current =>
                                        new object?[] { current.Owner.Member, current.Owner.Collection, current.Index, current.Id, current.Owner.ElementId })).ToArray(),
                                    replacement => source.TransformCollectionItems(actual, _ => replacement));
                            else DrawUIValue(first.Path + "/" + item.Identity, first.Label, first.ElementType, values[0], values,
                                actual.All(source.CanEditCollectionItem), actual.All(current => current.Owner.Rules?.NotNullItems != true),
                                change => source.TransformCollectionItems(actual, change), nativeEntries: entries,
                                selectEntry: entry => QueueUIPropertyCommand(source, ids, () => host.SelectUICollectionItemFactoryEntry(source, actual, entry)),
                                numericRangeProvider: () => NativeNumericInput.Merge(Nullable.GetUnderlyingType(first.ElementType) ?? first.ElementType, actual.Select(current => source.UIItemNumericRange(current.Owner.Collection, current.Index))),
                                axisNumericRangeProvider: axis => NativeNumericInput.Merge(PropertyCoordinateTypeForUI(first.ElementType), actual.Select(current => source.UIAxisNumericRanges(current).ElementAtOrDefault(axis))),
                                axisCanEditProvider: axis => actual.All(current => !source.IsUIAxisReadOnly(current, axis)),
                                rangedRangesProvider: () => actual.Select(current => source.UIItemNumericRange(current.Owner.Collection, current.Index)).ToArray(),
                                valueTarget: CollectionValueTarget(source, actual));
                            if (Gui.BeginPopupContextItem("Item actions"))
                            {
                                try
                                {
                                    if (ContentAssetReference.Supports(first.ElementType)) host.DrawReferenceFetchActions(values);
                                    if (first.ElementType.IsEnum && first.ElementType.IsDefined(typeof(FlagsAttribute), true))
                                    {
                                        using var flagsDisabled = ImGuiScopes.Disabled(!actual.All(source.CanEditCollectionItem));
                                        if (FlagEnumPropertyControl.DrawBulkActions() is { } flagAction)
                                            QueueUIPropertyCommand(source, ids, () => source.TransformCollectionItems(actual,
                                                value => FlagEnumSelection.Apply((Enum)(value ?? Enum.ToObject(first.ElementType, 0)), flagAction)));
                                    }
                                    if (host.DrawMenuItem("Copy", "", false, ids.Length == 1))
                                        QueueUIPropertyCommand(source, ids, () => Gui.SetClipboardText(source.CopyAssetPropertyAccessor(new(item.Owner.Collection, item.Index))), writable: false);
                                    var text = Gui.GetClipboardTextS() ?? ""; var canPaste = source.CanPasteCollectionItems(actual, text);
                                    if (host.DrawMenuItem("Paste", "", false, canPaste)) QueueUIPropertyCommand(source, ids, () => source.PasteCollectionItems(actual, text, false));
                                    if (host.DrawMenuItem("Replace", "", false, canPaste)) QueueUIPropertyCommand(source, ids, () => source.PasteCollectionItems(actual, text, true));
                                    var canReset = actual.All(source.CanEditCollectionItem) && actual.Any(current => source.CanResetAssetSettingItem(current.Owner.Collection, current.Index));
                                    if (host.DrawMenuItem("Reset to base value", "", false, canReset)) QueueUIPropertyCommand(source, ids, () => source.BatchCollectionEdits(() =>
                                    { foreach (var current in actual.Where(current => source.CanResetAssetSettingItem(current.Owner.Collection, current.Index))) source.ResetAssetSettingItem(current.Owner.Collection, current.Index); }, "Reset to base value"));
                                    if (actual.All(source.CanRenameCollectionKey) && host.BeginMenu("Rename"))
                                    {
                                        try
                                        {
                                            host.DrawRetainedUIDictionaryKey(source, ids, actual);
                                        }
                                        finally { Gui.EndMenu(); }
                                    }
                                    host.DrawUICollectionItemProviderCommands(source, ids, actual);
                                    var canRemove = actual.All(source.CanRemoveCollectionItem);
                                    if (host.DrawMenuItem("Remove", "", false, canRemove)) QueueUIPropertyCommand(source, ids, () => source.BatchCollectionEdits(() =>
                                    { foreach (var current in actual) source.RemoveCollectionItem(current); }, "Remove UI collection item"));
                                    if (actual.All(source.CanMoveCollectionItem))
                                    {
                                        var index = item.Index.Int;
                                        if (host.DrawMenuItem("Move up", "", false, index > 0)) Move(index - 1);
                                        if (host.DrawMenuItem("Move down", "", false, actual.All(current => index + 1 < current.Owner.Items.Count))) Move(index + 1);
                                        void Move(int destination) => QueueUIPropertyCommand(source, ids, () => source.BatchCollectionEdits(() =>
                                        { foreach (var current in actual) source.MoveCollectionItem(current, destination); }, "Move UI collection item"));
                                    }
                                }
                                finally { Gui.EndPopup(); }
                            }
                        }
                        finally { Gui.PopID(); }
                    }
                    if (first.Collection.Descriptor is DictionaryDescriptor)
                    {
                        host.DrawUIDictionaryDefaultAdd(source, collections);
                        continue;
                    }
                    using var disabled = ImGuiScopes.Disabled(!collections.All(source.CanAddCollectionItem));
                    if (Gui.SmallButton("Add"))
                    {
                        host.ValidateHierarchyEdits(); source.CommitEdits();
                        if (!host.CanEditHierarchyOwner(source) || !collections.All(source.CanAddCollectionItem))
                            throw new InvalidOperationException("The UI collection changed before adding.");
                        if (collections.All(source.CanDefaultUICollectionAdd))
                            QueueUIPropertyCommand(source, ids, () => host.AddDefaultUICollectionItems(source, collections));
                        else Gui.OpenPopup("Add collection item");
                    }
                    if (Gui.BeginPopup("Add collection item"))
                    {
                        try
                        {
                            if (ContentAssetReference.Supports(first.ElementType)) host.DrawReference(first.ElementType, null, value => Add(null, value), false, collections.All(collection => collection.Rules?.NotNullItems != true));
                            var entries = NativeAbstractEntries.Combine(collections.Select(source.UICollectionEntries).ToArray());
                            NativeAbstractEntryControl.Choices(entries, null, entry =>
                            {
                                QueueUIPropertyCommand(source, ids, () => host.SelectUICollectionAddFactoryEntry(source, collections, entry, null));
                                Gui.CloseCurrentPopup();
                            });
                            void Add(Type? type, object? value)
                            {
                                QueueUIPropertyCommand(source, ids, () => source.BatchCollectionEdits(() =>
                                { foreach (var collection in collections.DistinctBy(collection => collection.Collection)) source.AddCollectionItem(collection, type, null, value); }, "Add UI collection item"));
                                Gui.CloseCurrentPopup();
                            }
                        }
                        finally { Gui.EndPopup(); }
                    }
                }
                finally { Gui.TreePop(); }
            }
            finally { Gui.PopID(); }
        }
    }
}
