using Hexa.NET.ImGui;
using Stride.Engine;
using Stride.Graphics;
using Gui = Hexa.NET.ImGui.ImGui;
using NVector2 = System.Numerics.Vector2;
using NVector3 = System.Numerics.Vector3;
using NVector4 = System.Numerics.Vector4;
using SVector3 = Stride.Core.Mathematics.Vector3;
using SQuaternion = Stride.Core.Mathematics.Quaternion;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private HierarchyPanel? ownedHierarchyPanel;
    private HierarchyPanel HierarchyController => ownedHierarchyPanel ??= new(new HierarchyPanelHost(this));
    private HierarchyPanelState HierarchyState => HierarchyController.State;
    private ref string hierarchyFilter => ref HierarchyState.hierarchyFilter;
    private ref string hierarchyTokenSource => ref HierarchyState.hierarchyTokenSource;
    private ref string[] hierarchyFilterTokens => ref HierarchyState.hierarchyFilterTokens;
    private ref Guid? revealCreatedParent => ref HierarchyState.revealCreatedParent;
    private ref Guid? scrollHierarchyEntity => ref HierarchyState.scrollHierarchyEntity;
    private HashSet<Guid> hierarchyVisible => HierarchyState.hierarchyVisible;
    private List<Guid> hierarchyDisplayed => HierarchyState.hierarchyDisplayed;
    private ref Guid? hierarchySelectionAnchor => ref HierarchyState.hierarchySelectionAnchor;
    private ref EntityHierarchyDocument? hierarchySelectionDocument => ref HierarchyState.hierarchySelectionDocument;
    private ref (Entity Entity, bool Toggle, bool Range)? pendingHierarchySelection => ref HierarchyState.pendingHierarchySelection;
    private ref (EntityHierarchyDocument Document, Entity Entity)? pendingHierarchyGroupClick => ref HierarchyState.pendingHierarchyGroupClick;
    private ref bool hierarchyGroupClickDragged => ref HierarchyState.hierarchyGroupClickDragged;
    private void DrawHierarchyPanel(EditorIcons icons) => HierarchyController.Render(icons);

    private void CreateEmptyEntity(Entity? parent)
    {
        if (document == null || IsAuthoringPipelineUpdating || !document.CanCreateEntity(parent)) return;
        ValidatePendingPropertyStrings();
        FinishEntityRename(true); assetDocument?.EndEdit(); assetCommandContext = false;
        engineViewport?.Picker?.Cancel("Create entity");
        var entity = document!.CreateEmptyEntity(parent);
        CompleteCreatedHierarchyEntity(document, entity, parent?.Id, "");
    }

    private void DrawEntity(Entity entity)
    {
        if (!hierarchyVisible.Contains(entity.Id)) return;
        hierarchyDisplayed.Add(entity.Id);
        RegisterHierarchyEntityRow(document!, entity.Id);
        var flags = ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.SpanAvailWidth;
        if (entity.Transform.Children.Count == 0 && document!.GetHierarchyFolders(entity.Id).Count == 0) flags |= ImGuiTreeNodeFlags.Leaf;
        if (IsHierarchyEntityContentSelected(document!, entity.Id)) flags |= ImGuiTreeNodeFlags.Selected;
        Gui.PushID(entity.Id.ToString());
        if (revealCreatedParent is { } revealId && document.Asset.Hierarchy.Parts.TryGetValue(revealId, out var revealed))
        {
            // Open the whole ancestor chain so navigation/creation also works
            // when the requested parent is inside a collapsed subtree.
            Entity? ancestor = revealed.Entity;
            for (var depth = 0; ancestor != null && depth < document.Asset.Hierarchy.Parts.Count; depth++)
            {
                if (ancestor.Id == entity.Id) { Gui.SetNextItemOpen(true, ImGuiCond.Always); break; }
                ancestor = ancestor.Transform.Parent?.Entity;
            }
            if (revealId == entity.Id) revealCreatedParent = null;
        }
        var dropIndent = Gui.GetCursorScreenPos().X;
        var expanded = DrawHierarchyRow(renameEntity == entity && !inspectorRenameActive ? "" : entity.Name, HierarchyEntityIcon(entity), flags,
            forceSubmission: scrollHierarchyEntity == entity.Id || renameEntity == entity && !inspectorRenameActive,
            textColor: HierarchyMetadataTextColor(document, (flags & ImGuiTreeNodeFlags.Selected) != 0));
        RememberHierarchyDropRow(HierarchyContentItem.Entity(document, entity.Id), expanded, dropIndent);
        if (HierarchyController.RowWasVirtualized)
        {
            if (expanded) { DrawHierarchyChildren(entity); Gui.TreePop(); }
            Gui.PopID();
            return;
        }
        RememberVisualCaptureHierarchyEntity(document, entity.Id);
        DrawHierarchyEntityAnnotations(document, entity, renameEntity == entity && !inspectorRenameActive ? "" : entity.Name);
        if (scrollHierarchyEntity == entity.Id) { Gui.SetScrollHereY(0.5f); scrollHierarchyEntity = null; }
        if (Controls.Enabled) Controls.Record("entity/" + entity.Name);
        if (Controls.Enabled) Controls.Record("entity/id/" + entity.Id);
        if (Gui.IsItemClicked() && !Gui.IsItemToggledOpen() && Gui.GetIO().MousePos.X < hierarchyRowMaximum.X - HierarchyActionWidth)
        {
            if (IsHierarchyEntityContentSelected(document, entity.Id) && SelectedHierarchyDocuments.Sum(owner => owner.SelectedCount) > 1 && !Gui.GetIO().KeyCtrl && !Gui.GetIO().KeyShift)
            { pendingHierarchyGroupClick = (document, entity); hierarchyGroupClickDragged = false; }
            else { pendingHierarchyGroupClick = null; QueueHierarchyEntityClick(document, entity.Id, Gui.GetIO().KeyCtrl, Gui.GetIO().KeyShift); }
        }
        DrawEntityDragDrop(entity);
        if (Gui.BeginPopupContextItem("Entity actions"))
        {
            selectedHierarchyFolder = null;
            if (!IsHierarchyEntityContentSelected(document, entity.Id))
            {
                engineViewport?.Picker?.Cancel("Hierarchy selection changed");
                SelectCompositeHierarchyEntity(document, entity, false);
            }
            DrawHierarchyContextCreation(document, entity.Id);
            if (DrawNativeMenuItem(EditorLocalization.Widget("Rename", "Menu"), "F2", false, document.CanRenameEntity(entity))) BeginEntityRename(entity);
            if (DrawNativeMenuItem(EditorLocalization.Widget("Delete", "Menu"), "Delete", false, document.IsSelected(entity.Id) ? CanDeleteHierarchySelection : document.CanDeleteEntity(entity))) RequestEntityDeletion(entity);
            if (DrawNativeMenuItem("Move to...", "", false, document.CanDeleteEntity(entity))) RequestEntityMove(entity);
            if (Controls.Enabled) Controls.Record("hierarchy/move");
            DrawEntityFolderMoveMenu(entity);
            Gui.Separator(); DrawHierarchyClipboardMenu();
            Gui.Separator(); DrawHierarchyPrefabNavigationMenu();
            Gui.EndPopup();
        }
        if (renameEntity == entity && !inspectorRenameActive)
        {
            var next = Gui.GetCursorScreenPos();
            var namePosition = hierarchyRowMinimum + new NVector2(Gui.GetTreeNodeToLabelSpacing() + Gui.GetTextLineHeight() + Gui.GetStyle().ItemInnerSpacing.X, 0);
            Gui.SetCursorScreenPos(namePosition);
            Gui.SetNextItemWidth(Math.Max(1, hierarchyRowMaximum.X - HierarchyActionWidth - namePosition.X));
            if (focusRename) { Gui.SetKeyboardFocusHere(); focusRename = false; }
            Gui.InputText("##Name", ref renameDraft, 4096, ImGuiInputTextFlags.AutoSelectAll);
            if (Controls.Enabled) Controls.Record("rename/name");
            if (Gui.IsKeyPressed(ImGuiKey.Escape)) FinishEntityRename(false);
            else if (!Gui.IsItemActive() && Gui.IsItemDeactivated()) Execute(() => FinishEntityRename(true));
            FinishHierarchyRow(next);
        }
        DrawHierarchyEntityActions(document, entity);
        if (expanded)
        {
            DrawHierarchyChildren(entity);
            Gui.TreePop();
        }
        Gui.PopID();
    }


}
