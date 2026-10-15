using Stride.Core.Assets.Quantum;
using Stride.Core.Quantum;
using Stride.Engine;
using Stride.Assets.Presentation.NodePresenters.Rules;

namespace Stride.Editor.ImGui;

internal abstract partial class EntityHierarchyDocument
{
    internal bool CanCopyProperty(ComponentField field) => IsKnownComponentField(field) && field.Component.Entity is { } entity
        && Asset.Hierarchy.Parts.TryGetValue(entity.Id, out var part) && ReferenceEquals(part.Entity, entity) && entity.Components.Contains(field.Component);
    private bool CanMutateClipboardCollection(IObjectNode collection, IReadOnlyList<ComponentField> fields)
        => CanEditHierarchyNode(collection) && !Flatten(fields).Any(field => ReferenceEquals(field.Node.Target, collection) && PropertyClipboardRules.IsReadOnly(field.Node));
    private NativePropertyClipboard CreatePropertyClipboard(IReadOnlyList<ComponentField> fields, IReadOnlyList<IHierarchyCollectionValue>? items = null)
        => new(HierarchyGraph, Asset,
            member => CanEditHierarchyNode(member) && AssetMetadataDocument.CanAssignAuthoringNode(member)
                && !Flatten(fields).Any(field => ReferenceEquals(field.Node, member) && field.Presentation?.IsReadOnly(field) == true),
            (collection, index) => collection is IAssetObjectNode node && CanMutateClipboardCollection(collection, fields) && CanEditHierarchyItem(node, index),
            collection => CanMutateClipboardCollection(collection, fields) && PropertyClipboardRules.CanAdd(collection)
                && items?.OfType<HierarchyCollectionItem>().Where(item => ReferenceEquals(item.Collection, collection)).All(item => CanAddNestedHierarchyCollection(item.Context)) != false,
            collection => CanMutateClipboardCollection(collection, fields) && PropertyClipboardRules.CanRemove(collection)
                && items?.OfType<HierarchyCollectionItem>().Where(item => ReferenceEquals(item.Collection, collection)).All(item => CanRemoveNestedHierarchyCollection(item.Context)) != false,
            (member, value) => Flatten(fields).FirstOrDefault(field => ReferenceEquals(field.Node, member)) is { } field ? field.NormalizeValue(value)! : value,
            normalizeItem: (collection, index, value) =>
            {
                var item = items?.FirstOrDefault(item => ReferenceEquals(item.Collection, collection) && item.Index == index);
                if (item == null) return value;
                AssertHierarchyItemAxesEditable(item, value);
                return NativeNumericAxes.Normalize(MatrixPropertyValue.Normalize(PropertyValueValidation.Normalize(value, HierarchyItemNumericRange(item)), item.Value),
                    item.Value, item.AxisNumericRanges);
            },
            afterMemberUpdate: (member, _) =>
            {
                InvalidateHierarchyAuthoringPaths();
                if (fields.FirstOrDefault(field => ReferenceEquals(field.Node, member) && IsModelReference(field)) is { } model)
                    ModelMaterialNodeRules.Clear(ModelMaterialsNode((ModelComponent)model.Component));
            },
            acceptItem: (collection, index, value) => value != null || (!Flatten(fields)
                .Any(field => ReferenceEquals(field.Node.Target, collection) && PropertyClipboardRules.NotNullItems(field.Node))
                && items?.Any(item => ReferenceEquals(item.Collection, collection) && item.Index == index && !item.AllowsNull) != true
                && items?.OfType<HierarchyCollectionItem>().Any(item => ReferenceEquals(item.Collection, collection) && item.Context.Rules?.NotNullItems == true) != true));
    internal string CopyProperty(ComponentField field)
    {
        EndEdits();
        return CanCopyProperty(field) ? CreatePropertyClipboard([field]).Copy(new(field.Node, NodeIndex.Empty)) : "";
    }
    internal bool CanPasteProperties(IReadOnlyList<ComponentField> fields, string text)
    {
        if (fields.Count == 0) return false;
        var clipboard = CreatePropertyClipboard(fields);
        return fields.All(field => CanCopyProperty(field) && CanEditHierarchyField(field) && field.Presentation?.IsReadOnly(field) != true
            && (!IsModelReference(field) || CanEditAggregateModelReference(field))
            && !PropertyClipboardRules.IsReadOnly(field.Node) && (field.Node.Target?.IsEnumerable == true || AssetMetadataDocument.CanAssignAuthoringNode(field.Node))
            && clipboard.CanPaste(text, field.DeclaredType));
    }
    internal void PasteProperties(IReadOnlyList<ComponentField> fields, string text, bool replace)
    {
        EndEdits();
        if (!CanPasteProperties(fields, text)) throw new InvalidOperationException("The clipboard cannot be pasted into these properties.");
        var clipboard = CreatePropertyClipboard(fields);
        ApplyInheritedGraphMutation(() =>
        {
            foreach (var field in fields.DistinctBy(field => field.Node)) clipboard.Paste(text, field.DeclaredType, new(field.Node, NodeIndex.Empty), replace);
        }, replace ? "Replace property" : "Paste property");
    }
    internal bool CanCopyPropertyItem(IHierarchyCollectionValue item) => CanCopyProperty(item.Field)
        && (item is CollectionItem regular ? ReferenceEquals(item.Field.Node.Target, item.Collection) && item.Collection.HasId(regular.Id)
            : item is HierarchyCollectionItem nested && CanReadNestedHierarchyCollection(nested.Context)
                && (nested.Id == null || item.Collection.HasId(nested.Id.Value)) && HasHierarchyCollectionIndex(item.Collection, item.Index));
    internal string CopyPropertyItem(IHierarchyCollectionValue item)
    {
        EndEdits();
        return CanCopyPropertyItem(item) ? CreatePropertyClipboard([item.Field], [item]).Copy(new(item.Collection, item.Index)) : "";
    }
    internal bool CanPastePropertyItems(IReadOnlyList<IHierarchyCollectionValue> items, string text)
        => items.Count != 0 && items.All(item => CanCopyPropertyItem(item) && (item is not HierarchyCollectionItem nested || CanEditNestedHierarchyCollection(nested.Context)) && CanEditHierarchyField(item.Field)
            && CanEditHierarchyItem(item.Collection, item.Index) && !PropertyClipboardRules.IsReadOnly(item.Field.Node)
            && CreatePropertyClipboard([item.Field], [item]).CanPaste(text, item.Collection.Type));
    internal void PastePropertyItems(IReadOnlyList<IHierarchyCollectionValue> items, string text, bool replace)
    {
        EndEdits();
        if (!CanPastePropertyItems(items, text)) throw new InvalidOperationException("The clipboard cannot be pasted into these collection items.");
        var clipboard = CreatePropertyClipboard(items.Select(item => item.Field).Distinct().ToArray(), items);
        ApplyInheritedGraphMutation(() =>
        {
            foreach (var item in items.DistinctBy(item => (item.Collection, item.IdentityKey)))
                clipboard.Paste(text, item.Collection.Type, new(item.Collection, item.Index), replace);
        }, replace ? "Replace property" : "Paste property");
    }
}
