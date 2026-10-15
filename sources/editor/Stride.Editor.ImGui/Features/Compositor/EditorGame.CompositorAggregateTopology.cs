using Hexa.NET.ImGui;
using Stride.Core;
using Stride.Core.Extensions;
using Stride.Core.Quantum;
using Stride.Core.Reflection;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawCompositorAggregateObject(AssetMetadataDocument document, CompositorEditorState state, CompositorAggregateProperty property)
    {
        Gui.PushID(property.Key);
        DrawCompositorAggregateEnabled(document, property);
        if (property.InlineMember is { } inline)
        {
            DrawCompositorAggregateProperty(document, state, inline);
            if (property.Details.Count > 0 || HasCompositorNativeChildren(property.First))
            {
                Gui.SetNextItemOpen(property.First.Expansion != ExpandRule.Never, ImGuiCond.Once);
                if (Gui.TreeNodeEx("##Inline children")) { DrawCompositorAggregateChildren(document, state, property); Gui.TreePop(); }
            }
            Gui.PopID(); return;
        }
        Gui.SetNextItemOpen(property.First.Expansion != ExpandRule.Never, ImGuiCond.Once);
        var expanded = Gui.TreeNodeEx("##Object"); Gui.SameLine(); DrawCompositorLabel(property); ShowCompositorDocumentation(property);
        var type = property.Value?.GetType(); var mixed = property.Fields.Any(field => field.Accessor.RetrieveValue()?.GetType() != type);
        if (property.First.Types.Count > 0 || property.Value == null && ObjectFactoryRegistry.CanCreateInstance(property.First.Type))
        {
            Gui.SameLine(); Gui.BeginDisabled(!document.CanEditCompositorAggregate(property));
            if (Gui.BeginCombo("##Type", mixed ? DifferentPropertyValues : type == null ? "None" : DisplayAttribute.GetDisplayName(type)))
            {
                if (property.Fields.All(field => field.AllowsNull) && Gui.Selectable(EditorLocalization.Widget("None", "Button"), !mixed && type == null)) Apply(null);
                foreach (var candidate in property.First.Types.Count == 0 ? new[] { property.First.Type } : property.First.Types)
                    if (property.Fields.All(field => candidate == field.Type || field.Types.Contains(candidate)) && Gui.Selectable(DisplayAttribute.GetDisplayName(candidate), !mixed && candidate == type)) Apply(candidate);
                Gui.EndCombo();
            }
            Gui.EndDisabled();
        }
        else if (!property.First.Type.IsValueType && property.Fields.All(field => field.AllowsNull))
        {
            Gui.SameLine(); Gui.BeginDisabled(!document.CanEditCompositorAggregate(property));
            if (Gui.SmallButton("−")) Apply(null); Gui.EndDisabled();
        }
        DrawCompositorAggregateTopologyReset(document, property);
        if (expanded) { DrawCompositorAggregateChildren(document, state, property); Gui.TreePop(); }
        Gui.PopID();
        void Apply(Type? candidate) { UseTextureSettingsCommandContext(); ExecuteCompositorCommand(document, () => document.CreateCompositorAggregateObjects(property, candidate)); }
    }
    private void DrawCompositorAggregateCollection(AssetMetadataDocument document, CompositorEditorState state, CompositorAggregateProperty property)
    {
        Gui.PushID(property.Key);
        Gui.SetNextItemOpen(property.First.Expansion != ExpandRule.Never, ImGuiCond.Once);
        var expanded = Gui.TreeNodeEx("##Collection"); Gui.SameLine(); DrawCompositorLabel(property); ShowCompositorDocumentation(property);
        Action? command = null;
        Gui.SameLine();
        if (property.First.Collection!.Descriptor is not DictionaryDescriptor)
            DrawNativeCompositorCollectionAdd(document, property.Fields, action => command = action);
        else DrawNativeCompositorDictionaryAdd(document, property.Fields);
        DrawCompositorAggregateTopologyReset(document, property);
        if (expanded)
        {
            foreach (var item in property.Items)
            {
                if (!item.Fields.Any(document.IsCompositorPropertyVisible)) continue;
                Gui.PushID(item.Key); Gui.BeginGroup();
                Gui.BeginDisabled(!document.CanRemoveCompositorAggregateItems(item));
                if (Gui.SmallButton("−")) command = () => document.RemoveCompositorAggregateItems(item);
                Gui.EndDisabled(); Gui.SameLine();
                DrawCompositorAggregateProperty(document, state, item); Gui.EndGroup();
                DrawCompositorAggregateReorder(document, state, property, item, action => command = action);
                if (Gui.BeginPopupContextItem("##Item actions"))
                {
                    DrawCompositorClipboardActions(document, item.Fields);
                    if (DrawNativeMenuItem(EditorLocalization.Widget("Reset to base value", "Menu"), string.Empty, false, document.CanResetCompositorAggregate(item))) command = () => document.ResetCompositorAggregate(item);
                    if (item.First.Accessor.Node.Descriptor is DictionaryDescriptor dictionary && NativeDictionaryKey.SupportsText(dictionary.KeyType))
                        DrawRetainedCompositorDictionaryKey(document, item.Fields, item);
                    if (item.First.Accessor.Index.IsInt)
                    {
                        var index = item.First.Accessor.Index.Int;
                        if (DrawNativeMenuItem(EditorLocalization.Widget("Move up", "Menu"), string.Empty, false, document.CanMoveCompositorAggregateItems(item, index - 1))) command = () => document.MoveCompositorAggregateItems(item, index - 1);
                        if (DrawNativeMenuItem(EditorLocalization.Widget("Move down", "Menu"), string.Empty, false, document.CanMoveCompositorAggregateItems(item, index + 1))) command = () => document.MoveCompositorAggregateItems(item, index + 1);
                    }
                    DrawCompositorItemPresenterCommands(document, item.Fields);
                    Gui.EndPopup();
                }
                Gui.PopID();
            }
            if (property.Fields.All(field => field.NativePresenter != null))
                DrawCompositorNativeChildren(document, property.Fields.Select(field => field.NativePresenter!).ToArray(), additionalCollectionChildren: true);
            Gui.TreePop();
        }
        Gui.PopID();
        if (command != null) { UseTextureSettingsCommandContext(); ExecuteCompositorCommand(document, command); }
    }
    private void DrawCompositorAggregateEnabled(AssetMetadataDocument document, CompositorAggregateProperty property)
    {
        if (property.EnabledMember is not { } enabled) return;
        var value = enabled.Value is true;
        Gui.BeginDisabled(!document.CanEditCompositorAggregate(enabled)); Gui.PushItemFlag((ImGuiItemFlags)ImGuiItemFlagsPrivate.MixedValue, enabled.IsMixed);
        if (Gui.Checkbox("##Enabled", ref value)) { UseTextureSettingsCommandContext(); ExecuteCompositorCommand(document, () => document.EditCompositorAggregate(enabled, _ => value)); }
        Gui.PopItemFlag(); Gui.EndDisabled(); Gui.SameLine();
    }
    private void DrawCompositorAggregateTopologyReset(AssetMetadataDocument document, CompositorAggregateProperty property)
    {
        if (property.First.Accessor.IsItem) return;
        if (!Gui.BeginPopupContextItem("##Reset topology")) return;
        DrawCompositorClipboardActions(document, property.Fields);
        if (DrawNativeMenuItem(EditorLocalization.Widget("Reset to base value", "Menu"), string.Empty, false, document.CanResetCompositorAggregate(property)))
        { UseTextureSettingsCommandContext(); ExecuteCompositorCommand(document, () => document.ResetCompositorAggregate(property)); }
        if (property.Fields.Any(field => field.NativePresenter != null)) DrawCompositorItemPresenterCommands(document, property.Fields);
        Gui.EndPopup();
    }
    private void DrawCompositorAggregatePropertyGroups(AssetMetadataDocument document, CompositorEditorState state,
        IReadOnlyList<CompositorAggregateProperty> properties, IReadOnlyList<IObjectNode>? roots = null)
    {
        if (properties.All(property => property.Fields.All(field => field.Accessor.Node is IMemberNode))
            && DrawNativeCompositorAggregateRows(document, state, properties, roots)) return;
        foreach (var group in document.GetCompositorAggregateGroups(properties))
        {
            var expanded = true;
            if (group.Name.Length > 0)
            {
                Gui.PushID(group.Name); Gui.SetNextItemOpen(group.Expand != ExpandRule.Never, ImGuiCond.Once);
                expanded = Gui.TreeNodeEx("##Category"); Gui.SameLine(); Gui.TextUnformatted(group.Name);
            }
            if (expanded) foreach (var property in group.Fields) DrawCompositorAggregateProperty(document, state, property);
            if (group.Name.Length > 0) { if (expanded) Gui.TreePop(); Gui.PopID(); }
        }
    }
    private unsafe void DrawCompositorAggregateReorder(AssetMetadataDocument document, CompositorEditorState state, CompositorAggregateProperty parent,
        CompositorAggregateProperty item, Action<Action> defer)
    {
        if (!item.First.Accessor.Index.IsInt || !document.CanMoveCompositorAggregateItems(item, item.First.Accessor.Index.Int)) return;
        if (Gui.BeginDragDropSource(ImGuiDragDropFlags.SourceAllowNullId))
        {
            if (!ReferenceEquals(state.AggregateDrag, item)) { state.AggregateDrag = item; state.ItemDragToken = Guid.NewGuid(); }
            var token = state.ItemDragToken; Gui.SetDragDropPayload("STRIDE_COMPOSITOR_AGGREGATE", &token, (nuint)sizeof(Guid));
            Gui.TextUnformatted(item.Label); Gui.EndDragDropSource();
        }
        if (!Gui.BeginDragDropTarget()) return;
        var payload = Gui.AcceptDragDropPayload("STRIDE_COMPOSITOR_AGGREGATE");
        if (!payload.IsNull && payload.DataSize == sizeof(Guid) && payload.IsDelivery() && *(Guid*)payload.Data == state.ItemDragToken
            && state.AggregateDrag is { } source && parent.Items.Contains(source) && !ReferenceEquals(source, item))
        {
            var insertion = item.First.Accessor.Index.Int + (Gui.GetIO().MousePos.Y >= (Gui.GetItemRectMin().Y + Gui.GetItemRectMax().Y) * 0.5f ? 1 : 0);
            var destination = Math.Clamp(insertion - (source.First.Accessor.Index.Int < insertion ? 1 : 0), 0, parent.Items.Count - 1);
            if (document.CanMoveCompositorAggregateItems(source, destination)) defer(() => document.MoveCompositorAggregateItems(source, destination));
            state.AggregateDrag = null;
        }
        Gui.EndDragDropTarget();
    }
}
