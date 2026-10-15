using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class ContentReferencePicker
{
    private AssetBrowserContent? renamingContent;
    private string renameText = "";
    private bool renameFocus, renameEscapeHandled, contextSuspended;
    private int contextWaitFrames;

    private void DrawPickerContext(AssetBrowserContent row, ModalRequest request, bool hovered)
    {
        if (hovered && Gui.IsMouseReleased(ImGuiMouseButton.Right))
        { if (!modalBrowser.IsContentSelected(row)) modalBrowser.SelectContent(row, false, false); Gui.OpenPopup("##PickerAssetActions"); }
        if (!Gui.BeginPopup("##PickerAssetActions")) return;
        try { DrawPickerContextItems(row, request); }
        finally { Gui.EndPopup(); }
    }

    private void DrawPickerContextItems(AssetBrowserContent? row, ModalRequest request)
    {
        if (!ReferenceEquals(modal, request) || !request.IsCurrent() || request.ContextMutationPending || Gui.GetIO().AppFocusLost)
        { Gui.CloseCurrentPopup(); return; }
        var rules = row == null ? new PickerContextCapabilities(false, false, false, false, false) : PickerSelectionCapabilities(row);
        Gui.TextDisabled(EditorLocalization.Text("Asset", "Menu"));
        if (Gui.MenuItem(EditorLocalization.Widget("Delete", "Menu"), "Delete", false, rules.Delete)) Run(PickerContextAction.Delete);
        if (Gui.MenuItem(EditorLocalization.Widget("Rename", "Menu"), "F2", false, rules.Rename)) BeginPickerRename(row!);
        Gui.Separator(); Gui.TextDisabled(EditorLocalization.Text("Explore", "Menu"));
        if (Gui.MenuItem(EditorLocalization.Widget("OpenWithTextEditor", "Menu"), "", false, rules.Asset)) Run(PickerContextAction.OpenText);
        if (Gui.MenuItem(EditorLocalization.Widget("OpenAssetFile", "Menu"), "", false, rules.Asset)) Run(PickerContextAction.OpenAsset);
        if (Gui.MenuItem(EditorLocalization.Widget("OpenSourceFile", "Menu"), "", false, rules.Asset)) Run(PickerContextAction.OpenSource);
        var locationExplore = row == null ? host.CaptureBackgroundLocationExplore() : null;
        if (Gui.MenuItem(EditorLocalization.Widget("ShowInExplorer", "Menu"), "", false, rules.Explore || locationExplore != null))
        {
            if (row != null) Run(PickerContextAction.Explore);
            else if (request.IsCurrent())
            {
                locationExplore?.Invoke();
                if (host.ContextCommandPending) { contextSuspended = true; contextWaitFrames = 2; }
            }
        }
        void Run(PickerContextAction action)
        {
            if (row == null) return;
            RunPickerContextAction(request, row, action);
            if (action is PickerContextAction.Delete or PickerContextAction.OpenAsset or PickerContextAction.OpenText || host.ContextCommandPending)
            { contextSuspended = true; contextWaitFrames = 2; }
        }
    }

    private void BeginPickerRename(AssetBrowserContent row)
    {
        renamingContent = row;
        renameText = row.Asset is { } asset ? Path.GetFileName(asset.Name) : row.Name;
        renameFocus = true;
    }

    private bool DrawPickerRename(AssetBrowserContent row, ModalRequest request, float width = -1)
    {
        if (renamingContent?.Id != row.Id) return false;
        if (!PickerSelectionCapabilities(row).Rename) { renamingContent = null; return false; }
        if (renameFocus) { Gui.SetKeyboardFocusHere(); renameFocus = false; }
        Gui.SetNextItemWidth(width);
        var renameWasActive = ImGuiP.GetActiveID() == Gui.GetID("##Rename");
        var commit = NativeStringInput.Draw("##Rename", ref renameText, ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.AutoSelectAll);
        if ((renameWasActive || Gui.IsItemActive()) && !Gui.GetIO().AppFocusLost && Gui.IsKeyPressed(ImGuiKey.Escape, false))
        {
            ImGuiP.SetKeyOwner(ImGuiKey.Escape, Gui.GetID("##Rename"), (ImGuiInputFlags)ImGuiInputFlagsPrivate.LockThisFrame);
            renamingContent = null; renameEscapeHandled = true;
        }
        else if (commit || Gui.IsItemDeactivatedAfterEdit())
        {
            RunPickerContextAction(request, renamingContent, PickerContextAction.Rename, renameText);
            renamingContent = null;
        }
        return true;
    }

    private void DrawPickerContextShortcuts(ModalRequest request)
    {
        var io = Gui.GetIO();
        if (!request.IsCurrent() || !Gui.IsWindowFocused(ImGuiFocusedFlags.ChildWindows) || io.AppFocusLost
            || io.KeyCtrl || io.KeyShift || io.KeyAlt || io.KeySuper || io.WantTextInput || Gui.IsAnyItemActive()
            || Gui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId)) return;
        var row = modalBrowser.PrimaryEntry is { } asset ? new AssetBrowserContent(asset, null)
            : modalBrowser.SelectedContentFolders.SingleOrDefault() is { } folder ? new AssetBrowserContent(null, folder) : null;
        if (row == null) return;
        var rules = PickerSelectionCapabilities(row);
        if (rules.Rename && Gui.IsKeyPressed(ImGuiKey.F2, false))
        {
            ImGuiP.SetKeyOwner(ImGuiKey.F2, Gui.GetID("PickerRename"), (ImGuiInputFlags)ImGuiInputFlagsPrivate.LockThisFrame);
            BeginPickerRename(row);
        }
        if (rules.Delete && Gui.IsKeyPressed(ImGuiKey.Delete, false))
        {
            ImGuiP.SetKeyOwner(ImGuiKey.Delete, Gui.GetID("PickerDelete"), (ImGuiInputFlags)ImGuiInputFlagsPrivate.LockThisFrame);
            RunPickerContextAction(request, row, PickerContextAction.Delete);
            contextSuspended = true; contextWaitFrames = 2;
        }
    }

    private void RunPickerContextAction(ModalRequest request, AssetBrowserContent row, PickerContextAction action, string? name = null)
    {
        if (request.ContextMutationPending || !request.IsCurrent()) return;
        var rules = PickerSelectionCapabilities(row);
        if (action switch
        {
            PickerContextAction.Rename => !rules.Rename,
            PickerContextAction.Delete => !rules.Delete,
            PickerContextAction.OpenAsset or PickerContextAction.OpenText => !rules.Asset,
            PickerContextAction.OpenSource => !rules.Asset,
            PickerContextAction.Explore => !rules.Explore,
            _ => true,
        }) return;
        row = PickerContextTarget(row, action);
        if (action is not (PickerContextAction.Rename or PickerContextAction.Delete))
        { host.RunContextAction(row, action, modalBrowser, request.IsCurrent, name); return; }
        request.ContextMutationPending = true;
        host.RunContextAction(row, action, modalBrowser, request.IsCurrent, name, succeeded =>
        {
            request.ContextMutationPending = false;
            if (!ReferenceEquals(modal, request)) return;
            if (succeeded)
            {
                // Only the actual writer's completed, guarded operation may advance the source baseline.
                if (!request.Certificate.TryRebaselineAfterAuthorizedMutation()) { CancelModal(); return; }
                modalBrowser.RefreshEntries(request.Workspace);
            }
            else if (!request.IsCurrent()) CancelModal();
        });
    }

}
