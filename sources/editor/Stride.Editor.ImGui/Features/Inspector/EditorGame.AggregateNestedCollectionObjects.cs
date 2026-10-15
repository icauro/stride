using Hexa.NET.ImGui;
using Stride.Core;
using Stride.Core.Assets.Quantum;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawNestedHierarchyObjectType(CommonComponentProperty property, IReadOnlyList<HierarchyCollectionContext> contexts, IReadOnlyList<HierarchyCollectionItem> items)
        => DrawHierarchyItemFactory(property, items.Cast<IHierarchyCollectionValue>().ToArray());
    private void DrawNestedHierarchyObjectFields(CommonComponentProperty property, IReadOnlyList<HierarchyCollectionItem> items)
    {
        static IEnumerable<ComponentField> Leaves(IEnumerable<ComponentField> fields)
        {
            foreach (var field in fields)
            {
                if (field.IsIndexedCollection || field.IsDictionary || field.SupportsEditing) yield return field;
                else { if (field.SupportsObjectLifecycle) yield return field; foreach (var child in Leaves(field.Children)) yield return child; }
            }
        }
        var maps = items.Select((item, index) =>
        {
            var owner = aggregatePropertyOwners[property.Fields[index]]; var prefix = item.IdentityKey + "/";
            return Leaves(owner.GetNestedHierarchyItemFields(item)).ToDictionary(field => field.Name[prefix.Length..]);
        }).ToArray();
        foreach (var entry in maps[0])
        {
            var fields = new List<ComponentField> { entry.Value };
            foreach (var map in maps.Skip(1))
            {
                if (!map.TryGetValue(entry.Key, out var field) || field.DeclaredType != entry.Value.DeclaredType
                    || !Equals(field.Node.MemberDescriptor.MemberInfo, entry.Value.Node.MemberDescriptor.MemberInfo)) break;
                fields.Add(field);
            }
            if (fields.Count != maps.Length) continue;
            for (var index = 0; index < fields.Count; index++) aggregatePropertyOwners[fields[index]] = aggregatePropertyOwners[property.Fields[index]];
            DrawAggregateProperty(new(property.Key + "/item/" + items[0].IdentityKey + "/" + entry.Key, property.ComponentLabel, fields));
        }
    }
    private void DrawNestedHierarchyReset(CommonComponentProperty property, IReadOnlyList<HierarchyCollectionContext> contexts, IReadOnlyList<HierarchyCollectionItem>? items = null)
    {
        if (!Gui.BeginPopupContextItem("ResetNestedCollection")) return;
        if (items != null) DrawAggregateItemClipboardCommands(items.Cast<IHierarchyCollectionValue>().ToArray());
        else if (contexts.All(context => context.Parent != null))
            DrawAggregateItemClipboardCommands(contexts.Select(context => (IHierarchyCollectionValue)context.Parent!).ToArray());
        else DrawAggregatePropertyClipboardCommands(property);
        var owners = property.Fields.Select(field => aggregatePropertyOwners[field]).ToArray();
        IReadOnlyList<HierarchyCollectionItem?> targets = items ?? contexts.Select(context => context.Parent).ToArray();
        var inherited = contexts.Select((context, index) => targets[index] is { } item
            ? item.Id is { } id && item.Collection.BaseNode is IAssetObjectNode source && source.HasId(id)
            : context.Origin.Node is IAssetNode { BaseNode: not null }).ToArray();
        var enabled = inherited.Any(value => value) && contexts.Select((context, index) => !inherited[index] || (targets[index] is { } item
            ? owners[index].CanResetHierarchyItem(item.Collection, item.Index)
            : owners[index].CanResetHierarchyNode(context.Origin.Node))).All(allowed => allowed);
        if (DrawNativeMenuItem(EditorLocalization.Widget("Reset to base value", "Menu"), "", false, enabled)) QueueNestedAggregateMutation(property, contexts, (owner, context, index) =>
        {
            if (!inherited[index]) return () => { };
            if (targets[index] is { } item)
            {
                if (!owner.CanResetHierarchyItem(item.Collection, item.Index)) throw new InvalidOperationException("The inherited collection item changed.");
                return () =>
                {
                    if (item.Id is { } id && !item.Collection.HasId(id) || !owner.CanResetHierarchyItem(item.Collection, item.Index)) throw new InvalidOperationException("The inherited collection item changed.");
                    item.Collection.ResetOverrideRecursively(item.Index);
                };
            }
            if (!owner.CanResetHierarchyNode(context.Origin.Node)) throw new InvalidOperationException("The inherited collection changed.");
            return () =>
            {
                if (!owner.CanResetHierarchyNode(context.Origin.Node)) throw new InvalidOperationException("The inherited collection changed.");
                ((IAssetNode)context.Origin.Node).ResetOverrideRecursively();
            };
        }, "Reset to base value", requireCollectionEdit: false);
        Gui.EndPopup();
    }
}
