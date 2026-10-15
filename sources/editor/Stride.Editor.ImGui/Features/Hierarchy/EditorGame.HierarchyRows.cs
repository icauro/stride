using Hexa.NET.ImGui;
using Stride.Core.Assets;
using Stride.Engine;
using Stride.Rendering;
using Gui = Hexa.NET.ImGui.ImGui;
using Vector2 = System.Numerics.Vector2;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private ref bool? hierarchyExpandAll => ref HierarchyState.hierarchyExpandAll;
    private EditorTreeView hierarchyTree => HierarchyState.hierarchyTree;
    private ref int hierarchyExpansionRevision => ref HierarchyState.hierarchyExpansionRevision;
    private Dictionary<uint, int> hierarchyExpansionApplied => HierarchyState.hierarchyExpansionApplied;
    private HashSet<(AssetId Asset, Guid Entity)> lockedHierarchyEntities => HierarchyState.lockedHierarchyEntities;
    private ref Vector2 hierarchyRowMinimum => ref HierarchyState.hierarchyRowMinimum;
    private ref Vector2 hierarchyRowMaximum => ref HierarchyState.hierarchyRowMaximum;
    private float HierarchyActionWidth => HierarchyController.ActionWidth;
    private bool IsHierarchyEntityLocked(EntityHierarchyDocument owner, Guid id) => lockedHierarchyEntities.Contains((owner.Asset.Id, id));

    private bool HierarchyIconButton(string name, string tooltip)
    {
        Gui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(2 * Gui.GetStyle().FontScaleDpi));
        Gui.PushStyleColor(ImGuiCol.Button, new System.Numerics.Vector4(0, 0, 0, 0));
        var clicked = icons!.Button(name, tooltip, Gui.GetTextLineHeight() / Gui.GetStyle().FontScaleDpi);
        Gui.PopStyleColor(); Gui.PopStyleVar();
        return clicked;
    }

    private bool DrawHierarchyRow(string name, string icon, ImGuiTreeNodeFlags flags, bool hasActions = true, bool forceSubmission = false, uint? textColor = null, uint textWeight = 400)
        => HierarchyController.DrawRow(name, icon, flags, hasActions, forceSubmission, textColor, textWeight);

    private uint? HierarchyMetadataTextColor(EntityHierarchyDocument owner, bool selected)
        => !selected && owner is SceneDocument scene && !IsSceneLoaded(scene) ? 0xff808080u : null;

    private void ApplyHierarchyExpansion() => HierarchyController.ApplyHierarchyExpansion();

    private string HierarchyEntityIcon(Entity entity) => icons!.EntityIcon(entity);

    private void FinishHierarchyRow(Vector2 next)
    {
        Gui.SetCursorScreenPos(next - new Vector2(0, Gui.GetStyle().ItemSpacing.Y));
        Gui.Dummy(Vector2.Zero);
    }

    private void DrawHierarchyEntityActions(EntityHierarchyDocument owner, Entity entity)
    {
        var next = Gui.GetCursorScreenPos();
        Gui.SetCursorScreenPos(new Vector2(hierarchyRowMaximum.X - HierarchyActionWidth, hierarchyRowMinimum.Y));
        var loaded = owner is not SceneDocument scene || IsSceneLoaded(scene);
        Gui.BeginDisabled(!loaded);
        var locked = IsHierarchyEntityLocked(owner, entity.Id);
        if (HierarchyIconButton(locked ? "scene-locked" : "scene-unlocked", "Lock/unlock this entity"))
        {
            var recursive = Gui.GetIO().KeyCtrl;
            pendingHierarchyPrefabNavigation = () => RequestHierarchyEntityLock(owner, entity, !locked, recursive);
        }
        RegisterVisualCaptureItem("hierarchy.entity." + entity.Id + ".lock");
        Gui.SameLine();
        Gui.BeginDisabled(IsInlineHierarchyEntityRenaming(owner, entity));
        if (HierarchyIconButton("focus", "Focus on this entity (F)"))
            pendingHierarchyPrefabNavigation = () => RequestHierarchyEntityFocus(owner, entity);
        RegisterVisualCaptureItem("hierarchy.entity." + entity.Id + ".focus");
        Gui.EndDisabled();
        Gui.EndDisabled();
        FinishHierarchyRow(next);
    }

    private bool IsInlineHierarchyEntityRenaming(EntityHierarchyDocument owner, Entity entity)
        => !inspectorRenameActive && ReferenceEquals(renameOwner, owner) && ReferenceEquals(renameEntity, entity);
    private void RequestHierarchyEntityFocus(EntityHierarchyDocument owner, Entity entity)
    {
        if (IsInlineHierarchyEntityRenaming(owner, entity) || engineViewport?.IsPresentedScene(owner) != true
            || sessionHistory?.Registry.Find(owner.Asset.Id) is not { IsPublished: true } state
            || !ReferenceEquals(state.Document, owner) || !owner.Asset.Hierarchy.Parts.TryGetValue(entity.Id, out var part)
            || !ReferenceEquals(part.Entity, entity) || owner is SceneDocument scene && !IsSceneLoaded(scene)) return;
        engineViewport.Focus(owner, entity);
    }

    private void RequestHierarchyEntityLock(EntityHierarchyDocument owner, Entity entity, bool locked, bool recursive)
    {
        if (IsAuthoringPipelineUpdating || sessionHistory?.Registry.Find(owner.Asset.Id) is not { IsPublished: true } state
            || !ReferenceEquals(state.Document, owner) || !owner.Asset.Hierarchy.Parts.TryGetValue(entity.Id, out var part)
            || !ReferenceEquals(part.Entity, entity) || owner is SceneDocument scene && !IsSceneLoaded(scene)) return;
        bool ChangesLock(Entity current) => IsHierarchyEntityLocked(owner, current.Id) != locked
            || recursive && current.Transform.Children.Any(child => ChangesLock(child.Entity));
        if (!ChangesLock(entity)) return;
        ValidatePendingPropertyStrings();
        documents.CommitAll(); engineViewport?.Gizmos?.Cancel(); engineViewport?.Picker?.Cancel("Entity locking changed");
        void Apply(Entity current)
        {
            if (locked) lockedHierarchyEntities.Add((owner.Asset.Id, current.Id));
            else lockedHierarchyEntities.Remove((owner.Asset.Id, current.Id));
            if (recursive) foreach (var child in current.Transform.Children) Apply(child.Entity);
        }
        Apply(entity);
        scenePresentationVersion++;
    }
}
