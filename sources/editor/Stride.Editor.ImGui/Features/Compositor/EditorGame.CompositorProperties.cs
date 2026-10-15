using Hexa.NET.ImGui;
using Stride.Core;
using Stride.Core.Annotations;
using Stride.Core.Extensions;
using Stride.Core.Quantum;
using Stride.Core.Reflection;
using Stride.Core.Mathematics;
using Stride.Rendering;
using Stride.Rendering.Compositing;
using Gui = Hexa.NET.ImGui.ImGui;
using NVector2 = System.Numerics.Vector2;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private bool TryDrawSelectedCompositorProperties()
    {
        if (documents.Active?.Document is not AssetMetadataDocument { CompositorAsset: not null } source
            || sessionHistory?.Registry.Find(source.AuthoredAsset.Id) is not { IsPublished: true, IsDisposed: false, Document: var owner }
            || !ReferenceEquals(owner, source) || !compositorEditors.TryGetValue(source, out var state)) return false;
        if (state.SelectedLinks.Count != 0 && state.PropertySelection == null) return true;
        if (state.PropertySelection == null || state.SelectedBlocks.Count + state.SelectedItems.Count == 0) return false;
        // Native graph selection clears competing browser selection. Its own
        // retained canonical selection still owns these Property grid rows.
        DrawAssetMetadataDocument(source, inPropertyGrid: true);
        return true;
    }

    private void DrawCompositorSelectedProperties(AssetMetadataDocument document)
    {
        if (document.CompositorAsset == null || !compositorEditors.TryGetValue(document, out var state) || state.PropertySelection is not { } selection) return;
        if (state.SelectedBlocks.Count + state.SelectedItems.Count > 1) { DrawCompositorAggregateProperties(document, state); return; }
        var block = selection.Block is { } id ? document.CompositorBlocks.FirstOrDefault(block => block.Id == id) : null;
        var item = selection.Item is { } itemId ? document.CompositorItems.FirstOrDefault(item => item.ItemId == itemId) : null;
        var node = block?.Node ?? item?.Node;
        if (node == null) return;
        document.SetCompositorPresentationRoots([(node, block?.Id == Guid.Empty)]);
        Gui.TextUnformatted(block?.Title ?? item!.Name);
        if (node.Retrieve() is Stride.Core.Yaml.IUnloadable unknown) { DrawUnloadableProperty(unknown); return; }
        BeginAssetPresenterRows(document);
        DrawCompositorProperties(document, document.GetCompositorProperties(node, block?.Id == Guid.Empty), node);
    }
    private void DrawCompositorProperty(AssetMetadataDocument document, CompositorProperty property)
    {
        Gui.PushID(property.Key);
        var value = document.CompositorPropertyValue(property);
        Action? command = null;
        using var referenceCommit = new ContentReferenceCommitPhase(action => command = action, action => ExecuteCompositorCommand(document, action));
        if (value is Stride.Core.Yaml.IUnloadable unknown)
        {
            if (!DrawNativeCompositorValueFactory(document, [property])) DrawCompositorLabel(property);
            DrawUnloadableProperty(unknown);
        }
        else if (property.IsNullable && value == null)
        {
            DrawCompositorLabel(property); ShowCompositorDocumentation(property); Gui.SameLine(); Gui.TextUnformatted(EditorLocalization.Text("(null)")); Gui.SameLine();
            Gui.BeginDisabled(!document.CanEditCompositorProperty(property));
            if (Gui.SmallButton("+")) command = () => SetCompositorPropertyValue(document, property, Activator.CreateInstance(property.Type));
            Gui.EndDisabled();
        }
        else if (property.Collection is { } collection)
        {
            var expanded = Gui.TreeNodeEx("##Collection", ImGuiTreeNodeFlags.DefaultOpen); Gui.SameLine(); DrawCompositorLabel(property); ShowCompositorDocumentation(property);
            Gui.SameLine();
            if (collection.Descriptor is not DictionaryDescriptor)
                DrawNativeCompositorCollectionAdd(document, [property], action => command = action);
            else DrawNativeCompositorDictionaryAdd(document, [property]);
            if (expanded)
            {
                foreach (var item in property.Items)
                {
                    if (!document.IsCompositorPropertyVisible(item)) continue;
                    Gui.PushID(item.ItemId.ToString());
                    Gui.BeginDisabled(!document.CanEditCompositorProperty(item) || !document.CanRemoveCompositorPropertyItems(property));
                    if (Gui.SmallButton("−")) { var chosen = item; command = () => document.RemoveCompositorPropertyItem(chosen); }
                    Gui.EndDisabled(); Gui.SameLine(); DrawCompositorProperty(document, item);
                    DrawCompositorPropertyReorder(document, property, item, moved => command = moved);
                    if (Gui.BeginPopupContextItem("##Item actions"))
                    {
                        DrawCompositorClipboardActions(document, [item]);
                        if (DrawNativeMenuItem(EditorLocalization.Widget("Reset to base value", "Menu"), string.Empty, false, document.CanEditCompositorProperty(item) && document.CanResetAssetSettingItem(collection, item.Accessor.Index)))
                        { var chosen = item; command = () => document.ResetAssetSettingItem(collection, chosen.Accessor.Index); }
                        if (collection.Descriptor is DictionaryDescriptor dictionary && NativeDictionaryKey.SupportsText(dictionary.KeyType))
                            DrawRetainedCompositorDictionaryKey(document, [item]);
                        if (property.Rules?.CanReorderItems == true && item.Accessor.Index.IsInt)
                        {
                            Gui.BeginDisabled(!document.CanEditCompositorProperty(item) || !document.CanRemoveCompositorPropertyItems(property));
                            if (DrawNativeMenuItem(EditorLocalization.Widget("Move up", "Menu"), string.Empty, false, item.Accessor.Index.Int > 0)) { var chosen = item; command = () => document.MoveCompositorPropertyItem(chosen, chosen.Accessor.Index.Int - 1); }
                            if (DrawNativeMenuItem(EditorLocalization.Widget("Move down", "Menu"), string.Empty, false, item.Accessor.Index.Int < property.Items.Count - 1)) { var chosen = item; command = () => document.MoveCompositorPropertyItem(chosen, chosen.Accessor.Index.Int + 1); }
                            Gui.EndDisabled();
                        }
                        DrawCompositorItemPresenterCommands(document, [item]);
                        Gui.EndPopup();
                    }
                    Gui.PopID();
                }
                if (property.NativePresenter is { } collectionPresenter)
                    DrawCompositorNativeChildren(document, [collectionPresenter], additionalCollectionChildren: true);
                Gui.TreePop();
            }
        }
        else if (DrawNativeCompositorValueFactory(document, [property])) { }
        else if (AssetMetadataDocument.IsCompositorReferenceType(property.Type, value))
        {
            DrawCompositorLabel(property);
            Gui.BeginDisabled(!document.CanEditCompositorProperty(property));
            if (Gui.BeginCombo("##Value", CompositorReferenceName(document, value)))
            {
                if (property.AllowsNull && property.Accessor.AcceptValue(null) && Gui.Selectable(EditorLocalization.Widget("None##Null", "Button"), value == null)) command = () => SetCompositorPropertyValue(document, property, null);
                foreach (var renderer in document.CompositorAsset!.SharedRenderers)
                    if (property.Accessor.AcceptValue(renderer) && Gui.Selectable(CompositorReferenceName(renderer) + "##" + renderer.Id, ReferenceEquals(value, renderer)))
                    { var chosen = renderer; command = () => SetCompositorPropertyValue(document, property, chosen); }
                foreach (var stage in document.CompositorAsset.RenderStages)
                    if (property.Accessor.AcceptValue(stage) && Gui.Selectable(stage.Name + "##" + stage.Id, ReferenceEquals(value, stage)))
                    { var chosen = stage; command = () => SetCompositorPropertyValue(document, property, chosen); }
                if (property.Type == typeof(SceneCameraSlotId))
                {
                    if (Gui.Selectable(EditorLocalization.Widget("None##EmptyCameraSlot", "Button"), value is SceneCameraSlotId { IsEmpty: true })) command = () => SetCompositorPropertyValue(document, property, default(SceneCameraSlotId));
                    foreach (var choice in document.GetCompositorCameraSlotChoices())
                        if (Gui.Selectable(choice.Label + "##" + choice.Value.Id, Equals(value, choice.Value)))
                        { var chosen = choice.Value; command = () => SetCompositorPropertyValue(document, property, chosen); }
                }
                else foreach (var camera in document.CompositorAsset.Cameras)
                    if (property.Accessor.AcceptValue(camera) && Gui.Selectable(camera.Name + "##" + camera.Id, ReferenceEquals(value, camera)))
                    { var chosen = camera; command = () => SetCompositorPropertyValue(document, property, chosen); }
                foreach (var type in property.Types)
                    if (Gui.Selectable(DisplayAttribute.GetDisplayName(type))) { var chosen = type; command = () => document.CreateCompositorPropertyObject(property, chosen); }
                Gui.EndCombo();
            }
            Gui.EndDisabled();
        }
        else if (ContentAssetReference.Supports(property.Type))
        {
            DrawCompositorLabel(property); ShowCompositorDocumentation(property);
            Gui.BeginDisabled(!document.CanEditCompositorProperty(property));
            DrawContentReferenceValue(property.Type, value, property.AllowsNull,
                chosen => SetCompositorPropertyValue(document, property, chosen));
            Gui.EndDisabled();
        }
        else if (property.IsNavigationGroup)
        {
            DrawCompositorLabel(property); ShowCompositorDocumentation(property);
            DrawNavigationGroupValue(value, false, document.CanEditCompositorProperty(property), chosen => SetCompositorPropertyValue(document, property, chosen));
        }
        else if (property.InlineMember is { } inline)
        {
            DrawCompositorEnabled(document, property);
            DrawCompositorProperty(document, inline);
            if (property.Details.Count > 0 || HasCompositorNativeChildren(property))
            {
                Gui.SetNextItemOpen(property.Expansion != ExpandRule.Never, ImGuiCond.Once);
                if (Gui.TreeNodeEx("##Inline children")) { DrawCompositorPropertyChildren(document, property); Gui.TreePop(); }
            }
        }
        else if (!IsCompositorExtraScalarType(property.Type) && (property.Children.Count > 0 || HasCompositorNativeChildren(property) || property.Types.Count > 0 || value == null && !property.Type.IsValueType && property.Type != typeof(string)))
        {
            DrawCompositorEnabled(document, property);
            Gui.SetNextItemOpen(property.Expansion != ExpandRule.Never, ImGuiCond.Once);
            var expanded = Gui.TreeNodeEx("##Object"); Gui.SameLine(); DrawCompositorLabel(property); ShowCompositorDocumentation(property);
            if (property.Types.Count > 0 || value == null && ObjectFactoryRegistry.CanCreateInstance(property.Type))
            {
                Gui.SameLine(); Gui.BeginDisabled(!document.CanEditCompositorProperty(property));
                if (Gui.BeginCombo("##Type", value == null ? "None" : DisplayAttribute.GetDisplayName(value.GetType())))
                {
                    if (property.AllowsNull && property.Accessor.AcceptValue(null) && Gui.Selectable(EditorLocalization.Widget("None", "Button"), value == null)) command = () => document.CreateCompositorPropertyObject(property, null);
                    foreach (var type in property.Types.Count > 0 ? property.Types : [property.Type])
                        if (Gui.Selectable(DisplayAttribute.GetDisplayName(type), value?.GetType() == type)) { var chosen = type; command = () => document.CreateCompositorPropertyObject(property, chosen); }
                    Gui.EndCombo();
                }
                Gui.EndDisabled();
            }
            if (expanded) { DrawCompositorPropertyChildren(document, property); Gui.TreePop(); }
        }
        else
        {
            Gui.BeginDisabled(!document.CanEditCompositorProperty(property) && property.Type != typeof(string)
                && !AssetSettingNumericMetadata.Supports(property.Type) && !UsesCompositorRetainedMath(property.Type) && !UsesCompositorRetainedColor(property.Type) && !UsesCompositorRetainedSpecial(property.Type) && !UsesCompositorRetainedTemporal(property.Type) && !IsPathType(property.Type));
            if (property.IsNullable && property.AllowsNull)
            {
                using (ImGuiScopes.Disabled(!document.CanEditCompositorProperty(property)))
                    if (Gui.SmallButton("−")) command = () => SetCompositorPropertyValue(document, property, null);
                Gui.SameLine();
            }
            var changed = false; var deactivated = false; object? updated = value;
            if (!IsCompositorExtraScalarType(property.Type) && !IsCompositorNumericVector(property.Type)
                && !AssetSettingNumericMetadata.Supports(property.Type) && !UsesCompositorRetainedColor(property.Type) && !property.UsesRangedNumericTemplate && !(property.Type.IsEnum && icons!.HasEnumImages(property.Type))) DrawCompositorLabel(property);
            if (AssetSettingNumericMetadata.Supports(property.Type))
            { DrawCompositorLabel(property); DrawCompositorRetainedNumeric(document, [property]); }
            else if (UsesCompositorRetainedMath(property.Type))
            { DrawCompositorLabel(property); DrawCompositorRetainedMath(document, [property]); }
            else if (UsesCompositorRetainedColor(property.Type))
            { DrawCompositorLabel(property); DrawCompositorRetainedColor(document, [property]); }
            else if (UsesCompositorRetainedSpecial(property.Type))
            { DrawCompositorLabel(property); DrawCompositorRetainedSpecial(document, [property]); }
            else if (UsesCompositorRetainedTemporal(property.Type))
            { DrawCompositorLabel(property); DrawCompositorRetainedTemporal(document, [property]); }
            else if (IsPathType(property.Type))
            { DrawCompositorLabel(property); DrawCompositorRetainedPath(document, [property]); }
            else if (property.Type == typeof(bool)) { var v = (bool)value!; changed = Gui.Checkbox("##Value", ref v); Controls.Record("compositor/property/" + property.Key); updated = v; }
            else if (property.Type == typeof(int)) { changed = DrawCompositorIntegerInput("##Value", ImGuiDataType.S32, (int)value!, property.Range, out var v); updated = v; }
            else if (property.Type == typeof(float)) { var v = (float)value!; changed = Gui.InputFloat("##Value", ref v,
                0, 0, property.Range?.Format ?? "%.9g"); changed |= StepCompositorInput(ref v, property.Range); updated = v; }
            else if (property.Type == typeof(double)) { var v = (double)value!; changed = Gui.InputDouble("##Value", ref v,
                0, 0, property.Range?.Format ?? "%.17g"); changed |= StepCompositorInput(ref v, property.Range); updated = v; }
            else if (property.Type == typeof(string)) DrawCompositorRetainedString(document, [property]);
            else if (property.Type.IsEnum && icons!.HasEnumImages(property.Type)) changed = DrawCompositorEnumArtwork(property, value!, out updated);
            else if (property.Type.IsEnum)
            {
                if (Gui.BeginCombo("##Value", value?.ToString()))
                {
                    var flags = property.Type.IsDefined(typeof(FlagsAttribute), false); var current = EnumBits(value!);
                    for (var enumIndex = 0; enumIndex < property.EnumNames.Length; enumIndex++)
                    {
                        var name = property.EnumNames[enumIndex];
                        var option = Enum.Parse(property.Type, name); var mask = EnumBits(option);
                        if (Gui.Selectable(property.EnumLabels[enumIndex], flags ? mask == 0 ? current == 0 : (current & mask) == mask : Equals(option, value),
                            flags ? ImGuiSelectableFlags.NoAutoClosePopups : ImGuiSelectableFlags.None))
                        { updated = flags ? EnumFromBits(property.Type, mask == 0 ? 0UL : (current & mask) == mask ? current & ~mask : current | mask) : option; changed = true; }
                    }
                    if (flags && DrawFlagBulkActions() is { } bulk)
                    { updated = ApplyFlagBulkAction((Enum)value!, bulk); changed = true; }
                    Gui.EndCombo();
                }
            }
            else if (IsCompositorExtraScalarType(property.Type)) changed = DrawCompositorExtraScalar(document, property, value, out updated, out deactivated);
            else DrawSelectableText("##Value", value?.ToString() ?? "", true, new NVector2(0, Gui.GetTextLineHeight()));
            if (changed) { var chosen = updated; command = () => document.UpdateCompositorProperty(property, chosen); }
            var commit = deactivated || Gui.IsItemDeactivatedAfterEdit() || changed && (property.Type.IsEnum || property.Type == typeof(bool)
                || AssetSettingNumericMetadata.Supports(property.Type) && (Gui.IsKeyPressed(ImGuiKey.UpArrow) || Gui.IsKeyPressed(ImGuiKey.DownArrow)
                    || Gui.IsKeyPressed(ImGuiKey.PageUp) || Gui.IsKeyPressed(ImGuiKey.PageDown)));
            ShowCompositorDocumentation(property);
            Gui.EndDisabled();
            if (command != null) { UseTextureSettingsCommandContext(); ExecuteCompositorCommand(document, command); command = null; }
            if (commit) ExecuteCompositorCommand(document, document.EndCompositorSettingEdit);
        }
        DrawCompositorPropertyContext(document, property);
        if (command != null) { UseTextureSettingsCommandContext(); ExecuteCompositorCommand(document, command); }
        Gui.PopID();
    }
    private void SetCompositorPropertyValue(AssetMetadataDocument document, CompositorProperty property, object? value)
    {
        UseTextureSettingsCommandContext(); ExecuteCompositorCommand(document, () => { document.UpdateCompositorProperty(property, value); document.EndCompositorSettingEdit(); });
    }
    private void DrawCompositorEnabled(AssetMetadataDocument document, CompositorProperty property)
    {
        if (property.EnabledMember is not { } enabled) return;
        var value = (bool)document.CompositorPropertyValue(enabled)!;
        Gui.BeginDisabled(!document.CanEditCompositorProperty(enabled));
        if (Gui.Checkbox("##Enabled", ref value)) SetCompositorPropertyValue(document, enabled, value);
        Controls.Record("compositor/property/" + enabled.Key);
        Gui.EndDisabled(); Gui.SameLine();
    }
    private void DrawCompositorProperties(AssetMetadataDocument document, IReadOnlyList<CompositorProperty> properties, IObjectNode? root = null)
    {
        if (properties.All(property => property.Accessor.Node is IMemberNode))
        {
            using var rootTarget = root == null ? null : document.AcquireCompositorRootPresenter(root);
            DrawPresentedAssetRows(document, properties.Select(property => new PresentedAssetRow((IMemberNode)property.Accessor.Node,
                property.Category, property.Order, property.NativeCategoryExpansion ?? property.CategoryMetadata?.Expand ?? ExpandRule.Always,
                () => DrawCompositorProperty(document, property), property.NativeCategoryOrder ?? property.CategoryMetadata?.Order)),
                rootTarget == null ? null : [rootTarget.Presenter],
                (presenter, categories) => DrawCompositorVirtualRow(document, [presenter], categories));
            return;
        }
        foreach (var group in document.GetCompositorPropertyGroups(properties))
        {
            var expanded = true;
            if (group.Name.Length > 0)
            {
                Gui.PushID(group.Name); Gui.SetNextItemOpen(group.Expand != ExpandRule.Never, ImGuiCond.Once);
                expanded = Gui.TreeNodeEx("##Category"); Gui.SameLine(); Gui.TextUnformatted(group.Name);
            }
            if (expanded) foreach (var property in group.Fields) DrawCompositorProperty(document, property);
            if (group.Name.Length > 0) { if (expanded) Gui.TreePop(); Gui.PopID(); }
        }
    }
    private static string CompositorReferenceName(object? value) => value switch
    { ISharedRenderer renderer => "Shared: " + DisplayAttribute.GetDisplayName(renderer.GetType()), RenderStage stage => stage.Name, SceneCameraSlot slot => slot.Name,
        SceneCameraSlotId { IsEmpty: true } => "None", SceneCameraSlotId slot => slot.Id.ToString(), null => "None", _ => value.ToString() ?? "" };
    private unsafe void DrawCompositorPropertyReorder(AssetMetadataDocument document, CompositorProperty parent, CompositorProperty item, Action<Action> defer)
    {
        if (parent.Rules?.CanReorderItems != true || !item.Accessor.Index.IsInt || parent.Collection?.Descriptor is not CollectionDescriptor { HasInsert: true }
            || !document.CanRemoveCompositorPropertyItems(parent) || !document.CanEditCompositorProperty(item)) return;
        var state = compositorEditors.GetValue(document, static _ => new());
        if (Gui.BeginDragDropSource())
        {
            if (!ReferenceEquals(state.PropertyDrag, item)) { state.PropertyDrag = item; state.ItemDragToken = Guid.NewGuid(); }
            var token = state.ItemDragToken; Gui.SetDragDropPayload("STRIDE_COMPOSITOR_PROPERTY", &token, (nuint)sizeof(Guid));
            Gui.TextUnformatted(item.Label); Gui.EndDragDropSource();
        }
        if (!Gui.BeginDragDropTarget()) return;
        var payload = Gui.AcceptDragDropPayload("STRIDE_COMPOSITOR_PROPERTY");
        if (!payload.IsNull && payload.DataSize == sizeof(Guid) && payload.IsDelivery() && *(Guid*)payload.Data == state.ItemDragToken
            && state.PropertyDrag is { } source && ReferenceEquals(source.ParentCollection, parent) && source.ItemId != item.ItemId)
        {
            var target = item.Accessor.Index.Int;
            var after = Gui.GetIO().MousePos.Y >= (Gui.GetItemRectMin().Y + Gui.GetItemRectMax().Y) * 0.5f;
            var insertion = target + (after ? 1 : 0);
            var destination = Math.Clamp(insertion - (source.Accessor.Index.Int < insertion ? 1 : 0), 0, parent.Items.Count - 1);
            defer(() => document.MoveCompositorPropertyItem(source, destination)); state.PropertyDrag = null;
        }
        Gui.EndDragDropTarget();
    }
}
