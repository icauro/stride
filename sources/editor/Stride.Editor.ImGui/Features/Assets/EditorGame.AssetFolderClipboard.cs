using Stride.Core.Assets;
using Stride.Core.IO;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void PasteClipboardFolders(Package package, string[] paths, bool code)
    {
        if (!IsCurrentEditableAssetPackage(package)) throw new InvalidOperationException("The folder paste package is no longer available.");
        if (!code) { packageFolderHistory.AddFolders(package, paths); return; }
        var project = package.Container as SolutionProject ?? throw new InvalidOperationException("Code folders require an actual project.");
        var document = GetProjectBuildSettingsDocument(project, sessionHistory!);
        document.ValidateProjectFileBaseline();
        var state = authoring!.Find(project.FullPath.ToOSPath()) ?? throw new InvalidOperationException("The project has no authoring owner.");
        var directories = new OwnedSourceDirectoriesOperation(package.RootDirectory.ToOSPath());
        try
        {
            foreach (var path in paths) directories.Ensure(Path.Combine(package.RootDirectory.ToOSPath(), path.Replace('/', Path.DirectorySeparatorChar)));
        }
        catch { directories.CancelInitial(); throw; }
        if (directories.HasDirectories)
        {
            try { sessionHistory!.PushOperation(directories, state); }
            catch { directories.CancelInitial(); throw; }
        }
        foreach (var path in paths) document.ChangeProjectItem("Folder", path.Replace('/', Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, true);
    }
    private AssetItem[] AssetFolderClipboardItems(AssetBrowserFolder folder)
    {
        if (workspace == null) return [];
        var prefix = folder.RelativePath.Length == 0 ? "" : folder.RelativePath.TrimEnd('/') + "/";
        return folder.Package.Assets.Where(item => (item.Asset is IProjectAsset && item.Package.Container is SolutionProject) == folder.IsCode
            && item.UnqualifiedUrl.ToString().StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray();
    }
    private bool CanCopyAssetFolder(AssetBrowserFolder folder)
    {
        var items = AssetFolderClipboardItems(folder);
        return authoring != null && (items.Length != 0 || folder.RelativePath.Length != 0) && items.All(CanCopyAssetItem);
    }
    private void CopyAssetFolder(AssetBrowserFolder folder)
    {
        if (!CanCopyAssetFolder(folder)) return;
        var text = SerializeAssetFolderClipboard(folder);
        if (text.Length != 0) Gui.SetClipboardText(text);
    }
    private string SerializeAssetFolderClipboard(AssetBrowserFolder folder)
    {
        documents.CommitAll();
        var items = AssetFolderClipboardItems(folder);
        var parent = folder.Parent?.RelativePath ?? "";
        var prefix = parent.Length == 0 ? "" : parent.TrimEnd('/') + "/";
        var locations = items.ToDictionary(item => item.Id, item => new UFile(item.UnqualifiedUrl.ToString()[prefix.Length..]));
        var text = SerializeAssetClipboard(items, locations);
        var paths = new List<string>();
        void Collect(AssetBrowserFolder current)
        {
            if (current.IsCode != folder.IsCode) return;
            if (current.RelativePath.Length > prefix.Length) paths.Add(current.RelativePath[prefix.Length..]);
            foreach (var child in current.Children) Collect(child);
        }
        Collect(folder);
        if (paths.Count != 0) text = AssetFolderClipboardMetadata.Attach(text, folder.IsCode, paths);
        return text;
    }
    private Action? DrawAssetFolderClipboardActions(AssetBrowserFolder folder)
    {
        Action? action = null;
        if (DrawNativeMenuItem(EditorLocalization.Widget("Copy", "Menu"), "Ctrl+C", false, CanCopyAssetLocations(folder))) action = () => CopyAssetLocations(folder);
        if (DrawNativeMenuItem(EditorLocalization.Widget("Cut", "Menu"), "Ctrl+X", false, CanCutAssetLocations(folder))) action = () => CutAssetLocations(folder);
        if (DrawNativeMenuItem(EditorLocalization.Widget("Paste", "Menu"), "Ctrl+V", false, CanPasteAssetLocation(folder))) action = () => PasteAssetLocation(folder);
        if (DrawNativeMenuItem(EditorLocalization.Widget("Show in Explorer", "Menu"))) action = () => ExploreSelectedAssetLocation(folder);
        return action;
    }
}
