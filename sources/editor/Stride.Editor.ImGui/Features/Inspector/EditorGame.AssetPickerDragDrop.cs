using Hexa.NET.ImGui;
using Stride.Core.Assets;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private sealed record PickerDragOwner(Guid Token, AssetBrowserState Browser, long Version, Func<bool> Current);
    private PickerDragOwner? pickerDragOwner;
    private bool IsCurrentPickerDragOwner(Guid token) => pickerDragOwner is not { } owner || owner.Token != token
        || owner.Current() && owner.Browser.SelectionVersion == owner.Version;

    private unsafe void DrawPickerAssetDragSource(AssetBrowserContent content, AssetBrowserState browser, Func<bool> isCurrent)
    {
        if (workspace == null || !isCurrent() || !Gui.BeginDragDropSource()) return;
        try
        {
            var rows = browser.IsContentSelected(content) ? browser.SelectedContent.ToArray() : [content];
            var folders = rows.Where(row => row.Folder != null).Select(row => row.Folder!).ToArray();
            folders = folders.Where(folder => !folders.Any(parent => parent.Id != folder.Id
                && folder.Id.StartsWith(parent.Id + "/", StringComparison.OrdinalIgnoreCase))).ToArray();
            var ids = rows.Where(row => row.Asset != null).Select(row => row.Asset!.Id).ToArray();
            if (folders.Length != 0)
            {
                if (folders.Any(folder => !CanRelocateAssetFolder(folder)) || rows.Any(row => row.Asset is { } selected && !CanRelocateAsset(selected))) return;
                if (pickerDragOwner is not { } previous || !ReferenceEquals(previous.Browser, browser) || previous.Version != browser.SelectionVersion
                    || IsNewAssetDragGesture(AssetFolderDragType) || activeAssetFolderDrag?.Primary != content.Id)
                {
                    activeAssetFolderDrag = CaptureAssetFolderDragSelection(content.Id, folders.Select(folder => folder.Id).ToArray(), ids, browser);
                    pickerDragOwner = new(activeAssetFolderDrag.Token, browser, browser.SelectionVersion, isCurrent);
                }
                if (activeAssetFolderDrag is not { } selection || !IsCurrentAssetFolderDragSelection(selection)) return;
                var payload = new AssetFolderPayload { Workspace = selection.Workspace, Selection = selection.Token };
                Gui.SetDragDropPayload(AssetFolderDragType, &payload, (nuint)sizeof(AssetFolderPayload));
                foreach (var row in rows) DrawNativeLabel(row.Name);
            }
            else if (content.Asset is { } asset)
            {
                if (pickerDragOwner is not { } previous || !ReferenceEquals(previous.Browser, browser) || previous.Version != browser.SelectionVersion
                    || IsNewAssetDragGesture(AssetDragType) || activeAssetDragSelection?.Primary != asset.Id)
                {
                    activeAssetDragSelection = CaptureAssetDragSelection(asset.Id, browser.SelectionVersion, ids, true);
                    pickerDragOwner = new(activeAssetDragSelection.Token, browser, browser.SelectionVersion, isCurrent);
                }
                if (activeAssetDragSelection is { } selection && IsCurrentAssetDragSelection(selection)) PublishAssetDragSelection(selection);
            }
        }
        finally { Gui.EndDragDropSource(); }
    }

    private bool CanMovePickerFolderContent(AssetFolderDragSelection selection, AssetBrowserFolder target)
    {
        if (!IsCurrentAssetFolderDragSelection(selection) || !CanStartAssetEdit || !IsCurrentEditableAssetPackage(target.Package)) return false;
        var folders = selection.Folders.Select(assetBrowser.FindFolder).OfType<AssetBrowserFolder>().ToArray();
        if (folders.Length != selection.Folders.Length || folders.Any(folder => !CanRelocateAssetFolder(folder) || folder.IsCode != target.IsCode
            || target.Id == folder.Id || target.Id.StartsWith(folder.Id + "/", StringComparison.OrdinalIgnoreCase)
            || target.Children.Any(child => child.Name.Equals(folder.Name, StringComparison.OrdinalIgnoreCase)))) return false;
        if (folders.Select(folder => folder.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != folders.Length) return false;
        var contained = folders.SelectMany(AssetFolderClipboardItems).Select(item => item.Id).ToHashSet();
        var assets = selection.Assets.Where(id => !contained.Contains(id)).ToArray();
        return selection.Folders.Length + selection.Assets.Length != 0 && (assets.Length == 0 || CanRelocateDraggedAssets(assets, target));
    }

    private unsafe PickerAuthoringCommand? DrawPickerAssetDropTarget(AssetBrowserFolder target, Func<bool> isCurrent)
    {
        if (!isCurrent() || !CanStartAssetEdit || !IsCurrentEditableAssetPackage(target.Package) || !Gui.BeginDragDropTarget()) return null;
        PickerAuthoringCommand? command = null;
        try
        {
            AssetFolderDragSelection? captured = null;
            var assetPayload = Gui.AcceptDragDropPayload(AssetDragType, ImGuiDragDropFlags.AcceptBeforeDelivery | ImGuiDragDropFlags.AcceptNoDrawDefaultRect);
            if (!assetPayload.IsNull && assetPayload.DataSize == sizeof(AssetLocationPayload))
            {
                var value = *(AssetLocationPayload*)assetPayload.Data;
                if (TryGetAssetDragIds(value, out var ids) && activeAssetDragSelection is { } source && CanRelocateDraggedAssets(ids, target))
                    captured = new(source.Workspace, source.Token, "asset/" + source.Primary, [], ids) { Items = source.Items };
            }
            var folderPayload = Gui.AcceptDragDropPayload(AssetFolderDragType, ImGuiDragDropFlags.AcceptBeforeDelivery | ImGuiDragDropFlags.AcceptNoDrawDefaultRect);
            if (!folderPayload.IsNull && folderPayload.DataSize == sizeof(AssetFolderPayload))
            {
                var value = *(AssetFolderPayload*)folderPayload.Data;
                if (activeAssetFolderDrag is { } source && source.Token == value.Selection && source.Workspace == value.Workspace
                    && CanMovePickerFolderContent(source, target)) captured = source;
            }
            if (captured == null) return null;
            Gui.GetWindowDrawList().AddRect(Gui.GetItemRectMin(), Gui.GetItemRectMax(), Gui.GetColorU32(ImGuiCol.DragDropTarget), 0, ImDrawFlags.None, 2);
            if ((!assetPayload.IsNull && assetPayload.IsDelivery()) || (!folderPayload.IsNull && folderPayload.IsDelivery()))
            {
                var selection = captured; var owner = workspace;
                var completedOnce = false;
                void FinishTransfer(Action<bool> completed, bool succeeded)
                { if (completedOnce) return; completedOnce = true; completed(succeeded); }
                command = completed => Execute(() =>
                {
                    try
                    {
                        var before = CaptureCreationSelection(assetBrowser);
                        var propertyActive = PropertyGrid.AssetSelectionActive; var propertyScene = PropertyGrid.AssetContextScene;
                        var propertySelection = PropertyGrid.AssetContextSceneSelection;
                        if (!ReferenceEquals(workspace, owner) || !isCurrent() || !IsCurrentAssetFolderDragSelection(selection))
                            throw new InvalidOperationException("The picker drag target changed.");
                        try { RelocateDraggedAssetFolderContent(selection, target); }
                        finally
                        {
                            if (ReferenceEquals(workspace, owner))
                            {
                                before.Browser.RefreshEntries(owner!);
                                before.Browser.RestoreSelection(before.Assets, before.Primary);
                                before.Browser.RestoreNavigationScope(before.Scope);
                                before.Browser.RestoreContentFolderSelection(before.Folders, before.FolderPrimary);
                                before.Browser.RestoreContentSelectionOrder(before.Order);
                                selectedAssetPath = before.Browser.PrimaryEntry?.Path;
                                PropertyGrid.AssetSelectionActive = propertyActive; PropertyGrid.AssetContextScene = propertyScene;
                                PropertyGrid.AssetContextSceneSelection = propertySelection;
                            }
                        }
                        FinishTransfer(completed, true);
                    }
                    catch { FinishTransfer(completed, false); throw; }
                });
            }
        }
        finally { Gui.EndDragDropTarget(); }
        return command;
    }
    private void CancelPickerTransfers(AssetBrowserState browser)
    {
        var owner = pickerCreationScope;
        while (owner != null && !ReferenceEquals(owner.Browser, browser)) owner = owner.Parent;
        if (owner != null)
            while (pickerCreationScope != null)
            {
                var canceled = pickerCreationScope;
                CancelPickerAssetCreation();
                if (ReferenceEquals(canceled, owner)) break;
            }
        if (pickerDragOwner is not { } drag || !ReferenceEquals(drag.Browser, browser)) return;
        if (activeAssetDragSelection?.Token == drag.Token) activeAssetDragSelection = null;
        if (activeAssetFolderDrag?.Token == drag.Token) activeAssetFolderDrag = null;
        pickerDragOwner = null;
    }
}
