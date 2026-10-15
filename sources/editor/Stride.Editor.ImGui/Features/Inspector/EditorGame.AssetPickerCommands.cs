using Stride.Core.Assets;

namespace Stride.Editor.ImGui;

internal enum PickerContextAction { Rename, Delete, OpenAsset, OpenText, OpenSource, Explore }
internal sealed record PickerContextCapabilities(bool Rename, bool Delete, bool Asset, bool HasSource, bool Explore);

internal sealed partial class EditorGame
{
    private PickerContextCapabilities PickerCapabilities(AssetBrowserContent content)
    {
        if (workspace == null) return new(false, false, false, false, false);
        if (content.Asset is { } entry)
        {
            var item = workspace.AssetItems.FirstOrDefault(item => item.Id == entry.Id && !item.IsDeleted && SamePath(item.FullPath.ToOSPath(), entry.Path));
            if (item == null) return new(false, false, false, false, false);
            return new(CanStartAssetEdit && CanRelocateAsset(entry), CanStartAssetEdit && CanDeleteAsset(entry), true,
                AssetMainSource(item) != null, true);
        }
        var folder = content.Folder is { } captured ? assetBrowser.FindFolder(captured.Id) : null;
        if (folder == null || !ReferenceEquals(folder.Package, content.Folder!.Package)) return new(false, false, false, false, false);
        return new(CanStartAssetEdit && CanRelocateAssetFolder(folder), CanStartAssetEdit && CanCutAssetFolder(folder), false, false, true);
    }

    private void RunPickerContextAction(AssetBrowserContent content, PickerContextAction action, AssetBrowserState selection, Func<bool> isCurrent, string? name, Action<bool>? completed = null)
    {
        var owner = workspace;
        var package = content.Asset is { } captured ? owner?.AssetItems.FirstOrDefault(item => item.Id == captured.Id)?.Package : content.Folder?.Package;
        var deferred = false; var completedOnce = false;
        void Finish(bool succeeded) { if (completedOnce) return; completedOnce = true; completed?.Invoke(succeeded); }
        if (TryRunPickerSelectionAction(action, selection, isCurrent, Finish)) return;
        Execute(() =>
        {
            try
            {
            if (!ReferenceEquals(workspace, owner) || owner == null || !isCurrent()) throw new InvalidOperationException("The asset picker target is no longer current.");
            if (action == PickerContextAction.Rename && (selection.SelectedContent.Count != 1 || selection.SelectedContent[0].Id != content.Id))
                throw new InvalidOperationException("The asset picker selection changed.");
            if (content.Asset is { } selected)
            {
                if (selection.SelectedContent.Count != 1 || selection.SelectedCount != 1 || selection.Primary != selected.Id) throw new InvalidOperationException("The asset picker selection changed.");
                var item = owner.AssetItems.FirstOrDefault(item => item.Id == selected.Id && !item.IsDeleted);
                if (item == null || !ReferenceEquals(item.Package, package) || !SamePath(item.FullPath.ToOSPath(), selected.Path))
                    throw new InvalidOperationException("The selected asset changed.");
                var capabilities = PickerCapabilities(content);
                switch (action)
                {
                    case PickerContextAction.Rename when capabilities.Rename:
                        if (!IsValidAssetLeafName(name ?? "")) throw new ArgumentException("Invalid asset name.");
                        var slash = selected.Name.LastIndexOf('/');
                        var mainSelection = assetBrowser.CaptureSelection(); var mainPrimary = assetBrowser.Primary;
                        var mainScope = assetBrowser.CaptureNavigationScope(); var mainFolders = assetBrowser.CaptureContentFolderSelection(); var mainFolderPrimary = assetBrowser.PrimaryContentFolder;
                        try { RelocateAsset(selected, (slash < 0 ? "" : selected.Name[..(slash + 1)]) + name); }
                        finally
                        {
                            if (ReferenceEquals(workspace, owner))
                            {
                                assetBrowser.RestoreSelection(mainSelection, mainPrimary);
                                assetBrowser.RestoreNavigationScope(mainScope); assetBrowser.RestoreContentFolderSelection(mainFolders, mainFolderPrimary);
                                selection.RefreshEntries(owner);
                            }
                        }
                        break;
                    case PickerContextAction.Delete when capabilities.Delete:
                        deferred = true; RequestAssetDeletion(selection, isCurrent, Finish); break;
                    case PickerContextAction.OpenAsset: RequestOpenExternalAsset(item); break;
                    case PickerContextAction.OpenText: RequestOpenExternalAsset(item, true); break;
                    case PickerContextAction.OpenSource: OpenAssetSourceExternally(item); break;
                    case PickerContextAction.Explore: ExploreAssetEntry(selected); break;
                    default: throw new InvalidOperationException("This asset cannot be edited.");
                }
                if (!deferred) Finish(true);
                return;
            }
            if (content.Folder is not { } capturedFolder || selection.SelectedContentFolders.Count != 1 || selection.SelectedContentFolders[0].Id != capturedFolder.Id)
                throw new InvalidOperationException("The asset picker folder selection changed.");
            var folder = assetBrowser.FindFolder(capturedFolder.Id);
            if (folder == null || !ReferenceEquals(folder.Package, package)) throw new InvalidOperationException("The selected folder changed.");
            var rules = PickerCapabilities(content);
            switch (action)
            {
                case PickerContextAction.Rename when rules.Rename:
                    if (!IsValidAssetLeafName(name ?? "")) throw new ArgumentException("Invalid folder name.");
                    var parent = folder.Parent?.RelativePath ?? "";
                    RelocateAssetFolder(folder, (parent.Length == 0 ? "" : parent + "/") + name); selection.RefreshEntries(owner); break;
                case PickerContextAction.Delete when rules.Delete:
                    deferred = true; RequestAssetDeletion(selection, isCurrent, Finish); break;
                case PickerContextAction.Explore: ExploreAssetFolder(folder); break;
                default: throw new InvalidOperationException("This folder cannot be edited.");
            }
            if (!deferred) Finish(true);
            }
            catch { Finish(false); throw; }
        });
    }
}
