using Stride.Core.Assets;
using Stride.Core.Diagnostics;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private sealed record PreparedAssetDrop(ProjectWorkspace Workspace, AssetBrowserFolder Folder, string[] Files);
    private PreparedAssetDrop? preparedAssetDrop;
    private bool assetImportProgressRetired = true;
    private Task<string[]>? assetDropSourcesTask;
    private CancellationTokenSource? assetDropSourcesCancellation;
    private ProjectWorkspace? assetDropSourcesWorkspace;
    private AssetBrowserFolder? assetDropSourcesFolder;

    private void RequestDroppedAssetSources(string[] files, AssetBrowserFolder folder)
    {
        if (!CanAddAsset || !IsCurrentEditableAssetPackage(folder.Package)
            || assetBrowser.FindFolder(folder.Id) is not { } current || !ReferenceEquals(current.Package, folder.Package)
            || current.RelativePath != folder.RelativePath || current.IsCode != folder.IsCode)
            throw new InvalidOperationException("The asset import target is not available.");
        var paths = files.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (paths.Length == 0) return;
        if (!paths.Any(Directory.Exists)) { RequestDroppedAssetTemplates(paths, folder); return; }
        var cancellation = new CancellationTokenSource();
        assetDropSourcesWorkspace = workspace; assetDropSourcesFolder = folder;
        assetDropSourcesCancellation = cancellation;
        assetDropSourcesTask = Task.Run(() => AssetDropSources.Expand(paths, cancellation.Token), cancellation.Token);
        BeginAssetImportProgress(false);
    }

    private void PumpAssetDropSources()
    {
        if (preparedAssetDrop is { } ready && assetImportProgressRetired)
        {
            preparedAssetDrop = null;
            try
            {
                if (!ReferenceEquals(workspace, ready.Workspace) || !CanAddAsset || !IsCurrentEditableAssetPackage(ready.Folder.Package)
                    || assetBrowser.FindFolder(ready.Folder.Id) is not { } current || !ReferenceEquals(current.Package, ready.Folder.Package)
                    || current.RelativePath != ready.Folder.RelativePath || current.IsCode != ready.Folder.IsCode)
                    throw new InvalidOperationException("The asset import target is no longer available.");
                if (ready.Files.Length > 0) RequestDroppedAssetTemplates(ready.Files, ready.Folder);
            }
            catch (Exception error)
            {
                BeginAssetImportProgress(false); CompleteAssetImportProgress(null, error, false);
                outputLog.Append("Project", LogMessageType.Error, "Asset import", error.Message, error.ToString());
            }
        }
        if (assetDropSourcesTask is not { IsCompleted: true } task) return;
        var owner = assetDropSourcesWorkspace; var folder = assetDropSourcesFolder;
        var cancellation = assetDropSourcesCancellation;
        assetDropSourcesTask = null; assetDropSourcesCancellation = null;
        assetDropSourcesWorkspace = null; assetDropSourcesFolder = null;
        try
        {
            if (task.IsCanceled || cancellation?.IsCancellationRequested == true)
            { CompleteAssetImportProgress(null, null, true); return; }
            var files = task.GetAwaiter().GetResult();
            CompleteAssetImportProgress(null, null, false);
            if (!ReferenceEquals(workspace, owner) || folder == null || !CanAddAsset
                || !IsCurrentEditableAssetPackage(folder.Package) || assetBrowser.FindFolder(folder.Id) is not { } current
                || !ReferenceEquals(current.Package, folder.Package) || current.RelativePath != folder.RelativePath || current.IsCode != folder.IsCode)
                throw new InvalidOperationException("The asset import target is no longer available.");
            if (files.Length > 0) preparedAssetDrop = new(owner!, folder, files);
        }
        catch (Exception error)
        {
            CompleteAssetImportProgress(null, error, false);
            outputLog.Append("Project", LogMessageType.Error, "Asset import", error.Message, error.ToString());
        }
        finally { cancellation?.Dispose(); }
    }

    private void StopAssetDropSources()
    {
        preparedAssetDrop = null;
        var task = assetDropSourcesTask; var cancellation = assetDropSourcesCancellation;
        assetDropSourcesTask = null; assetDropSourcesCancellation = null;
        assetDropSourcesWorkspace = null; assetDropSourcesFolder = null;
        cancellation?.Cancel();
        if (task == null) { cancellation?.Dispose(); return; }
        Interlocked.Increment(ref detachedAssetImports);
        _ = task.ContinueWith(completed =>
        {
            try { if (completed.IsFaulted) _ = completed.Exception; }
            finally { cancellation?.Dispose(); Interlocked.Decrement(ref detachedAssetImports); }
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }
}
