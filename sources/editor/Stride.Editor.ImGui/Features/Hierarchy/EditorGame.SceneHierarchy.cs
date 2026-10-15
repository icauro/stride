using Hexa.NET.ImGui;
using Stride.Assets.Entities;
using Stride.Core.Assets;
using Stride.Core.Mathematics;
using Stride.Core.Serialization;
using Stride.Engine;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private sealed class SceneHierarchyRows
    {
        internal readonly HashSet<Guid> Visible = [];
        internal readonly List<Guid> Displayed = [];
        internal Guid? Anchor;
    }
    private readonly Dictionary<AssetId, SceneHierarchyRows> foreignSceneRows = [];
    private sealed record SceneHierarchyPlacement(SceneDocument Document, AssetId[] Children, Scene? Parent, Vector3 Offset);
    private SceneAsset? AuthoredScene(AssetId id) => (authoring?.Find(id)?.Document as SceneDocument)?.Asset
        ?? workspace?.AssetItems.FirstOrDefault(item => item.Id == id)?.Asset as SceneAsset;
    private static AssetId? SceneParentId(SceneAsset scene) => scene.Parent == null ? null
        : AttachedReferenceManager.GetAttachedReference(scene.Parent)?.Id
            ?? throw new InvalidOperationException("The parent scene reference cannot be resolved.");
    private SceneDocument? ResolveSceneHierarchyDocument(AssetId id)
    {
        if (authoring?.Find(id)?.Document is SceneDocument retained) return retained;
        var item = workspace?.AssetItems.FirstOrDefault(item => item.Id == id);
        if (item?.Asset is not SceneAsset) return null;
        return GetCanonicalDocument(item.FullPath.ToOSPath(), () => new SceneDocument(item.FullPath.ToOSPath(), sessionHistory, item.Package.IsReadOnly)) as SceneDocument;
    }
    private bool CanDropScene(AssetId id, EntityHierarchyDocument target, Guid? parent, string folder, int index, out string reason)
    {
        try { return CanDropSceneCore(id, target, parent, folder, index, out reason); }
        catch (InvalidOperationException) { reason = "The scene hierarchy cannot be resolved."; return false; }
    }
    private bool CanDropSceneCore(AssetId id, EntityHierarchyDocument target, Guid? parent, string folder, int index, out string reason)
    {
        reason = "Can only add a scene to another scene";
        if (target is not SceneDocument destination || parent != null
            || folder.Length != 0 && !destination.GetHierarchyFolders(null).Contains(folder, StringComparer.OrdinalIgnoreCase)
            || index < destination.Asset.Hierarchy.RootParts.Count || index > destination.Asset.Hierarchy.RootParts.Count + destination.Asset.ChildrenIds.Count) return false;
        var child = AuthoredScene(id);
        if (child == null || workspace == null || !target.CanSave || workspace.FindAsset(id).Package.IsReadOnly
            || authoring?.Find(id)?.Document is SceneDocument { CanSave: false }) return false;
        if (id == target.Asset.Id) { reason = "Can't be a parent of itself"; return false; }
        var previous = SceneParentId(child);
        if (previous == target.Asset.Id) { reason = "Scene already has this parent"; return false; }
        if (previous is { } old && (AuthoredScene(old) == null || workspace.FindAsset(old).Package.IsReadOnly
            || authoring?.Find(old)?.Document is SceneDocument { CanSave: false })) return false;
        var visited = new HashSet<AssetId>();
        SceneAsset? ancestor = destination.Asset;
        while (ancestor != null)
        {
            if (!visited.Add(ancestor.Id) || ancestor.Id == id) { reason = "Circular scene hierarchy"; return false; }
            var next = SceneParentId(ancestor);
            ancestor = next is { } key ? AuthoredScene(key) ?? throw new InvalidOperationException("The parent scene is not available.") : null;
        }
        reason = "Add to this scene"; return true;
    }
    private SceneDocument[] ReparentDroppedScenes(AssetId[] ids, SceneDocument target, int index, Vector3 position)
    {
        var chosen = ids.ToHashSet();
        var roots = ids.Where(id =>
        {
            var visited = new HashSet<AssetId>(); var ancestor = SceneParentId(AuthoredScene(id)!);
            while (ancestor is { } key)
            {
                if (!visited.Add(key)) throw new InvalidOperationException("Circular scene hierarchy");
                if (chosen.Contains(key)) return false;
                ancestor = SceneParentId(AuthoredScene(key) ?? throw new InvalidOperationException("The parent scene is not available."));
            }
            return true;
        }).ToArray();
        var changed = new Dictionary<AssetId, SceneHierarchyPlacement>();
        void Add(SceneDocument source) => changed.TryAdd(source.Asset.Id,
            new(source, source.Asset.ChildrenIds.ToArray(), source.Asset.Parent, source.Asset.Offset));
        Add(target);
        foreach (var id in roots)
        {
            var source = ResolveSceneHierarchyDocument(id) ?? throw new InvalidOperationException("The scene is not available.");
            if (!source.CanSave) throw new InvalidOperationException("This scene cannot be edited.");
            Add(source);
            if (SceneParentId(source.Asset) is { } previous)
            {
                var old = ResolveSceneHierarchyDocument(previous) ?? throw new InvalidOperationException("The parent scene is not available.");
                if (!old.CanSave || !old.Asset.ChildrenIds.Contains(id)) throw new InvalidOperationException("The scene parent relationship changed.");
                Add(old);
                var state = changed[previous]; changed[previous] = state with { Children = state.Children.Where(child => child != id).ToArray() };
            }
        }
        var insertion = index - target.Asset.Hierarchy.RootParts.Count;
        var targetState = changed[target.Asset.Id]; var children = targetState.Children.ToList();
        children.InsertRange(insertion, roots); changed[target.Asset.Id] = targetState with { Children = children.ToArray() };
        var reference = (Scene)ContentAssetReference.Create(typeof(Scene), workspace!.FindAsset(target.Asset.Id));
        foreach (var id in roots) { var child = changed[id]; changed[id] = child with { Parent = reference, Offset = position }; }
        var participants = changed.Values.Select(value => authoring!.Find(value.Document.Asset.Id)
            ?? throw new InvalidOperationException("The scene has no canonical authored owner.")).ToArray();
        foreach (var state in changed.Values)
            state.Document.ApplySceneHierarchy(state.Children, state.Parent, state.Offset, participants);
        return roots.Select(id => changed[id].Document).ToArray();
    }
    private void DrawHierarchySceneChildren()
    {
        if (document is not SceneDocument root || workspace == null) return;
        foreach (var id in root.Asset.ChildrenIds)
        {
            var item = workspace.AssetItems.FirstOrDefault(item => item.Id == id && item.Asset is SceneAsset);
            if (item == null) continue;
            Gui.PushID(id.ToString());
            if (Gui.Selectable(System.IO.Path.GetFileName(item.UnqualifiedUrl.ToString())))
            {
                var path = item.FullPath.ToOSPath(); pendingHierarchyPrefabNavigation = () => OpenScene(path);
            }
            if (Gui.BeginDragDropTarget())
            {
                var index = root.Asset.Hierarchy.RootParts.Count + root.Asset.ChildrenIds.IndexOf(id);
                if (Gui.GetIO().MousePos.Y > (Gui.GetItemRectMin().Y + Gui.GetItemRectMax().Y) / 2) index++;
                AcceptHierarchyAssetDrop(null, "", index, "this scene"); Gui.EndDragDropTarget();
            }
            Gui.PopID();
        }
    }
    private void DrawCompositeSceneHierarchy(SceneDocument root)
    {
        var visited = new HashSet<AssetId>();
        void DrawScene(SceneDocument scene)
        {
            if (!visited.Add(scene.Asset.Id)) return;
            Gui.PushID(scene.Asset.Id.ToString());
            Gui.SetNextItemOpen(true, ImGuiCond.Once);
            if (HasHierarchyContextReveal(scene)) Gui.SetNextItemOpen(true, ImGuiCond.Always);
            var flags = ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.SpanAvailWidth;
            SelectionService.RegisterSceneRootRow(scene);
            HierarchyController.SetKeyboardRowTarget(scene);
            if (SelectionService.IsSceneRootSelected(scene)) flags |= ImGuiTreeNodeFlags.Selected;
            var name = System.IO.Path.GetFileNameWithoutExtension(scene.Path);
            var nameWeight = ReferenceEquals(scene, document) ? 700u : 400u;
            var dropIndent = Gui.GetCursorScreenPos().X;
            var open = DrawHierarchyRow(name, "entity-scene", flags, forceSubmission: true,
                textColor: HierarchyMetadataTextColor(scene, (flags & ImGuiTreeNodeFlags.Selected) != 0), textWeight: nameWeight);
            RememberHierarchyDropRow(scene, open, dropIndent);
            if (!IsSceneLoaded(scene)) HierarchyController.DrawSceneUnloadedAnnotation(name, nameWeight, EditorLocalization.Text("(Unloaded)"));
            QueueSceneRootPointerSelection(scene);
            DrawSceneRootDragDrop(scene, root);
            if (ReferenceEquals(scene, document) && Controls.Enabled) Controls.Record("hierarchy/drop/root");
            DrawEntityDragDrop(null, scene);
            if (Gui.BeginPopupContextItem("Scene actions"))
            {
                if (!SelectionService.IsSceneRootSelected(scene))
                {
                    SelectionService.QueueSceneRootClick(scene, false, false);
                    CompleteHierarchyContentSelection(activateOwner: false);
                }
                DrawHierarchyContextCreation(scene, null);
                var sceneClipboard = Gui.GetClipboardTextS() ?? "";
                if (DrawNativeMenuItem(EditorLocalization.Widget("Paste", "Menu"), "Ctrl+V", false, CanPasteHierarchyDestinations(sceneClipboard, [(scene, [(null, "")])])))
                    pendingHierarchyPrefabNavigation = () =>
                    {
                        if (!CanPasteHierarchyDestinations(sceneClipboard, [(scene, [(null, "")])]))
                            throw new InvalidOperationException("The scene paste destination changed.");
                        if (!SelectionService.IsSceneRootSelected(scene))
                        { SelectionService.QueueSceneRootClick(scene, false, false); CompleteHierarchyContentSelection(); }
                        PasteHierarchySelection(false);
                    };
                var activeRootSelection = SelectionService.LastHierarchySelectedContent as SceneDocument;
                if (DrawNativeMenuItem(EditorLocalization.Widget("Set as active scene", "Menu"), "", ReferenceEquals(scene, document), !IsAuthoringPipelineUpdating))
                    pendingHierarchyPrefabNavigation = () => SetActiveHierarchyScene(activeRootSelection);
                if (DrawNativeMenuItem(EditorLocalization.Widget("Delete", "Menu"), "Delete", false, SelectionService.IsSceneRootSelected(scene)
                    ? CanDeleteHierarchySelection : CanRemoveSceneRoots([scene])))
                    pendingHierarchyPrefabNavigation = () =>
                    {
                        if (!SelectionService.IsSceneRootSelected(scene))
                        { SelectionService.QueueSceneRootClick(scene, false, false); CompleteHierarchyContentSelection(); }
                        RequestSelectedEntitiesDeletion();
                    };
                Gui.EndPopup();
            }
            if (icons != null)
            {
                var next = Gui.GetCursorScreenPos();
                Gui.SetCursorScreenPos(new System.Numerics.Vector2(hierarchyRowMaximum.X - HierarchyActionWidth, hierarchyRowMinimum.Y));
                var loaded = IsSceneLoaded(scene); var locked = IsSceneLocked(scene); var recursive = Gui.GetIO().KeyCtrl;
                if (HierarchyIconButton(loaded ? "scene-loaded" : "scene-unloaded", "Load/unload all entities"))
                    pendingHierarchyPrefabNavigation = () => RequestSceneLoading(scene, !loaded, recursive);
                Gui.SameLine(); Gui.BeginDisabled(!loaded);
                if (HierarchyIconButton(locked ? "scene-locked" : "scene-unlocked", "Lock/unlock all entities"))
                    pendingHierarchyPrefabNavigation = () => RequestSceneLocking(scene, !locked, recursive);
                Gui.EndDisabled();
                FinishHierarchyRow(next);
            }
            if (open)
            {
                if (ReferenceEquals(scene, document)) DrawHierarchyChildren(null);
                else
                {
                    if (!foreignSceneRows.TryGetValue(scene.Asset.Id, out var rows)) foreignSceneRows.Add(scene.Asset.Id, rows = new());
                    rows.Visible.Clear(); rows.Displayed.Clear();
                    bool Visit(Entity entity)
                    {
                        var visible = hierarchyFilterTokens.All(token => entity.Name.Contains(token, StringComparison.OrdinalIgnoreCase));
                        foreach (var child in entity.Transform.Children) visible |= Visit(child.Entity);
                        if (!visible) visible = scene.GetHierarchyFolders(entity.Id).Any(folder =>
                            hierarchyFilterTokens.All(token => folder[(folder.LastIndexOf('/') + 1)..].Contains(token, StringComparison.OrdinalIgnoreCase)));
                        if (visible) rows.Visible.Add(entity.Id);
                        return visible;
                    }
                    foreach (var entity in scene.Asset.Hierarchy.RootParts) Visit(entity);
                    DrawForeignSceneHierarchyChildren(scene, null, rows);
                }
                foreach (var id in scene.Asset.ChildrenIds)
                    if (ResolveSceneHierarchyDocument(id) is { } child) DrawScene(child);
                Gui.TreePop();
            }
            Gui.PopID();
        }
        DrawScene(root);
    }
    private void DrawForeignSceneHierarchyChildren(SceneDocument scene, Entity? parent, SceneHierarchyRows rows)
    {
        var entities = scene.GetHierarchyChildrenCached(parent?.Id);
        var folders = scene.GetHierarchyFolders(parent?.Id);
        void DrawFolder(string path)
        {
            var prefix = path.Length == 0 ? "" : path + "/";
            foreach (var folder in folders)
            {
                if (!folder.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || folder[prefix.Length..].Contains('/')) continue;
                if (!entities.Any(entity => rows.Visible.Contains(entity.Id) &&
                    (string.Equals(scene.Asset.Hierarchy.Parts[entity.Id].Folder, folder, StringComparison.OrdinalIgnoreCase)
                        || (scene.Asset.Hierarchy.Parts[entity.Id].Folder ?? "").StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase)))
                    && !folders.Any(candidate => candidate.StartsWith(folder, StringComparison.OrdinalIgnoreCase)
                        && hierarchyFilterTokens.All(token => candidate[(candidate.LastIndexOf('/') + 1)..].Contains(token, StringComparison.OrdinalIgnoreCase)))) continue;
                RegisterHierarchyFolderRow(scene, parent?.Id, folder);
                Gui.PushID((parent?.Id.ToString() ?? "root") + folder);
                ApplyHierarchyExpansion();
                OpenHierarchyContextFolder(scene, parent?.Id, folder);
                var folderFlags = ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.SpanAvailWidth;
                if (IsHierarchyFolderSelected(scene, parent?.Id, folder)) folderFlags |= ImGuiTreeNodeFlags.Selected;
                var editing = IsRenamingHierarchyFolder(scene, parent?.Id, folder);
                var keyboardIdentity = EditorTreeKeyboardAdapter.BeginRow(folder[prefix.Length..]);
                var textColor = HierarchyMetadataTextColor(scene, (folderFlags & ImGuiTreeNodeFlags.Selected) != 0);
                if (textColor is { } gray) Gui.PushStyleColor(ImGuiCol.Text, gray);
                var dropIndent = Gui.GetCursorScreenPos().X;
                var open = DrawNativeTreeNode(folder[prefix.Length..], folderFlags, editing, out var virtualized, paint: !editing);
                RememberHierarchyDropRow(HierarchyContentItem.Folder(scene, parent?.Id, folder), open, dropIndent);
                if (textColor != null) Gui.PopStyleColor();
                HierarchyController.RememberKeyboardRow(keyboardIdentity, folderFlags, open);
                if (virtualized)
                {
                    if (open) { DrawFolder(folder); Gui.TreePop(); }
                    Gui.PopID();
                    continue;
                }
                if (Gui.IsItemClicked() && !Gui.IsItemToggledOpen())
                    QueueHierarchyFolderClick(scene, parent?.Id, folder, Gui.GetIO().KeyCtrl, Gui.GetIO().KeyShift);
                var folderMinimum = Gui.GetItemRectMin(); var folderMaximum = Gui.GetItemRectMax();
                CompleteHierarchyContextFolderReveal(scene, parent?.Id, folder);
                if (Gui.BeginPopupContextItem("Folder actions"))
                {
                    EnsureHierarchyFolderSelected(scene, parent?.Id, folder);
                    DrawHierarchyContextCreation(scene, parent?.Id, folder);
                    void Defer(Action action)
                    {
                        var selectionVersion = SelectionService.ContentVersion;
                        pendingHierarchyPrefabNavigation = () =>
                        {
                            if (IsAuthoringPipelineUpdating || !IsCurrentHierarchyContextOwner(scene)
                                || !scene.GetHierarchyFolders(parent?.Id).Contains(folder, StringComparer.OrdinalIgnoreCase)
                                || SelectionService.ContentVersion != selectionVersion || !IsHierarchyFolderSelected(scene, parent?.Id, folder))
                                throw new InvalidOperationException("The hierarchy context selection changed.");
                            action();
                        };
                    }
                    if (DrawNativeMenuItem(EditorLocalization.Widget("Rename", "Menu"), "F2", false, scene.CanRenameHierarchyFolder(parent?.Id, folder)))
                        Defer(() => RequestHierarchyFolderRename(scene, parent?.Id, folder));
                    DrawHierarchyPrefabNavigationMenu(scene, Defer);
                    Gui.Separator();
                    if (DrawNativeMenuItem(EditorLocalization.Widget("Duplicate", "Menu"), "Ctrl+D", false, CanDuplicateHierarchyClipboard)) Defer(DuplicateHierarchySelection);
                    if (DrawNativeMenuItem(EditorLocalization.Widget("Copy", "Menu"), "Ctrl+C", false, scene.CanCopyHierarchyFolder(parent?.Id, folder)))
                        pendingHierarchyPrefabNavigation = () =>
                        {
                            if (!IsCurrentPresentedSceneRoot(scene)) throw new InvalidOperationException("The hierarchy copy owner changed.");
                            EnsureHierarchyFolderSelected(scene, parent?.Id, folder); CopyHierarchySelection(false);
                        };
                    if (DrawNativeMenuItem(EditorLocalization.Widget("Cut", "Menu"), "Ctrl+X", false, scene.CanCutHierarchyFolder(parent?.Id, folder))) Defer(() => CopyHierarchySelection(true));
                    if (DrawNativeMenuItem(EditorLocalization.Widget("Paste", "Menu"), "Ctrl+V", false, scene.CanPasteHierarchyFolder(Gui.GetClipboardTextS() ?? "", parent?.Id, folder))) Defer(() => PasteHierarchySelection(false));
                    if (DrawNativeMenuItem(EditorLocalization.Widget("Delete", "Menu"), "Delete", false, CanDeleteHierarchySelection)) Defer(RequestSelectedEntitiesDeletion);
                    Gui.EndPopup();
                }
                if (editing) DrawHierarchyFolderRenameField(scene, parent?.Id, folder,
                    folderMinimum + new System.Numerics.Vector2(Gui.GetTreeNodeToLabelSpacing(), 0), folderMaximum);
                if (open)
                { DrawFolder(folder); Gui.TreePop(); }
                Gui.PopID();
            }
            foreach (var entity in entities)
            {
                if (!string.Equals((scene.Asset.Hierarchy.Parts[entity.Id].Folder ?? "").Trim('/'), path, StringComparison.OrdinalIgnoreCase)) continue;
                if (!rows.Visible.Contains(entity.Id)) continue;
                rows.Displayed.Add(entity.Id);
                RegisterHierarchyEntityRow(scene, entity.Id);
                Gui.PushID(entity.Id.ToString());
                var flags = ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.SpanAvailWidth;
                if (entity.Transform.Children.Count == 0 && scene.GetHierarchyFolders(entity.Id).Count == 0) flags |= ImGuiTreeNodeFlags.Leaf;
                if (IsHierarchyEntityContentSelected(scene, entity.Id)) flags |= ImGuiTreeNodeFlags.Selected;
                OpenHierarchyContextEntity(scene, entity);
                var editingEntity = IsInlineHierarchyEntityRenaming(scene, entity);
                var dropIndent = Gui.GetCursorScreenPos().X;
                var open = DrawHierarchyRow(editingEntity ? "" : entity.Name, HierarchyEntityIcon(entity), flags,
                    forceSubmission: editingEntity || HasHierarchyContextReveal(scene) && hierarchyRevealEntity == entity.Id,
                    textColor: HierarchyMetadataTextColor(scene, (flags & ImGuiTreeNodeFlags.Selected) != 0));
                RememberHierarchyDropRow(HierarchyContentItem.Entity(scene, entity.Id), open, dropIndent);
                if (HierarchyController.RowWasVirtualized)
                {
                    if (open) { DrawForeignSceneHierarchyChildren(scene, entity, rows); Gui.TreePop(); }
                    Gui.PopID();
                    continue;
                }
                RememberVisualCaptureHierarchyEntity(scene, entity.Id);
                CompleteHierarchyContextParentReveal(scene, entity);
                DrawHierarchyEntityAnnotations(scene, entity, editingEntity ? "" : entity.Name);
                if (HasHierarchyContextReveal(scene) && hierarchyRevealEntity == entity.Id)
                { Gui.SetScrollHereY(0.5f); ClearHierarchyContextReveal(); }
                if (Gui.IsItemClicked() && !Gui.IsItemToggledOpen() && Gui.GetIO().MousePos.X < hierarchyRowMaximum.X - HierarchyActionWidth)
                {
                    var toggle = Gui.GetIO().KeyCtrl; var range = Gui.GetIO().KeyShift;
                    if (!toggle && !range && scene.IsSelected(entity.Id) && SelectedHierarchyDocuments.Sum(owner => owner.SelectedCount) > 1)
                    { pendingHierarchyGroupClick = (scene, entity); hierarchyGroupClickDragged = false; }
                    else { pendingHierarchyGroupClick = null; QueueHierarchyEntityClick(scene, entity.Id, toggle, range); }
                }
                DrawEntityDragDrop(entity, scene);
                if (Gui.BeginPopupContextItem("Entity actions"))
                {
                    if (!IsHierarchyEntityContentSelected(scene, entity.Id)) SelectCompositeHierarchyEntity(scene, entity, false);
                    DrawHierarchyContextCreation(scene, entity.Id);
                    void Defer(Action action) => pendingHierarchyPrefabNavigation = () =>
                    {
                        if (!IsCurrentHierarchyContextOwner(scene)
                            || !scene.Asset.Hierarchy.Parts.TryGetValue(entity.Id, out var current) || !ReferenceEquals(current.Entity, entity))
                            throw new InvalidOperationException("The hierarchy destination changed.");
                        if (!scene.IsSelected(entity.Id)) SelectCompositeHierarchyEntity(scene, entity, false); action();
                    };
                    if (DrawNativeMenuItem(EditorLocalization.Widget("Rename", "Menu"), "F2", false, scene.CanRenameEntity(entity)))
                        Defer(() => BeginEntityRename(scene, entity));
                    if (DrawNativeMenuItem(EditorLocalization.Widget("Delete", "Menu"), "Delete", false, scene.CanDeleteEntity(entity))) Defer(() => RequestEntityDeletion(scene, entity));
                    if (DrawNativeMenuItem("Move to...", "", false, scene.CanDeleteEntity(entity))) Defer(() => RequestEntityMove(scene, entity));
                    if (Controls.Enabled) Controls.Record("hierarchy/move");
                    Gui.Separator();
                    if (DrawNativeMenuItem(EditorLocalization.Widget("Duplicate", "Menu"), "Ctrl+D", false, CanDuplicateHierarchyClipboard)) Defer(DuplicateHierarchySelection);
                    if (DrawNativeMenuItem(EditorLocalization.Widget("Copy", "Menu"), "Ctrl+C", false, CanCopyHierarchyClipboard))
                        pendingHierarchyPrefabNavigation = () =>
                        {
                            if (!IsCurrentPresentedSceneRoot(scene) || !scene.Asset.Hierarchy.Parts.TryGetValue(entity.Id, out var copied)
                                || !ReferenceEquals(copied.Entity, entity)) throw new InvalidOperationException("The hierarchy copy owner changed.");
                            if (!scene.IsSelected(entity.Id)) SelectCompositeHierarchyEntity(scene, entity, false);
                            CopyHierarchySelection(false);
                        };
                    if (DrawNativeMenuItem(EditorLocalization.Widget("Cut", "Menu"), "Ctrl+X", false, CanCutHierarchyClipboard)) Defer(() => CopyHierarchySelection(true));
                    var clipboard = Gui.GetClipboardTextS() ?? "";
                    var pasteTargets = HierarchyPasteContentTargets();
                    if (DrawNativeMenuItem(EditorLocalization.Widget("Paste", "Menu"), "Ctrl+V", false, CanPasteHierarchyDestinations(clipboard, pasteTargets)))
                        Defer(() => PasteHierarchySelection(false));
                    Gui.Separator();
                    DrawHierarchyPrefabNavigationMenu(scene, Defer);
                    Gui.EndPopup();
                }
                DrawHierarchyContextEntityRename(scene, entity);
                DrawHierarchyEntityActions(scene, entity);
                if (open) { DrawForeignSceneHierarchyChildren(scene, entity, rows); Gui.TreePop(); }
                Gui.PopID();
            }
        }
        DrawFolder("");
    }
}
