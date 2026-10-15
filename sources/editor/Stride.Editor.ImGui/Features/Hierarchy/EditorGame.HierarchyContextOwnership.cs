using Stride.Engine;
using Gui = Hexa.NET.ImGui.ImGui;
using Vector2 = System.Numerics.Vector2;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private WeakReference<EntityHierarchyDocument>? hierarchyRevealOwner;
    private Guid? hierarchyRevealEntity;
    private Guid? hierarchyRevealParent;
    private string? hierarchyRevealFolder;

    private bool IsCurrentHierarchyContextOwner(EntityHierarchyDocument owner)
        => sessionHistory == null && authoring == null && ReferenceEquals(document, owner)
            || ReferenceEquals(owner.SessionHistory, sessionHistory)
            && authoring?.Find(owner.Asset.Id) is { IsPublished: true } state && ReferenceEquals(state.Document, owner)
            && (owner is SceneDocument scene ? IsCurrentPresentedSceneRoot(scene) : ReferenceEquals(PresentedHierarchyDocument, owner));

    private bool IsCurrentHierarchyDragSource(EntityHierarchyDocument owner)
    {
        if (IsCurrentHierarchyContextOwner(owner)) return true;
        if (!ReferenceEquals(owner.SessionHistory, sessionHistory) || authoring?.Find(owner.Asset.Id) is not { IsPublished: true } state
            || !ReferenceEquals(state.Document, owner)) return false;
        if (documents.Find(owner) != null) return true;
        if (owner is not SceneDocument scene) return false;
        var visited = new HashSet<Stride.Core.Assets.AssetId>();
        while (visited.Add(scene.Asset.Id))
        {
            if (documents.Find(scene) != null) return true;
            if (SceneParentId(scene.Asset) is not { } id || authoring?.Find(id) is not { IsPublished: true, Document: SceneDocument parent }
                || !parent.Asset.ChildrenIds.Contains(scene.Asset.Id)) return false;
            scene = parent;
        }
        return false;
    }

    private void RevealHierarchyContext(EntityHierarchyDocument owner, Guid? entity, Guid? parent, string? folder = null, bool clearFilter = true)
    {
        if (clearFilter) hierarchyFilter = "";
        if (ReferenceEquals(owner, document))
        {
            scrollHierarchyEntity = entity; revealCreatedParent = parent;
            revealHierarchyFolder = string.IsNullOrEmpty(folder) ? null : (parent, folder);
            return;
        }
        hierarchyRevealOwner = new(owner); hierarchyRevealEntity = entity;
        hierarchyRevealParent = parent; hierarchyRevealFolder = folder;
    }
    private bool HasHierarchyContextReveal(EntityHierarchyDocument owner)
        => hierarchyRevealOwner?.TryGetTarget(out var current) == true && ReferenceEquals(current, owner)
            && IsCurrentHierarchyContextOwner(owner);
    private void OpenHierarchyContextEntity(EntityHierarchyDocument owner, Entity entity)
    {
        if (!HasHierarchyContextReveal(owner)) return;
        var id = hierarchyRevealParent ?? hierarchyRevealEntity;
        if (id is not { } target || !owner.Asset.Hierarchy.Parts.TryGetValue(target, out var part)) return;
        var ancestor = part.Entity;
        for (var depth = 0; ancestor != null && depth < owner.Asset.Hierarchy.Parts.Count; depth++)
        {
            if (ancestor.Id == entity.Id) { Gui.SetNextItemOpen(true, Hexa.NET.ImGui.ImGuiCond.Always); return; }
            ancestor = ancestor.Transform.Parent?.Entity;
        }
    }
    private void OpenHierarchyContextFolder(EntityHierarchyDocument owner, Guid? parent, string folder)
    {
        if (HasHierarchyContextReveal(owner) && hierarchyRevealParent == parent && hierarchyRevealFolder is { } target
            && (string.Equals(target, folder, StringComparison.OrdinalIgnoreCase) || target.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase)))
            Gui.SetNextItemOpen(true, Hexa.NET.ImGui.ImGuiCond.Always);
    }
    private void CompleteHierarchyContextFolderReveal(EntityHierarchyDocument owner, Guid? parent, string folder)
    {
        if (!HasHierarchyContextReveal(owner) || hierarchyRevealEntity != null || hierarchyRevealParent != parent
            || !string.Equals(hierarchyRevealFolder, folder, StringComparison.OrdinalIgnoreCase)) return;
        Gui.SetScrollHereY(0.5f); ClearHierarchyContextReveal();
    }
    private void CompleteHierarchyContextParentReveal(EntityHierarchyDocument owner, Entity entity)
    {
        if (HasHierarchyContextReveal(owner) && hierarchyRevealEntity == null && hierarchyRevealParent == entity.Id
            && string.IsNullOrEmpty(hierarchyRevealFolder)) ClearHierarchyContextReveal();
    }
    private void ClearHierarchyContextReveal()
    {
        hierarchyRevealOwner = null; hierarchyRevealEntity = hierarchyRevealParent = null; hierarchyRevealFolder = null;
    }
    private void DrawHierarchyContextEntityRename(EntityHierarchyDocument owner, Entity entity)
    {
        if (!IsInlineHierarchyEntityRenaming(owner, entity)) return;
        var next = Gui.GetCursorScreenPos();
        var origin = hierarchyRowMinimum + new Vector2(Gui.GetTreeNodeToLabelSpacing() + Gui.GetTextLineHeight() + Gui.GetStyle().ItemInnerSpacing.X, 0);
        Gui.SetCursorScreenPos(origin);
        Gui.SetNextItemWidth(Math.Max(1, hierarchyRowMaximum.X - HierarchyActionWidth - origin.X));
        if (focusRename) { Gui.SetKeyboardFocusHere(); focusRename = false; }
        Gui.InputText("##Name", ref renameDraft, 4096, Hexa.NET.ImGui.ImGuiInputTextFlags.AutoSelectAll);
        if (Gui.IsKeyPressed(Hexa.NET.ImGui.ImGuiKey.Escape)) FinishEntityRename(false);
        else if (!Gui.IsItemActive() && Gui.IsItemDeactivated()) Execute(() => FinishEntityRename(true));
        FinishHierarchyRow(next);
    }
}
