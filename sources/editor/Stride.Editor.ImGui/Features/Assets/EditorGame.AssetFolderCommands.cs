using Hexa.NET.ImGui;
using Stride.Core.Assets;
using Stride.Core.IO;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private const string AssetFolderDragType = "STRIDE_ASSET_FOLDER_LOCATION";
    private readonly AssetFolderRenameState assetFolderRename = new();
    private struct AssetFolderPayload { public Guid Workspace; public Guid Selection; }
    private sealed record AssetFolderDragCertificate(string Id, Package Package, string Path, bool Code, string[] Descendants);
    private sealed record AssetFolderDragSelection(Guid Workspace, Guid Token, string Primary, string[] Folders, AssetId[] Assets)
    {
        internal long Version { get; init; }
        internal AssetDragCertificate[] Items { get; init; } = [];
        internal AssetFolderDragCertificate[] Locations { get; init; } = [];
    }
    private AssetFolderDragSelection? activeAssetFolderDrag;
    private Func<bool>? assetFolderRenameCurrent;
    private void ClearAssetFolderRename() { assetFolderRename.Clear(); assetFolderRenameCurrent = null; }
    private void RequestAssetFolderRename(AssetBrowserFolder folder, Func<bool>? isCurrent = null)
    {
        if (!CanRelocateAssetFolder(folder) || isCurrent?.Invoke() == false) return;
        assetFolderRename.Begin(folder); assetFolderRenameCurrent = isCurrent;
    }
    private bool DrawAssetFolderRenameField(AssetBrowserFolder folder, float width = -1)
    {
        if (assetFolderRename.Id != folder.Id) return false;
        if (assetFolderRenameCurrent?.Invoke() == false) { ClearAssetFolderRename(); return false; }
        if (assetFolderRename.Focus) { Gui.SetKeyboardFocusHere(); assetFolderRename.Focus = false; }
        Gui.SetNextItemWidth(width);
        var commit = NativeStringInput.Draw("##RenameFolder", ref assetFolderRename.Text, ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.AutoSelectAll);
        if (Gui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows) && Gui.IsKeyPressed(ImGuiKey.Escape)) ClearAssetFolderRename();
        else if (commit || Gui.IsItemDeactivatedAfterEdit())
        {
            var name = assetFolderRename.Text; var isCurrent = assetFolderRenameCurrent; ClearAssetFolderRename();
            var parent = folder.Parent?.RelativePath ?? "";
            pendingAssetRelocation = () => { if (isCurrent?.Invoke() == false) return; if (!IsValidAssetLeafName(name)) throw new ArgumentException("Invalid folder name."); RelocateAssetFolder(folder, parent.Length == 0 ? name : parent + "/" + name); };
        }
        return true;
    }
    private unsafe void DrawAssetFolderDragSource(AssetBrowserFolder folder)
    {
        if (!CanRelocateAssetFolder(folder) || !Gui.BeginDragDropSource()) return;
        if (activeAssetFolderDrag is not { } current || current.Workspace != assetDragIdentity || current.Primary != folder.Id
            || current.Version != assetBrowser.SelectionVersion || IsNewAssetDragGesture(AssetFolderDragType))
        {
            var selected = assetBrowser.SelectedContentFolders.Any(candidate => candidate.Id == folder.Id);
            var scoped = assetBrowser.IsFolderSelected(folder) ? assetBrowser.CaptureNavigationScope().Folders.Select(assetBrowser.FindFolder).OfType<AssetBrowserFolder>().ToArray() : [];
            var scopeRoots = scoped.Where(candidate => !scoped.Any(parent => parent.Id != candidate.Id && candidate.Id.StartsWith(parent.Id + "/", StringComparison.OrdinalIgnoreCase))).ToArray();
            var folders = selected ? SelectedAssetContentRoots().Select(candidate => candidate.Id).ToArray() : scopeRoots.Length > 0 ? scopeRoots.Select(candidate => candidate.Id).ToArray() : [folder.Id];
            AssetId[] assets = selected ? assetBrowser.CaptureSelection() : [];
            activeAssetFolderDrag = CaptureAssetFolderDragSelection(folder.Id, folders, assets);
        }
        var selection = activeAssetFolderDrag;
        var payload = new AssetFolderPayload { Workspace = selection.Workspace, Selection = selection.Token };
        Gui.SetDragDropPayload(AssetFolderDragType, &payload, (nuint)sizeof(AssetFolderPayload));
        foreach (var id in selection.Folders) if (assetBrowser.FindFolder(id) is { } item) Gui.TextUnformatted(item.Name);
        Gui.EndDragDropSource();
    }
    private unsafe bool DrawMixedAssetContentDragSource(AssetEntry asset)
    {
        if (!assetBrowser.IsSelected(asset.Id) || assetBrowser.SelectedContentFolders.Count == 0) return false;
        if (!CanRelocateAsset(asset) || !SelectedAssetContentRoots().All(CanRelocateAssetFolder) || !Gui.BeginDragDropSource()) return true;
        var primary = "asset/" + asset.UiId;
        if (activeAssetFolderDrag is not { } current || current.Workspace != assetDragIdentity || current.Primary != primary
            || current.Version != assetBrowser.SelectionVersion || IsNewAssetDragGesture(AssetFolderDragType))
            activeAssetFolderDrag = CaptureAssetFolderDragSelection(primary, SelectedAssetContentRoots().Select(folder => folder.Id).ToArray(), assetBrowser.CaptureSelection());
        var selection = activeAssetFolderDrag;
        var payload = new AssetFolderPayload { Workspace = selection.Workspace, Selection = selection.Token };
        Gui.SetDragDropPayload(AssetFolderDragType, &payload, (nuint)sizeof(AssetFolderPayload));
        foreach (var id in selection.Folders) if (assetBrowser.FindFolder(id) is { } folder) Gui.TextUnformatted(folder.Name);
        foreach (var id in selection.Assets) if (workspace!.Assets.FirstOrDefault(entry => entry.Id == id) is { } entry) Gui.TextUnformatted(entry.Name);
        Gui.EndDragDropSource(); return true;
    }
    private unsafe void AcceptAssetFolderDrop(AssetBrowserFolder target)
    {
        var payload = Gui.AcceptDragDropPayload(AssetFolderDragType);
        if (payload.IsNull || payload.DataSize != sizeof(AssetFolderPayload) || !payload.IsDelivery()) return;
        var data = *(AssetFolderPayload*)payload.Data;
        if (activeAssetFolderDrag is not { } selection || data.Workspace != assetDragIdentity || selection.Workspace != data.Workspace || selection.Token != data.Selection
            || !IsCurrentAssetFolderDragSelection(selection)) return;
        pendingAssetRelocation = () => RelocateDraggedAssetFolderContent(selection, target);
    }
    private AssetFolderDragSelection CaptureAssetFolderDragSelection(string primary, string[] folders, AssetId[] assets, AssetBrowserState? browser = null)
    {
        var locations = folders.Select(id => assetBrowser.FindFolder(id) ?? throw new InvalidOperationException("The dragged folder no longer exists.")).ToArray();
        return new(assetDragIdentity, Guid.NewGuid(), primary, folders, assets)
        {
            Version = (browser ?? assetBrowser).SelectionVersion,
            Items = CaptureAssetDragCertificates(assets.Concat(locations.SelectMany(AssetFolderClipboardItems).Select(item => item.Id))),
            Locations = locations.Select(folder => new AssetFolderDragCertificate(folder.Id, folder.Package, folder.RelativePath, folder.IsCode,
                FolderRelativePaths(folder).Order(StringComparer.OrdinalIgnoreCase).ToArray())).ToArray(),
        };
    }
    private bool IsCurrentAssetFolderDragSelection(AssetFolderDragSelection selection) => selection.Workspace == assetDragIdentity
        && IsCurrentPickerDragOwner(selection.Token)
        && selection.Locations.Length == selection.Folders.Length && IsCurrentAssetDragCertificates(selection.Items)
        && selection.Locations.All(before => assetBrowser.FindFolder(before.Id) is { } folder && ReferenceEquals(before.Package, folder.Package)
            && before.Path == folder.RelativePath && before.Code == folder.IsCode
            && before.Descendants.SequenceEqual(FolderRelativePaths(folder).Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase)
            && AssetFolderClipboardItems(folder).Select(item => item.Id).ToHashSet().SetEquals(selection.Items
                .Where(item => ReferenceEquals(item.Package, before.Package) && item.Code == before.Code
                    && item.Url.StartsWith(before.Path.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase))
                .Select(item => item.Id)));
    private bool CanRelocateDraggedAssets(AssetId[] ids, AssetBrowserFolder target)
    {
        if (!CanStartAssetEdit || !IsEditableAssetPackage(target.Package) || !ReferenceEquals(target.Package.Session, workspace!.Session)
            || assetBrowser.FindFolder(target.Id) is not { } currentTarget || !ReferenceEquals(currentTarget.Package, target.Package)
            || currentTarget.RelativePath != target.RelativePath || currentTarget.IsCode != target.IsCode
            || target.IsCode && target.Package.Container is not SolutionProject) return false;
        var entries = workspace.Assets.ToDictionary(entry => entry.Id);
        var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in ids)
        {
            if (!entries.TryGetValue(id, out var entry) || !CanRelocateAsset(entry)) return false;
            var item = workspace.FindAsset(id);
            if ((item.Asset is IProjectAsset) != target.IsCode) return false;
            var url = target.RelativePath.Length == 0 ? Path.GetFileName(entry.Name) : target.RelativePath + "/" + Path.GetFileName(entry.Name);
            if (!destinations.Add(url) || target.Package.Assets.Any(existing =>
                existing.Id != id && existing.UnqualifiedUrl.FullPath.Equals(url, StringComparison.OrdinalIgnoreCase)
                || !ReferenceEquals(item.Package, target.Package) && existing.Id == id)) return false;
            var location = target.Package.Container?.Qualify(new UFile(url)) ?? new UFile(url);
            var moved = ReferenceEquals(item.Package, target.Package) ? item.Clone(true, newLocation: location, newAsset: item.Asset) : item.CloneForPackage(target.Package, location, item.Asset);
            var path = moved.FullPath.ToOSPath();
            if (!SamePath(path, item.FullPath.ToOSPath()) && (File.Exists(path) || Directory.Exists(path))) return false;
        }
        return ids.Length > 0;
    }
    private void RelocateDraggedAssetFolderContent(AssetFolderDragSelection selection, AssetBrowserFolder target)
    {
        if (!IsCurrentAssetFolderDragSelection(selection) || !CanStartAssetEdit || !IsEditableAssetPackage(target.Package)
            || !ReferenceEquals(target.Package.Session, workspace!.Session)
            || assetBrowser.FindFolder(target.Id) is not { } currentTarget || !ReferenceEquals(currentTarget.Package, target.Package)
            || currentTarget.RelativePath != target.RelativePath || currentTarget.IsCode != target.IsCode)
            throw new InvalidOperationException("The target folder is no longer current.");
        var folders = selection.Folders.Select(id => assetBrowser.FindFolder(id) ?? throw new InvalidOperationException("The dragged folder no longer exists.")).ToArray();
        if (folders.Any(folder => folder.IsCode != target.IsCode || !CanRelocateAssetFolder(folder)))
            throw new InvalidOperationException("The folder cannot be moved to this location.");
        var contained = folders.SelectMany(AssetFolderClipboardItems).Select(item => item.Id).ToHashSet();
        var assets = selection.Assets.Where(id => !contained.Contains(id)).Select(id => workspace!.Assets.Single(entry => entry.Id == id)).ToArray();
        if (assets.Any(entry => !CanRelocateAsset(entry) || (workspace.FindAsset(entry.Id).Asset is IProjectAsset) != target.IsCode)) throw new InvalidOperationException("The asset cannot be moved to this location.");
        if (assets.Length > 0 && !CanRelocateDraggedAssets(assets.Select(entry => entry.Id).ToArray(), target))
            throw new InvalidOperationException("The target folder cannot accept this asset selection.");
        assets = assets.Where(entry => !ReferenceEquals(workspace.FindAsset(entry.Id).Package, target.Package)
            || workspace.FindAsset(entry.Id).UnqualifiedUrl.FullPath != (target.RelativePath.Length == 0 ? Path.GetFileName(entry.Name) : target.RelativePath + "/" + Path.GetFileName(entry.Name))).ToArray();
        if (folders.Length == 0 && assets.Length == 0) return;
        using var capture = sessionHistory!.CaptureOperationsAtomic(() =>
        {
            foreach (var folder in folders) RelocateAssetFolder(folder, target.RelativePath.Length == 0 ? folder.Name : target.RelativePath + "/" + folder.Name, target.Package);
            foreach (var asset in assets) RelocateAsset(asset, target.RelativePath.Length == 0 ? Path.GetFileName(asset.Name) : target.RelativePath + "/" + Path.GetFileName(asset.Name), target.Package);
        });
        capture.Publish(sessionHistory, "Move selection");
    }
}
