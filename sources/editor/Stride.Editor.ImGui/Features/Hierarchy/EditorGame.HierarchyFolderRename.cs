using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;
using Vector2 = System.Numerics.Vector2;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private WeakReference<EntityHierarchyDocument>? hierarchyFolderRenameOwner;
    private bool focusHierarchyFolderRename, pendingHierarchyFolderRenameCommit;
    private bool HasPendingHierarchyFolderRenameChanges => renamingHierarchyFolder is { } folder
        && folder.Path[(folder.Path.LastIndexOf('/') + 1)..] != hierarchyFolderName;
    private bool IsRenamingHierarchyFolder(EntityHierarchyDocument owner, Guid? parent, string path)
        => hierarchyFolderRenameOwner?.TryGetTarget(out var target) == true && ReferenceEquals(owner, target) && renamingHierarchyFolder is { } folder
            && folder.Owner == parent && string.Equals(folder.Path, path, StringComparison.OrdinalIgnoreCase);
    private void DrawHierarchyFolderRenameField(EntityHierarchyDocument owner, Guid? parent, string path, Vector2 origin, Vector2 maximum)
    {
        if (!IsRenamingHierarchyFolder(owner, parent, path)) return;
        var next = Gui.GetCursorScreenPos();
        Gui.SetCursorScreenPos(origin);
        Gui.SetNextItemWidth(Math.Max(UiScale(40), maximum.X - origin.X));
        if (focusHierarchyFolderRename) { Gui.SetKeyboardFocusHere(); focusHierarchyFolderRename = false; }
        var commit = Gui.InputText("##HierarchyFolderName", ref hierarchyFolderName, 4096,
            ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.AutoSelectAll);
        var cancel = Gui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows) && Gui.IsKeyPressed(ImGuiKey.Escape);
        if (cancel) FinishHierarchyFolderRename(false);
        else if (commit || Gui.IsItemDeactivatedAfterEdit()) pendingHierarchyFolderRenameCommit = true;
        Gui.SetCursorScreenPos(next);
    }
    private void FinishHierarchyFolderRename(bool commit)
    {
        if (renamingHierarchyFolder is not { } folder) { hierarchyFolderRenameOwner = null; pendingHierarchyFolderRenameCommit = false; return; }
        var owner = hierarchyFolderRenameOwner?.TryGetTarget(out var target) == true ? target : null;
        var name = hierarchyFolderName;
        if (commit && folder.Path[(folder.Path.LastIndexOf('/') + 1)..] != name)
        {
            if (owner == null || IsAuthoringPipelineUpdating || sessionHistory?.Registry.Find(owner.Asset.Id) is not { IsPublished: true } state
                || !ReferenceEquals(state.Document, owner) || !owner.CanRenameHierarchyFolder(folder.Owner, folder.Path))
                throw new InvalidOperationException("The folder rename owner is no longer editable.");
            ValidatePendingPropertyStrings();
            if (!IsCurrentHierarchyContextOwner(owner) || !owner.CanRenameHierarchyFolder(folder.Owner, folder.Path))
                throw new InvalidOperationException("The folder rename owner changed.");
            var slash = folder.Path.LastIndexOf('/');
            var replacement = (slash < 0 ? "" : folder.Path[..(slash + 1)]) + name;
            RenameHierarchyFolderWithSelection(owner, folder.Owner, folder.Path, name, replacement, state);
            RevealHierarchyContext(owner, null, folder.Owner, replacement);
        }
        renamingHierarchyFolder = null; hierarchyFolderRenameOwner = null;
        pendingHierarchyFolderRenameCommit = focusHierarchyFolderRename = false;
    }
}
