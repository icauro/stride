using Stride.Core.Assets;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void RequestMediaFactorySources(string[] files, Package package, string folder, AssetCreationPanel.Choice choice)
    {
        if (!CanAddAsset || IsAssetImportUpdating || package.IsReadOnly || !workspace!.Session.Packages.Contains(package))
            throw new InvalidOperationException("The media creation target is no longer available.");
        documents.CommitAll();
        var sources = files.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var preset = choice.Factory.New();
        assetImportWorkspace = workspace; assetImportPackage = package; assetImportFolder = folder;
        requestedAssetImportOptions = null; requestedAssetImportFileOptions = null; Volatile.Write(ref assetImportProgress, null);
        var cancellation = new CancellationTokenSource(); assetImportCancellation = cancellation;
        assetImportTask = Task.Run(() => AssetSourceImport.PrepareMediaFactory(sources, preset, cancellation.Token), cancellation.Token);
        BeginAssetImportProgress(false);
    }
}
