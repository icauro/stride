using Hexa.NET.ImGui;
using Stride.Core;
using Stride.Engine;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private ref string componentSearch => ref PropertyGrid.ComponentSearch;
    private EntityComponent? pendingComponentRemoval { get => PropertyGrid.PendingComponentRemoval; set => PropertyGrid.PendingComponentRemoval = value; }

    private void DrawAddComponent(Entity entity)
    {
        var owner = document!;
        Gui.BeginDisabled(!owner.CanAddComponents(entity));
        if (Gui.Button(EditorLocalization.Widget("Add component"), new System.Numerics.Vector2(-1, 0))) { componentSearch = ""; Gui.OpenPopup("AddComponent"); }
        Gui.EndDisabled();
        if (!Gui.BeginPopup("AddComponent")) return;
        Gui.InputTextWithHint("##Search", "Search", ref componentSearch, 256);
        Type? selectedType = null;
        foreach (var category in EntityHierarchyDocument.RegisteredComponentTypes.Where(type => owner.CanAddComponent(entity, type))
                     .Where(type => string.IsNullOrEmpty(componentSearch) || (DisplayAttribute.GetDisplayName(type) ?? type.Name).Contains(componentSearch, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(type => DisplayAttribute.GetDisplayName(type) ?? type.Name)
                     .GroupBy(type => ComponentCategoryAttribute.GetCategory(type) ?? "").OrderBy(group => group.Key))
        {
            if (componentSearch.Length == 0 && category.Key.Length != 0)
            {
                if (!BeginNativeMenu(category.Key)) continue;
                foreach (var type in category) if (DrawComponentType(type)) selectedType = type;
                Gui.EndMenu();
            }
            else foreach (var type in category) if (DrawComponentType(type)) selectedType = type;
        }
        if (selectedType != null)
        {
            Execute(() =>
            {
                ValidatePendingPropertyStrings();
                if (!ReferenceEquals(document, owner) || !owner.IsSelected(entity.Id) || !owner.CanAddComponent(entity, selectedType))
                    throw new InvalidOperationException("The component destination changed.");
                owner.AddComponent(entity, selectedType);
            });
            Gui.CloseCurrentPopup();
        }
        Gui.EndPopup();
    }

    private bool DrawComponentType(Type type)
    {
        Gui.PushID(type.AssemblyQualifiedName);
        var selected = DrawNativeMenuItem(DisplayAttribute.GetDisplayName(type) ?? type.Name);
        Gui.PopID(); return selected;
    }

    private void DrawRemoveComponentAction(EntityComponent component)
    {
        if (!Gui.BeginPopupContextItem("ComponentActions")) return;
        DrawComponentClipboardActions(component);
        DrawInheritedComponentResetMenu(component);
        if (DrawNativeMenuItem(EditorLocalization.Widget("Remove", "Button"), "", false, document!.CanRemoveComponent(component))) pendingComponentRemoval = component;
        Gui.EndPopup();
    }

    private void ApplyPendingComponentRemoval()
    {
        ApplyPendingComponentPaste();
        if (pendingComponentRemoval == null) return;
        var component = pendingComponentRemoval; pendingComponentRemoval = null;
        var owner = document;
        Execute(() =>
        {
            ValidatePendingPropertyStrings();
            if (owner == null || !ReferenceEquals(document, owner) || component.Entity is not { } entity
                || !owner.IsSelected(entity.Id) || !owner.CanRemoveComponent(component))
                throw new InvalidOperationException("The component destination changed.");
            owner.RemoveComponent(component);
            if (activeColorPopup?.Component == component) activeColorPopup = null;
            foreach (var field in componentErrors.Keys.Where(field => field.Component == component).ToArray()) componentErrors.Remove(field);
        });
    }
}
