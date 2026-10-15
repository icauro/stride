using Hexa.NET.ImGui;
using Stride.Engine;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private (Guid? Owner, string Path)? selectedHierarchyFolder;
    private EntityHierarchyDocument? folderSelectionDocument;
    private (Guid? Owner, string Path)? renamingHierarchyFolder;
    private string hierarchyFolderName = "";

    private void DrawHierarchyChildren(Entity? parent)
    {
        if (folderSelectionDocument != null && !IsCurrentHierarchyDragSource(folderSelectionDocument))
        {
            folderSelectionDocument = null; selectedHierarchyFolder = null;
        }
        if (activeHierarchyFolderDrag is { } drag && !IsCurrentHierarchyDragSource(drag.Document)) activeHierarchyFolderDrag = null;
        var owner = parent?.Id;
        var entities = document!.GetHierarchyChildrenCached(owner);
        var folders = document.GetHierarchyFolders(owner);
        void DrawFolderChildren(string path)
        {
            var prefix = path.Length == 0 ? "" : path + "/";
            foreach (var folder in folders.Where(folder => folder.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !folder[prefix.Length..].Contains('/')))
            {
                var name = folder[prefix.Length..];
                var visible = entities.Any(entity => hierarchyVisible.Contains(entity.Id) &&
                    (string.Equals(document.Asset.Hierarchy.Parts[entity.Id].Folder, folder, StringComparison.OrdinalIgnoreCase)
                    || (document.Asset.Hierarchy.Parts[entity.Id].Folder ?? "").StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase)))
                    || folders.Any(candidate => (candidate == folder || candidate.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase))
                        && hierarchyFilterTokens.All(token => candidate[(candidate.LastIndexOf('/') + 1)..].Contains(token, StringComparison.OrdinalIgnoreCase)));
                if (!visible) continue;
                RegisterHierarchyFolderRow(document, owner, folder);
                Gui.PushID((owner?.ToString() ?? "root") + "/" + folder.ToUpperInvariant());
                var flags = ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.SpanAvailWidth;
                if (IsHierarchyFolderSelected(document, owner, folder)) flags |= ImGuiTreeNodeFlags.Selected;
                if (revealHierarchyFolder is { } reveal && reveal.Owner == owner &&
                    (string.Equals(reveal.Path, folder, StringComparison.OrdinalIgnoreCase) || reveal.Path.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase)))
                { Gui.SetNextItemOpen(true, ImGuiCond.Always); if (string.Equals(reveal.Path, folder, StringComparison.OrdinalIgnoreCase)) revealHierarchyFolder = null; }
                ApplyHierarchyExpansion();
                var editing = IsRenamingHierarchyFolder(document, owner, folder);
                var keyboardIdentity = EditorTreeKeyboardAdapter.BeginRow("##Folder");
                var dropIndent = Gui.GetCursorScreenPos().X;
                var expanded = DrawNativeTreeNode("##Folder", flags, editing, out var virtualized, caption: name, paint: false);
                RememberHierarchyDropRow(HierarchyContentItem.Folder(document, owner, folder), expanded, dropIndent);
                HierarchyController.RememberKeyboardRow(keyboardIdentity, flags, expanded);
                if (virtualized)
                {
                    if (expanded) { DrawFolderChildren(folder); Gui.TreePop(); }
                    Gui.PopID();
                    continue;
                }
                var rowOrigin = Gui.GetItemRectMin();
                var rowMaximum = Gui.GetItemRectMax();
                var iconSize = Gui.GetTextLineHeight();
                var labelOrigin = rowOrigin + new System.Numerics.Vector2(Gui.GetTreeNodeToLabelSpacing(),
                    Math.Max(0, (Gui.GetItemRectSize().Y - iconSize) * 0.5f));
                DrawHierarchyFolderDragDrop(owner, folder);
                if (Gui.IsItemClicked() && !Gui.IsItemToggledOpen())
                { QueueHierarchyFolderClick(document, owner, folder, Gui.GetIO().KeyCtrl, Gui.GetIO().KeyShift); }
                DrawHierarchyFolderDragSource(owner, folder);
                if (Gui.BeginPopupContextItem("Folder actions"))
                {
                    EnsureHierarchyFolderSelected(document, owner, folder);
                    DrawHierarchyContextCreation(document, owner, folder);
                    void Dispatch(Action action)
                    {
                        var source = document!;
                        var selectionVersion = SelectionService.ContentVersion;
                        pendingHierarchyPrefabNavigation = () =>
                        {
                            if (IsAuthoringPipelineUpdating || !IsCurrentHierarchyContextOwner(source)
                                || !source.GetHierarchyFolders(owner).Contains(folder, StringComparer.OrdinalIgnoreCase)
                                || SelectionService.ContentVersion != selectionVersion || !IsHierarchyFolderSelected(source, owner, folder))
                                throw new InvalidOperationException("The hierarchy context selection changed.");
                            action();
                        };
                    }
                    DrawHierarchyPrefabNavigationMenu(document, Dispatch);
                    if (DrawNativeMenuItem(EditorLocalization.Widget("Rename", "Menu"), "F2", false, document.CanRenameHierarchyFolder(owner, folder))) RequestHierarchyFolderRename(owner, folder);
                    if (DrawNativeMenuItem(EditorLocalization.Widget("Delete", "Menu"), "Delete", false, CanDeleteHierarchySelection)) Dispatch(RequestSelectedEntitiesDeletion);
                    if (DrawNativeMenuItem("Move selected entities here", "", false, document.CanMoveSelectionToFolder(owner)))
                        Execute(() => { MoveHierarchyEntitiesToFolder(document, document.GetSelectedTransformRoots(), owner, folder, Gui.GetIO().KeyAlt); revealHierarchyFolder = (owner, folder); revealCreatedParent = owner; });
                    Gui.Separator(); DrawHierarchyClipboardMenu();
                    Gui.EndPopup();
                }
                if (icons != null) Gui.GetWindowDrawList().AddImage(icons.Get(expanded ? "folder-open" : "folder"), labelOrigin,
                    labelOrigin + new System.Numerics.Vector2(iconSize));
                var textSize = MeasureNativeLabel(name);
                var textOrigin = new System.Numerics.Vector2(labelOrigin.X + iconSize + Gui.GetStyle().ItemInnerSpacing.X,
                    rowOrigin.Y + Math.Max(0, (rowMaximum.Y - rowOrigin.Y - textSize.Y) / 2));
                if (editing) DrawHierarchyFolderRenameField(document, owner, folder, new System.Numerics.Vector2(textOrigin.X, rowOrigin.Y), rowMaximum);
                else DrawNativeTreeText(name, textOrigin, rowOrigin, rowMaximum, HierarchyMetadataTextColor(document, (flags & ImGuiTreeNodeFlags.Selected) != 0));
                if (expanded) { DrawFolderChildren(folder); Gui.TreePop(); }
                Gui.PopID();
            }
            foreach (var entity in entities)
                if (string.Equals((document.Asset.Hierarchy.Parts[entity.Id].Folder ?? "").Trim('/'), path, StringComparison.OrdinalIgnoreCase)) DrawEntity(entity);
        }
        DrawFolderChildren("");
    }

    private void DrawHierarchyFolderCreationMenu()
    {
        if (DrawNativeMenuItem("Create folder at root", "", false, document?.CanEditHierarchyFolders(null) == true))
            Execute(() => CreateHierarchyFolderUi(null, ""));
        if (DrawNativeMenuItem("Create folder in selection", "", false, CanCreateInHierarchySelection(true)))
            Execute(CreateHierarchyFolderInSelection);
    }

    private void CreateHierarchyFolderUi(Guid? owner, string path)
        => CreateHierarchyFolderUi(document, owner, path);
    private void CreateHierarchyFolderUi(EntityHierarchyDocument? target, Guid? owner, string path)
    {
        if (target == null || IsAuthoringPipelineUpdating || !IsCurrentHierarchyContextOwner(target) || !target.CanEditHierarchyFolders(owner)) return;
        ValidatePendingPropertyStrings();
        if (!IsCurrentHierarchyContextOwner(target) || !target.CanEditHierarchyFolders(owner)) return;
        var created = target.CreateHierarchyFolder(owner, path);
        EnsureHierarchyFolderSelected(target, owner, created);
        RevealHierarchyContext(target, null, owner, created);
        RequestHierarchyFolderRename(target, owner, created);
    }

    private void DrawEntityFolderMoveMenu(Entity entity)
    {
        if (!BeginNativeMenu("Move to folder", document!.CanMoveSelectionToFolder(entity.Transform.Parent?.Entity.Id))) return;
        var owner = entity.Transform.Parent?.Entity.Id;
        if (DrawNativeMenuItem("No folder")) Execute(() => MoveHierarchySelectionToFolder(owner, ""));
        foreach (var folder in document.GetHierarchyFolders(owner))
        {
            Gui.PushID(folder);
            if (Gui.Selectable("##FolderMove")) Execute(() => MoveHierarchySelectionToFolder(owner, folder));
            Gui.SameLine(); Gui.TextUnformatted(folder); Gui.PopID();
        }
        Gui.EndMenu();
    }

    private void RequestHierarchyFolderRename(Guid? owner, string path)
    {
        var primary = SelectionService.PrimaryHierarchyContent;
        var target = primary is { FolderPath: { } selected } folder && folder.FolderOwner == owner
            && string.Equals(selected, path, StringComparison.OrdinalIgnoreCase) ? folder.Document : folderSelectionDocument ?? document;
        RequestHierarchyFolderRename(target, owner, path);
    }
    private void RequestHierarchyFolderRename(EntityHierarchyDocument? target, Guid? owner, string path)
    {
        FinishEntityRename(true);
        if (target == null || IsAuthoringPipelineUpdating || !IsCurrentHierarchyContextOwner(target) || !target.CanRenameHierarchyFolder(owner, path)) return;
        ValidatePendingPropertyStrings();
        if (!IsCurrentHierarchyContextOwner(target) || !target.CanRenameHierarchyFolder(owner, path)) return;
        hierarchyFolderRenameOwner = new(target);
        renamingHierarchyFolder = (owner, path); hierarchyFolderName = path[(path.LastIndexOf('/') + 1)..];
        focusHierarchyFolderRename = true; hierarchyFilter = "";
        RevealHierarchyContext(target, null, owner, path);
    }
    private void DrawHierarchyFolderRename()
    {
        if (renamingHierarchyFolder == null) { hierarchyFolderRenameOwner = null; return; }
        if (pendingHierarchyFolderRenameCommit) { pendingHierarchyFolderRenameCommit = false; Execute(() => FinishHierarchyFolderRename(true)); }
    }
}
