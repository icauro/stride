using Stride.Engine;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawHierarchyRootContext(EntityHierarchyDocument owner)
    {
        if (!Gui.BeginPopupContextItem("Hierarchy root actions")) return;
        if (owner is PrefabDocument prefab && !SelectionService.IsContentSelected(HierarchyContentItem.Root(prefab)))
        {
            SelectionService.QueueHierarchyClick(HierarchyContentItem.Root(prefab), false, false);
            CompleteHierarchyContentSelection();
        }
        DrawHierarchyContextCreation(owner, null, inSelection: false);
        DrawHierarchyPrefabNavigationMenu();
        Gui.Separator();
        if (DrawNativeMenuItem(EditorLocalization.Widget("Duplicate", "Menu"), "Ctrl+D", false, CanDuplicateHierarchyClipboard)) Execute(DuplicateHierarchySelection);
        if (DrawNativeMenuItem(EditorLocalization.Widget("Copy", "Menu"), "Ctrl+C", false, CanCopyHierarchyClipboard)) Execute(() => CopyHierarchySelection(false));
        if (DrawNativeMenuItem(EditorLocalization.Widget("Cut", "Menu"), "Ctrl+X", false, CanCutHierarchyClipboard)) Execute(() => CopyHierarchySelection(true));
        if (DrawNativeMenuItem(EditorLocalization.Widget("Delete", "Menu"), "Delete", false, CanDeleteHierarchySelection)) RequestSelectedEntitiesDeletion();
        var text = Gui.GetClipboardTextS() ?? "";
        if (DrawNativeMenuItem(EditorLocalization.Widget("Paste", "Menu"), "Ctrl+V", false, owner.CanPasteHierarchyContent(text, [(null, "")])))
            pendingHierarchyPrefabNavigation = () =>
            {
                if (!ReferenceEquals(document, owner) || !ReferenceEquals(authoring?.Find(owner.Asset.Id)?.Document, owner))
                    throw new InvalidOperationException("The hierarchy owner changed.");
                PasteHierarchySelection(true);
            };
        Gui.Separator();
        if (DrawNativeMenuItem(EditorLocalization.Widget("Rename", "Menu"), "F2", false, owner is not PrefabDocument && owner.Selected is { } selected && owner.CanRenameEntity(selected)))
            BeginEntityRename(owner.Selected!);
        Gui.EndPopup();
    }

    private void DrawHierarchyContextCreation(EntityHierarchyDocument owner, Guid? parentId, string folder = "", bool inSelection = true)
    {
        var destination = inSelection ? HierarchySelectionCreationDestination() : null;
        if (destination is { } selected) { owner = selected.Owner; parentId = selected.Parent; folder = selected.Folder; }
        var parent = parentId is { } id && owner.Asset.Hierarchy.Parts.TryGetValue(id, out var part) ? part.Entity : null;
        var available = parentId == null || parent != null;
        available &= IsCurrentHierarchyContextOwner(owner);
        var canCreate = available && owner.CanCreateEntity(parent);
        if (DrawNativeMenuItem(EditorLocalization.Widget("Folder", "Menu"), "", false, available && owner.CanEditHierarchyFolders(parentId)))
            QueueHierarchyContextCreation(owner, parentId, folder, () => CreateHierarchyFolderUi(owner, parentId, folder), destination);
        if (DrawNativeMenuItem(EditorLocalization.Widget("Empty entity", "Menu"), "", false, canCreate))
            QueueHierarchyContextCreation(owner, parentId, folder, () => InsertContextEntity(owner, parentId, folder, new Entity("Entity")), destination);
        foreach (var category in EntityCreationFactories.Categories)
        {
            if (!BeginNativeMenu(category, canCreate)) continue;
            if (category == "Model")
            {
                DrawContextModelFactory(owner, parentId, folder, "Model", false, selection: destination);
                DrawContextModelFactory(owner, parentId, folder, "Instanced Model", true, selection: destination);
            }
            foreach (var factory in EntityCreationFactories.Registered.Where(factory => factory.Category == category))
                if (DrawNativeMenuItem(factory.Label))
                    QueueHierarchyContextCreation(owner, parentId, folder,
                        () =>
                        {
                            if (factory.Kind == EditorEntityKind.SkyboxLight) RequestSkyboxLightCreation(owner, parentId, folder, destination);
                            else InsertContextEntity(owner, parentId, folder, EntityCreationFactories.Create(factory.Kind, AuthoredCameraSlot()));
                        }, destination);
            Gui.EndMenu();
        }
        Gui.Separator();
    }

    private void DrawContextModelFactory(EntityHierarchyDocument owner, Guid? parentId, string folder, string label,
        bool instanced, bool enabled = true, HierarchyCreationDestination? selection = null)
    {
        if (DrawNativeMenuItem(label, "", false, enabled))
            QueueHierarchyContextCreation(owner, parentId, folder,
                () => RequestModelEntityCreation(owner, parentId, folder, instanced, selection), selection);
    }

    private void QueueHierarchyContextCreation(EntityHierarchyDocument owner, Guid? parentId, string folder, Action create,
        HierarchyCreationDestination? selection = null)
    {
        pendingHierarchyPrefabNavigation = () =>
        {
            if (IsAuthoringPipelineUpdating || !IsCurrentHierarchyContextOwner(owner)
                || selection is { } initial && !IsCurrentHierarchyCreationDestination(initial))
                throw new InvalidOperationException("The hierarchy owner changed.");
            if ((parentId is { } id && !owner.Asset.Hierarchy.Parts.ContainsKey(id))
                || (folder.Length != 0 && !owner.GetHierarchyFolders(parentId).Contains(folder, StringComparer.OrdinalIgnoreCase)))
                throw new InvalidOperationException("The hierarchy destination changed.");
            ValidatePendingPropertyStrings();
            FinishEntityRename(true); assetDocument?.EndEdit(); assetCommandContext = false;
            engineViewport?.Picker?.Cancel("Create entity");
            if (!IsCurrentHierarchyContextOwner(owner) || selection is { } current && !IsCurrentHierarchyCreationDestination(current)
                || parentId is { } currentParent && !owner.Asset.Hierarchy.Parts.ContainsKey(currentParent)
                || folder.Length != 0 && !owner.GetHierarchyFolders(parentId).Contains(folder, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException("The hierarchy destination changed.");
            create();
        };
    }

    private void InsertContextEntity(EntityHierarchyDocument owner, Guid? parentId, string folder, Entity entity)
    {
        var parent = parentId is { } id ? owner.Asset.Hierarchy.Parts[id].Entity : null;
        if (IsAuthoringPipelineUpdating || !owner.CanCreateEntity(parent)) throw new InvalidOperationException("The hierarchy cannot be edited.");
        ValidatePendingPropertyStrings();
        if (!IsCurrentHierarchyContextOwner(owner) || !owner.CanCreateEntity(parent)
            || parent != null && (!owner.Asset.Hierarchy.Parts.TryGetValue(parent.Id, out var part) || !ReferenceEquals(part.Entity, parent))
            || folder.Length != 0 && !owner.GetHierarchyFolders(parentId).Contains(folder, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("The hierarchy destination changed.");
        owner.InsertNewEntity(entity, parent, folder);
        CompleteCreatedHierarchyEntity(owner, entity, parentId, folder);
    }
}
