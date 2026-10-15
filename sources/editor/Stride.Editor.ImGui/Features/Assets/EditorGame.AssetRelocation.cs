using Hexa.NET.ImGui;
using Stride.Core.Assets;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private const string AssetDragType = "STRIDE_ASSET_LOCATION";
    private struct AssetLocationPayload { public Guid Workspace; public AssetId Asset; public Guid Selection; }
    private sealed record AssetDragCertificate(AssetId Id, Package Package, string Url, string Path, bool Code);
    private sealed record AssetDragSelection(Guid Token, Guid Workspace, AssetId Primary, long Version, AssetId[] Ids, AssetDragCertificate[] Items, bool References = false);
    private AssetDragSelection? activeAssetDragSelection;
    private Guid assetDragIdentity = Guid.NewGuid();
    private AssetRelocationHistory assetRelocationHistory = new();
    private bool assetRelocationCommandContext;
    private AssetId? renamingAsset;
    private string assetRenameDraft = "";
    private bool focusAssetRename;
    private Action? pendingAssetRelocation;
    private bool CanRelocateAsset(AssetEntry asset)
    {
        if (!CanStartAssetEdit || workspace!.AssetItems.FirstOrDefault(candidate => candidate.Id == asset.Id && !candidate.IsDeleted) is not { } item
            || !SamePath(item.FullPath.ToOSPath(), asset.Path)) return false;
        return IsCurrentEditableAssetPackage(item.Package) && (item.Asset is not IProjectAsset || (item.Asset is SourceCodeAsset || item.Asset is Stride.Assets.Scripts.VisualScriptAsset) && item.Package.Container is SolutionProject) && item.AlternativePath == null;
    }
    private void RequestAssetRename(AssetEntry asset)
    {
        if (!CanRelocateAsset(asset)) return;
        renamingAsset = asset.Id; assetRenameDraft = Path.GetFileName(asset.Name); focusAssetRename = true;
    }
    private bool DrawAssetRenameField(AssetEntry asset, float width = -1)
    {
        if (renamingAsset != asset.Id) return false;
        if (focusAssetRename) { Gui.SetKeyboardFocusHere(); focusAssetRename = false; }
        Gui.SetNextItemWidth(width);
        var commit = NativeStringInput.Draw("##RenameAsset", ref assetRenameDraft,
            ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.AutoSelectAll);
        if (Controls.Enabled) Controls.Record("asset/rename/" + asset.Id);
        var cancel = Gui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows) && Gui.IsKeyPressed(ImGuiKey.Escape);
        commit |= Gui.IsItemDeactivatedAfterEdit();
        if (cancel) { renamingAsset = null; assetRenameDraft = ""; }
        else if (commit)
        {
            var name = assetRenameDraft;
            var slash = asset.Name.LastIndexOf('/');
            var url = slash >= 0 ? asset.Name[..(slash + 1)] + name : name;
            renamingAsset = null;
            pendingAssetRelocation = () => RelocateAsset(asset, url);
        }
        return true;
    }
    private unsafe void DrawAssetLocationDragSource(AssetEntry asset)
    {
        if (DrawMixedAssetContentDragSource(asset)) return;
        if (workspace == null || !Gui.BeginDragDropSource()) return;
        if (!workspace.AssetItems.Any(item => item.Id == asset.Id && !item.IsDeleted)) { Gui.EndDragDropSource(); return; }
        if (activeAssetDragSelection is not { } selection || selection.Workspace != assetDragIdentity
            || IsNewAssetDragGesture(AssetDragType) || selection.References || selection.Primary != asset.Id || selection.Version != assetBrowser.SelectionVersion)
        {
            var ids = assetBrowser.IsSelected(asset.Id) ? assetBrowser.Visible.Where(entry => assetBrowser.IsSelected(entry.Id)).Select(entry => entry.Id).ToArray() : [asset.Id];
            activeAssetDragSelection = CaptureAssetDragSelection(asset.Id, assetBrowser.SelectionVersion, ids, false);
        }
        PublishAssetDragSelection(activeAssetDragSelection);
        Gui.EndDragDropSource();
    }
    private unsafe void DrawReferenceAssetDragSource(AssetEntry asset)
    {
        if (workspace == null || !Gui.BeginDragDropSource()) return;
        if (!workspace.AssetItems.Any(item => item.Id == asset.Id && !item.IsDeleted)) { Gui.EndDragDropSource(); return; }
        var ids = assetReferences.IsSelected(asset.Id)
            ? assetReferences.SelectedEntries.Select(entry => entry.Id).ToArray() : [asset.Id];
        if (activeAssetDragSelection is not { } selection || selection.Workspace != assetDragIdentity
            || IsNewAssetDragGesture(AssetDragType) || !selection.References || selection.Primary != asset.Id || !selection.Ids.SequenceEqual(ids))
            activeAssetDragSelection = CaptureAssetDragSelection(asset.Id, 0, ids, true);
        PublishAssetDragSelection(activeAssetDragSelection);
        Gui.EndDragDropSource();
    }
    private unsafe void PublishAssetDragSelection(AssetDragSelection selection)
    {
        var items = workspace!.AssetItems.Where(item => !item.IsDeleted).ToDictionary(item => item.Id);
        if (selection.Ids.Length == 0 || selection.Ids.Any(id => !items.ContainsKey(id))) return;
        var payload = new AssetLocationPayload { Workspace = selection.Workspace, Asset = selection.Primary, Selection = selection.Token };
        Gui.SetDragDropPayload(AssetDragType, &payload, (nuint)sizeof(AssetLocationPayload));
        foreach (var id in selection.Ids)
            Gui.TextUnformatted(items[id].UnqualifiedUrl.ToString());
    }
    private AssetDragSelection CaptureAssetDragSelection(AssetId primary, long version, AssetId[] ids, bool references)
    {
        return new(Guid.NewGuid(), assetDragIdentity, primary, version, ids, CaptureAssetDragCertificates(ids), references);
    }
    private AssetDragCertificate[] CaptureAssetDragCertificates(IEnumerable<AssetId> ids) => ids.Distinct().Select(id => workspace!.FindAsset(id))
        .Select(item => new AssetDragCertificate(item.Id, item.Package, item.UnqualifiedUrl.FullPath, item.FullPath.ToOSPath(), item.Asset is IProjectAsset && item.Package.Container is SolutionProject)).ToArray();
    private static bool IsNewAssetDragGesture(string type)
    {
        var payload = Gui.GetDragDropPayload();
        return payload.IsNull || !payload.IsDataType(type);
    }
    private bool IsCurrentAssetDragSelection(AssetDragSelection selection)
    {
        return selection.Workspace == assetDragIdentity && IsCurrentPickerDragOwner(selection.Token) && selection.Items.Length == selection.Ids.Length && IsCurrentAssetDragCertificates(selection.Items);
    }
    private bool IsCurrentAssetDragCertificates(AssetDragCertificate[] certificates)
    {
        if (workspace == null) return false;
        var items = workspace.AssetItems.Where(item => !item.IsDeleted).ToDictionary(item => item.Id);
        return certificates.All(before => items.TryGetValue(before.Id, out var item)
            && ReferenceEquals(before.Package, item.Package) && before.Url == item.UnqualifiedUrl.FullPath && SamePath(before.Path, item.FullPath.ToOSPath())
            && before.Code == (item.Asset is IProjectAsset && item.Package.Container is SolutionProject));
    }
    private bool TryGetAssetDragIds(AssetLocationPayload payload, out AssetId[] ids)
    {
        ids = [];
        if (payload.Workspace != assetDragIdentity || workspace == null) return false;
        if (payload.Selection == Guid.Empty)
        {
            if (!workspace.AssetItems.Any(item => item.Id == payload.Asset && !item.IsDeleted)) return false;
            ids = [payload.Asset]; return true;
        }
        if (activeAssetDragSelection is not { } selection || selection.Token != payload.Selection
            || selection.Workspace != payload.Workspace || selection.Primary != payload.Asset) return false;
        if (!IsCurrentAssetDragSelection(selection)) return false;
        ids = selection.Ids; return ids.Length > 0;
    }
    private unsafe void DrawAssetFolderDropTarget(AssetBrowserFolder folder)
    {
        if (!CanStartAssetEdit || !IsEditableAssetPackage(folder.Package) || !Gui.BeginDragDropTarget()) return;
        var payload = Gui.AcceptDragDropPayload(AssetDragType, ImGuiDragDropFlags.AcceptBeforeDelivery | ImGuiDragDropFlags.AcceptNoDrawDefaultRect);
        if (!payload.IsNull && payload.DataSize == sizeof(AssetLocationPayload))
        {
            var data = *(AssetLocationPayload*)payload.Data;
            if (TryGetAssetDragIds(data, out var draggedAssets) && activeAssetDragSelection is { } source
                && CanRelocateDraggedAssets(draggedAssets, folder))
            {
                Gui.GetWindowDrawList().AddRect(Gui.GetItemRectMin(), Gui.GetItemRectMax(), Gui.GetColorU32(ImGuiCol.DragDropTarget), 0, ImDrawFlags.None, 2);
                if (payload.IsDelivery())
                {
                    var selection = new AssetFolderDragSelection(source.Workspace, source.Token, "asset/" + source.Primary, [], source.Ids.ToArray()) { Items = source.Items };
                    pendingAssetRelocation = () =>
                    {
                        if (!IsCurrentAssetDragSelection(source)) throw new InvalidOperationException("The dragged asset selection is no longer current.");
                        RelocateDraggedAssetFolderContent(selection, folder);
                    };
                }
            }
        }
        AcceptAssetFolderDrop(folder);
        Gui.EndDragDropTarget();
    }
    private void ApplyPendingAssetRelocation()
    {
        if (pendingAssetRelocation is not { } action) return;
        pendingAssetRelocation = null; Execute(action);
    }
    private void RelocateAsset(AssetEntry entry, string url, Package? destinationPackage = null)
    {
        if (!CanRelocateAsset(entry) || destinationPackage != null && !IsCurrentEditableAssetPackage(destinationPackage))
            throw new InvalidOperationException("The selected asset is no longer available for editing.");
        if (workspace!.FindAsset(entry.Id).Asset is Stride.Assets.Scripts.VisualScriptAsset)
        { RelocateVisualScriptAsset(entry, url, destinationPackage); return; }
        if (workspace!.FindAsset(entry.Id).Asset is SourceCodeAsset)
        { RelocateSourceAsset(entry, url, destinationPackage); return; }
        var original = workspace.FindAsset(entry.Id);
        var targetPackage = destinationPackage ?? original.Package;
        url = url.Replace('\\', '/');
        AssetRelocationHistory.ValidateUrl(url);
        var location = targetPackage.Container?.Qualify(new Stride.Core.IO.UFile(url)) ?? new Stride.Core.IO.UFile(url);
        if (ReferenceEquals(original.Package, targetPackage) && string.Equals(location.FullPath, original.Location.FullPath, StringComparison.Ordinal)) return;
        if (targetPackage.Assets.Any(item => item.Id != original.Id && string.Equals(item.UnqualifiedUrl.FullPath, url, StringComparison.OrdinalIgnoreCase)
            || !ReferenceEquals(original.Package, targetPackage) && item.Id == original.Id))
            throw new InvalidOperationException("An asset with the same name exists in the same directory.");
        ValidatePendingPropertyStrings();
        var retained = documents.AuthoredDocuments.OfType<IAssetReferenceDocument>()
            .ToDictionary(document => document.AuthoredAsset.Id);
        if (assetRelocationHistory.Relocate(workspace!, entry, url, retained,
            asset => (IAssetReferenceDocument)GetAssetTagDocument(asset, false), RelocateAssetDocumentPath, destinationPackage))
        {
            assetRelocationCommandContext = true; assetTagBatchCommandContext = packageFolderCommandContext = false;
            assetDeletionCommandContext = false;
            RefreshRelocatedAssets(entry.Id);
        }
    }
    private void RefreshRelocatedAssets(AssetId? primary = null)
    {
        var ids = assetBrowser.CaptureSelection(); var selected = primary ?? assetBrowser.Primary;
        var contentFolders = assetBrowser.CaptureContentFolderSelection(); var folderPrimary = assetBrowser.PrimaryContentFolder;
        var scope = assetBrowser.CaptureNavigationScope();
        assetBrowser.RefreshEntries(workspace!); assetBrowser.RestoreSelection(ids, selected);
        assetBrowser.RestoreNavigationScope(scope); assetBrowser.RestoreContentFolderSelection(contentFolders, folderPrimary);
        SyncAuthoredAssetTags();
        assetReferences.Clear();
    }
    private void ClearAssetRelocationState()
    {
        assetRelocationHistory.Clear(); assetRelocationCommandContext = false;
        renamingAsset = null; assetRenameDraft = ""; pendingAssetRelocation = null; assetDragIdentity = Guid.NewGuid();
        activeAssetDragSelection = null; activeAssetFolderDrag = null; pickerDragOwner = null; assetFolderRename.Clear();
    }
}
