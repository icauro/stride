using Stride.Core.Assets.Quantum;
using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawAggregateTopologyReset(CommonComponentProperty property, IReadOnlyList<CollectionItem>? items = null)
    {
        if (!Gui.BeginPopupContextItem("ResetAggregateTopology")) return;
        if (items == null) DrawAggregatePropertyClipboardCommands(property);
        else DrawAggregateItemClipboardCommands(items);
        var inherited = property.Fields.Select((field, index) => items == null
            ? field.Node is IAssetNode { BaseNode: not null }
            : items[index].Collection.BaseNode is IAssetObjectNode source && source.HasId(items[index].Id)).ToArray();
        var canReset = inherited.Any(value => value) && property.Fields.Select((field, index) => !inherited[index] || (items == null
            ? aggregatePropertyOwners[field].CanResetHierarchyNode(field.Node)
            : aggregatePropertyOwners[field].CanResetHierarchyItem(items[index].Collection, items[index].Index))).All(allowed => allowed);
        if (DrawNativeMenuItem(EditorLocalization.Widget("Reset to base value", "Menu"), "", false, canReset))
            QueueAggregateMutation(property, field =>
            {
                var index = AggregateFieldIndex(property, field); var owner = aggregatePropertyOwners[field];
                if (!inherited[index]) return () => { };
                if (items == null)
                {
                    if (!owner.CanResetHierarchyNode(field.Node)) throw new InvalidOperationException("The selected base property changed.");
                    return () =>
                    {
                        if (!owner.CanResetHierarchyNode(field.Node)) throw new InvalidOperationException("The selected base property changed.");
                        ((IAssetNode)field.Node).ResetOverrideRecursively();
                    };
                }
                var item = items[index];
                if (!owner.CanResetHierarchyItem(item.Collection, item.Index)) throw new InvalidOperationException("The selected base item changed.");
                return () =>
                {
                    if (!item.Collection.HasId(item.Id) || !owner.CanResetHierarchyItem(item.Collection, item.Index)) throw new InvalidOperationException("The selected base item changed.");
                    item.Collection.ResetOverrideRecursively(item.Index);
                };
            }, "Reset to base value");
        Gui.EndPopup();
    }
    private void DrawAggregateTopologyLabel(CommonComponentProperty property)
    {
        var bold = PushPackagePropertiesBoldFont(property.Fields.Any(field => field.Node is IAssetMemberNode member && member.IsContentOverridden()));
        Gui.TextUnformatted(property.First.Label); if (bold) Gui.PopFont();
        DrawAggregateTopologyReset(property);
    }
}
