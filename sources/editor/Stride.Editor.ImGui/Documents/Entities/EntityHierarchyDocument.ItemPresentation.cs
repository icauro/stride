using Stride.Core.Assets.Editor.Quantum.NodePresenters.Commands;
using Stride.Core.Assets.Quantum;
using Stride.Core.Quantum;

namespace Stride.Editor.ImGui;

internal abstract partial class EntityHierarchyDocument
{
    internal bool IsHierarchyItemVisible(IHierarchyCollectionValue item)
        => propertyPresenters.All(presentation => presentation.IsItemVisible(item.Collection, item.Index));
    internal string? HierarchyItemLabel(IHierarchyCollectionValue item)
        => propertyPresenters.Select(presentation => presentation.ItemLabel(item.Collection, item.Index)).FirstOrDefault(label => label != null);
    internal PropertyNumericRange? HierarchyItemNumericRange(IHierarchyCollectionValue item)
        => propertyPresenters.Select(presentation => presentation.ItemNumericRange(item.Collection, item.Index, item.Type)).FirstOrDefault(range => range != null);
    internal bool IsHierarchyItemAxisReadOnly(IHierarchyCollectionValue item, int axis)
        => item.Value == null && (item.Type == typeof(Stride.Core.Mathematics.Rectangle) || item.Type == typeof(Stride.Core.Mathematics.RectangleF))
            || propertyPresenters.Any(presentation => presentation.IsItemAxisReadOnly(item.Collection, item.Index, axis));
    internal void AssertHierarchyItemAxesEditable(IHierarchyCollectionValue item, object? proposed)
    { foreach (var presentation in propertyPresenters) presentation.AssertItemAxesEditable(item.Collection, item.Index, item.Value, proposed); }
    internal IReadOnlyList<AbstractNodeEntry> HierarchyItemEntries(IHierarchyCollectionValue item)
    {
        var supplied = HierarchyItemNativeEntries(item);
        var types = item is CollectionItem legacy ? CollectionItemTypes(legacy.Field)
            : NestedHierarchyItemTypes(((HierarchyCollectionItem)item).Context);
        return NativeAbstractEntries.Available(supplied, types, item.AllowsNull);
    }
    internal IEnumerable<AbstractNodeEntry>? HierarchyItemNativeEntries(IHierarchyCollectionValue item)
        => propertyPresenters.Select(presentation => presentation.ItemEntries(item.Collection, item.Index)).FirstOrDefault(entries => entries != null)
            ?? propertyPresenters.Select(presentation => presentation.CollectionEntries(item.Collection)).FirstOrDefault(entries => entries != null);
    internal IEnumerable<AbstractNodeEntry>? HierarchyCollectionNativeEntries(HierarchyCollectionContext context)
        => propertyPresenters.Select(presentation => presentation.CollectionEntries(context.Node)).FirstOrDefault(entries => entries != null);
    internal IReadOnlyList<AbstractNodeEntry> HierarchyCollectionEntries(HierarchyCollectionContext context)
        => NativeAbstractEntries.Available(HierarchyCollectionNativeEntries(context),
            NestedHierarchyItemTypes(context), context.Rules?.NotNullItems != true && (!context.ElementType!.IsValueType || Nullable.GetUnderlyingType(context.ElementType) != null), true);
    internal EditorPresenterTarget? AcquireHierarchyItemTarget(IAssetObjectNode collection, NodeIndex index, bool collectionRoot = false)
    {
        foreach (var presentation in propertyPresenters)
            if (presentation.AcquireItemTarget(collection, index, collectionRoot) is { } target) return target;
        return null;
    }
}
