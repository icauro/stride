using System.Globalization;
using Hexa.NET.ImGui;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Engine;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private Action? pendingCollectionAction;

    private void DrawCollectionProperty(ComponentField field)
    {
        using var referencePhase = new ContentReferenceCommitPhase(command => pendingCollectionAction = command,
            command => Execute(() => { ValidatePendingPropertyStrings(); command(); }));
        Gui.PushID(field.Name);
        var open = Gui.TreeNodeEx("##Collection", ImGuiTreeNodeFlags.None);
        var controlPrefix = "component/" + field.Component.Id + "/" + field.Name + "/collection";
        if (Controls.Enabled) Controls.Record(controlPrefix + "/expand");
        Gui.SameLine(); DrawDocumentedComponentLabel(field);
        if (open)
        {
            var owner = document!;
            var items = owner.GetCollectionItems(field);
            foreach (var item in items)
            {
                if (!owner.IsHierarchyItemVisible(item)) continue;
                Gui.PushID(item.Id.ToString());
                Gui.TextUnformatted(owner.HierarchyItemLabel(item) ?? "[" + item.Index + "]");
                Gui.BeginDisabled(!item.IsContentReference && item.Type != typeof(string) && !UsesCollectionNumericDraft(item) && !IsPackageColorType(item.Type) && (!owner.CanEditCollection(field) || !owner.CanEditHierarchyItem(item.Collection, item.Index)));
                DrawCollectionItemValue(item);
                Gui.EndDisabled();
                if (Gui.BeginPopupContextItem("Item property actions"))
                {
                    if (item.Type.IsEnum && item.Type.IsDefined(typeof(FlagsAttribute), true))
                    {
                        using var flagsDisabled = ImGuiScopes.Disabled(!owner.CanEditCollection(field) || !owner.CanEditHierarchyItem(item.Collection, item.Index));
                        if (DrawFlagBulkActions() is { } action)
                            pendingCollectionAction = () => { owner.UpdateCollectionItem(item, ApplyFlagBulkAction((Enum)(item.Value ?? Enum.ToObject(item.Type, 0)), action)); owner.EndCollectionEdit(); };
                    }
                    DrawCollectionItemClipboardCommands(owner, item); Gui.EndPopup();
                }
                if (owner.CanRemoveCollection(field))
                {
                    Gui.BeginDisabled(!owner.CanEditHierarchyItem(item.Collection, item.Index));
                    if (Gui.SmallButton(EditorLocalization.Widget("Remove", "Button"))) pendingCollectionAction = () => owner.RemoveCollectionItem(item);
                    if (Controls.Enabled) Controls.Record(controlPrefix + "/remove/" + item.Id);
                    if (owner.CanReorderCollection(field))
                    {
                        Gui.SameLine(); Gui.BeginDisabled(item.Index.Int == 0);
                        if (Gui.SmallButton(EditorLocalization.Widget("Move up", "Menu"))) { var index = item.Index.Int - 1; pendingCollectionAction = () => owner.MoveCollectionItem(item, index); }
                        if (Controls.Enabled) Controls.Record(controlPrefix + "/up/" + item.Id);
                        Gui.EndDisabled(); Gui.SameLine(); Gui.BeginDisabled(item.Index.Int == items.Count - 1);
                        if (Gui.SmallButton(EditorLocalization.Widget("Move down", "Menu"))) { var index = item.Index.Int + 1; pendingCollectionAction = () => owner.MoveCollectionItem(item, index); }
                        if (Controls.Enabled) Controls.Record(controlPrefix + "/down/" + item.Id);
                        Gui.EndDisabled();
                    }
                    Gui.EndDisabled();
                }
                Gui.PopID();
            }
            if (owner.CanAddCollection(field))
            {
                var contextForAdd = owner.GetRootHierarchyCollection(field);
                var defaultAdd = contextForAdd != null && owner.HierarchyCollectionNativeEntries(contextForAdd) == null;
                if (Gui.Button("Add"))
                {
                    if (defaultAdd) pendingCollectionAction = () => SelectHierarchyDefaultCollectionAdd([owner], [contextForAdd!],
                        () => owner.AddHierarchyDefaultCollectionItem(contextForAdd!));
                    else Gui.OpenPopup("AddItem");
                }
                if (Controls.Enabled) Controls.Record(controlPrefix + "/add");
                if (!defaultAdd && Gui.BeginPopup("AddItem"))
                {
                    var declared = field.CollectionElementType!;
                    if (ContentAssetReference.Supports(declared))
                        DrawContentReferenceValue(declared, null, false, reference =>
                            referencePhase.Submit(() => owner.AddCollectionAssetReference(field, reference!)));
                    if (owner.GetRootHierarchyCollection(field) is { } context)
                        NativeAbstractEntryControl.Choices(owner.HierarchyCollectionNativeEntries(context)?.ToArray() ?? [], null, entry =>
                            pendingCollectionAction = () => SelectHierarchyCollectionEntries([owner], [context], entry,
                                () => owner.AddHierarchyCollectionEntry(context, entry)));
                    if (SceneObjectReference.Supports(declared))
                    {
                        foreach (var part in owner.Asset.Hierarchy.Parts.Values)
                        {
                            void Reference(object value, string id)
                            {
                                Gui.PushID(id);
                                if (DrawNativeSelectable(SceneReferenceLabel(value))) pendingCollectionAction = () => owner.AddCollectionReference(field, value);
                                if (Controls.Enabled) Controls.Record(controlPrefix + "/add/" + id);
                                Gui.PopID();
                            }
                            if (SceneObjectReference.AcceptsEntity(declared, part.Entity)) Reference(part.Entity, part.Entity.Id.ToString());
                            else foreach (var component in part.Entity.Components.Where(declared.IsInstanceOfType)) Reference(component, component.Id.ToString());
                        }
                    }
                    Gui.EndPopup();
                }
            }
            Gui.TreePop();
        }
        Gui.PopID();
    }

    private void DrawCollectionItemValue(CollectionItem item)
    {
        var value = item.Value;
        var owner = document!;
        if (!item.IsContentReference && !item.IsSceneReference && Nullable.GetUnderlyingType(item.DeclaredType) == null && (!item.SupportsSimpleEditing || owner.HierarchyItemNativeEntries(item) != null))
        {
            var entries = owner.HierarchyItemEntries(item);
            if (entries.Count != 0 && BeginNativeCombo("##Type", NativeAbstractEntryControl.Label(entries, value)))
            {
                NativeAbstractEntryControl.Choices(entries, value, entry => pendingCollectionAction = () =>
                    SelectHierarchyItemEntries([owner], [item], entry, () => owner.ChangeHierarchyItemEntry(item, entry)));
                Gui.EndCombo();
            }
        }
        if (value is Stride.Core.Yaml.IUnloadable unknown) { DrawUnloadableProperty(unknown); return; }
        if (item.Type == typeof(Stride.Core.IO.UFile) || item.Type == typeof(Stride.Core.IO.UDirectory))
        {
            DrawEntityPropertyPaths(item.Type, [owner], [(Func<object?>)(() => item.Value)],
                () => owner.CanSelectHierarchyItemEntry(item), path => { owner.UpdateCollectionItem(item, path); owner.EndCollectionEdit(); },
                certificates: () => [item.Collection, item.Index, item.Id], identity: item.Collection);
            if (item.AllowsNull)
            {
                using var disabled = ImGuiScopes.Disabled(!owner.CanEditCollection(item.Field) || !owner.CanEditHierarchyItem(item.Collection, item.Index));
                if (Gui.SmallButton("Clear")) pendingCollectionAction = () => { owner.UpdateCollectionItem(item, null); owner.EndCollectionEdit(); };
            }
            return;
        }
        if (item.IsContentReference)
        {
            using var referencePhase = new ContentReferenceCommitPhase(command => pendingCollectionAction = command,
                command => Execute(() => { ValidatePendingPropertyStrings(); command(); }));
            DrawContentReferenceValue(item.Type, value, item.AllowsNull,
                updated => referencePhase.Submit(() => { owner.UpdateCollectionItem(item, updated); owner.EndCollectionEdit(); }),
                canEdit: owner.CanEditCollection(item.Field) && owner.CanEditHierarchyItem(item.Collection, item.Index));
            return;
        }
        if (!item.SupportsSimpleEditing)
        {
            var fields = document!.GetCollectionItemFields(item);
            if (fields.Count == 0) Gui.TextUnformatted(value == null ? "None" : DisplayAttribute.GetDisplayName(value.GetType()) ?? value.GetType().Name);
            else DrawComponentFields(fields);
            return;
        }
        var nullable = Nullable.GetUnderlyingType(item.DeclaredType) != null;
        using var nullableRow = NativeNullableRow.Begin("##Nullable", nullable);
        if (nullable)
        {
            using (ImGuiScopes.Disabled(!owner.CanEditCollection(item.Field) || !owner.CanEditHierarchyItem(item.Collection, item.Index) || item.Value != null && !item.AllowsNull))
                if (DrawNativeNullablePresence(item.Value != null, out var create))
                    pendingCollectionAction = () => { owner.UpdateCollectionItem(item, create ? CreateNullableValue(item.Type) : null); owner.EndCollectionEdit(); };
            nullableRow.ValueColumn();
            if (value == null) { Gui.TextUnformatted(EditorLocalization.Text("(null)")); return; }
        }
        Gui.SetNextItemWidth(-1);
        if (item.Type == typeof(string)) { DrawCollectionString(item); return; }
        if (UsesCollectionNumericDraft(item))
        {
            DrawCollectionNumericDrafts(item);
            if (!nullable && item.AllowsNull)
            {
                using var disabled = ImGuiScopes.Disabled(!owner.CanEditCollection(item.Field) || !owner.CanEditHierarchyItem(item.Collection, item.Index));
                if (Gui.SmallButton("Clear")) pendingCollectionAction = () => { owner.UpdateCollectionItem(item, null); owner.EndCollectionEdit(); };
            }
            return;
        }
        if (value == null)
        {
            if (item.IsSceneReference) DrawCollectionReference(item);
            else if (Gui.Button(EditorLocalization.Widget("Create", "Button")))
                pendingCollectionAction = () => { document!.UpdateCollectionItem(item, item.Type == typeof(string) ? "" : Activator.CreateInstance(item.Type)); document.EndCollectionEdit(); };
            return;
        }
        var discrete = item.Type == typeof(bool) || item.Type.IsEnum || item.IsSceneReference;
        object? updated = value; var changed = false; var deactivated = false;
        if (item.Type == typeof(Color) || item.Type == typeof(Color3) || item.Type == typeof(Color4))
        {
            DrawRetainedSingleItemColor(item);
            return;
        }
        if (DrawNativeScalarTemplate(item.Collection, item.Type, value, false, owner.CanEditCollection(item.Field) && owner.CanEditHierarchyItem(item.Collection, item.Index),
            next => pendingCollectionAction = () => { document!.UpdateCollectionItem(item, next); document.EndCollectionEdit(); }))
        {
            if (!nullable && item.AllowsNull && Gui.SmallButton("Clear")) pendingCollectionAction = () => { document!.UpdateCollectionItem(item, null); document.EndCollectionEdit(); };
            return;
        }
        if (item.IsSceneReference) { DrawCollectionReference(item); return; }
        if (item.Type == typeof(bool)) { var enabled = (bool)value; changed = DrawNativeCheckbox("##Value", ref enabled); updated = enabled; }
        else if (item.Type.IsEnum)
        {
            if (icons?.HasEnumImages(item.Type) == true)
                changed = NativeEnumImageControl.Draw(icons, item.Collection, item.Type, value, false,
                    item.EnumOptions.Select(option => (Enum)option.Value), UiScale(1), out updated);
            else if (BeginNativeCombo("##Value", item.EnumOptions.FirstOrDefault(option => Equals(option.Value, value))?.Label ?? value.ToString()))
            {
                foreach (var option in item.EnumOptions)
                {
                    if (item.Type.IsDefined(typeof(FlagsAttribute), true))
                    {
                        var mask = EnumBits(option.Value); var bits = EnumBits(value);
                        var selected = mask == 0 ? bits == 0 : (bits & mask) == mask;
                        if (DrawNativeCheckbox(option.Label, ref selected))
                        { updated = EnumFromBits(item.Type, mask == 0 ? 0 : selected ? bits | mask : bits & ~mask); changed = true; }
                    }
                    else if (DrawNativeSelectable(option.Label, Equals(option.Value, value))) { updated = option.Value; changed = true; }
                }
                if (item.Type.IsDefined(typeof(FlagsAttribute), true) && DrawFlagBulkActions() is { } action)
                { updated = ApplyFlagBulkAction((Enum)value, action); changed = true; }
                Gui.EndCombo();
            }
        }
        else DrawSelectableText("##Value", Convert.ToString(value, CultureInfo.InvariantCulture) ?? "");
        deactivated |= Gui.IsItemDeactivated();
        if (changed) Execute(() => document!.UpdateCollectionItem(item, updated));
        if (discrete && changed || deactivated) pendingCollectionAction ??= () => document!.EndCollectionEdit();
        if (!nullable && item.AllowsNull)
        {
            if (Gui.SmallButton("Clear")) pendingCollectionAction = () => { document!.UpdateCollectionItem(item, null); document.EndCollectionEdit(); };
        }
    }

    private void DrawCollectionReference(CollectionItem item)
    {
        var controlPrefix = "component/" + item.Field.Component.Id + "/" + item.Field.Name + "/collection/" + item.Id + "/reference";
        var open = BeginNativeCombo("##Reference", SceneReferenceLabel(item.Value));
        if (Controls.Enabled) Controls.Record(controlPrefix);
        if (!open)
        {
            DrawSceneReferenceDrop(item.Type, document!.CanEditCollection(item.Field), candidate =>
                pendingCollectionAction = () => { document.UpdateCollectionItem(item, candidate); document.EndCollectionEdit(); });
            return;
        }
        void Candidate(object? value, string id)
        {
            Gui.PushID(id);
            if (DrawNativeSelectable(SceneReferenceLabel(value), ReferenceEquals(item.Value, value)))
                pendingCollectionAction = () => { document!.UpdateCollectionItem(item, value); document.EndCollectionEdit(); };
            if (Controls.Enabled) Controls.Record(controlPrefix + "/" + id);
            Gui.PopID();
        }
        if (item.AllowsNull) Candidate(null, "none");
        foreach (var part in document!.Asset.Hierarchy.Parts.Values)
        {
            if (SceneObjectReference.AcceptsEntity(item.Type, part.Entity)) Candidate(part.Entity, part.Entity.Id.ToString());
            else foreach (var component in part.Entity.Components.Where(item.Type.IsInstanceOfType)) Candidate(component, component.Id.ToString());
        }
        Gui.EndCombo();
    }

    private void ApplyPendingCollectionAction()
    {
        var action = pendingCollectionAction; pendingCollectionAction = null;
        if (action != null) Execute(() => { ValidatePendingPropertyStrings(); action(); });
    }
}
