using Stride.UI.Assets;
using Stride.Core;
using Stride.Core.Assets;
using Stride.Core.IO;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void PublishUISelectionAsset(UIAssetExport export, UIDocument source)
    {
        var currentWorkspace = workspace; var history = sessionHistory;
        var owner = authoring?.Find(source.Asset.Id);
        var providers = history?.Extensions; var providerEpoch = providers?.Version;
        bool Current() => CanStartAssetEdit && source.CanSave && history != null
            && ReferenceEquals(workspace, currentWorkspace) && ReferenceEquals(sessionHistory, history)
            && ReferenceEquals(source.SessionHistory, history) && owner is { IsPublished: true, IsDisposed: false }
            && ReferenceEquals(authoring?.Find(source.Asset.Id), owner) && ReferenceEquals(owner.Document, source)
            && ReferenceEquals(history.Extensions, providers) && history.Extensions?.Version == providerEpoch && export.MatchesSource(source);
        if (!Current()) throw new InvalidOperationException("The UI export source is no longer current.");
        ValidatePendingPropertyStrings(); documents.CommitAll();
        if (!Current()) throw new InvalidOperationException("The UI export source changed while finishing pending edits.");
        var sourceItem = currentWorkspace!.Session.FindAsset(source.Asset.Id)
            ?? throw new InvalidOperationException("The UI export source asset is unavailable.");
        if (!SamePath(sourceItem.FullPath.ToOSPath(), source.Path) || !IsCurrentEditableAssetPackage(sourceItem.Package))
            throw new InvalidOperationException("The UI export source location is no longer writable.");
        var package = sourceItem.Package;
        var asset = export.Asset;
        var defaultName = asset is UILibraryAsset ? "UILibrary" : "UIPage";
        var baseName = Stride.Utilities.BuildValidFileName(asset.Hierarchy.RootParts.FirstOrDefault()?.Name ?? defaultName);
        var sourceUrl = sourceItem.UnqualifiedUrl.ToString().Replace('\\', '/');
        var separator = sourceUrl.LastIndexOf('/');
        var directory = separator < 0 ? "" : sourceUrl[..(separator + 1)];
        UFile Qualified(string relative) => package.Container?.Qualify(new UFile(relative)) ?? new UFile(relative);
        string AssetPath(string relative)
        {
            // Resolve native source-folder/namespace/extension semantics without moving or saving a file.
            var candidate = sourceItem.Clone(true, Qualified(relative), asset); candidate.AlternativePath = null;
            return candidate.FullPath.ToOSPath();
        }
        var name = NamingHelper.ComputeNewName(baseName, leaf =>
        {
            var url = directory + leaf;
            var path = AssetPath(url);
            return package.Assets.Any(item => StringComparer.OrdinalIgnoreCase.Equals(item.UnqualifiedUrl.ToString(), url))
                || File.Exists(path) || Directory.Exists(path) || authoring!.Find(path) != null;
        });
        var relative = directory + name;
        var item = new AssetItem(Qualified(relative), asset) { SourceFolder = sourceItem.SourceFolder };
        var outputPath = AssetPath(relative);
        var before = CaptureCreationSelection();
        var uiSelection = ownedUIEditor?.CaptureNativeSelection(source) ?? [];
        EditorOperationCapture? capture = null;
        try
        {
            capture = history!.CaptureOperationsAtomic(() =>
            {
                if (!Current()) throw new InvalidOperationException("The UI export source changed before publication.");
                CreatePreparedAsset(package, item, outputPath, "Create " + defaultName);
                if (!Current()) throw new InvalidOperationException("The UI export source changed during publication.");
                if (asset is UILibraryAsset)
                {
                    if (authoring!.Find(outputPath) is not { IsPublished: true, Document: UIDocument library }
                        || library.Asset.Id != asset.Id) throw new InvalidOperationException("The created UI library has no canonical owner.");
                    source.RelinkExportedLibrary(export, library, item.Location.ToString());
                }
            }, restore => { restore(); history.UiLibraryGraphs.RefreshPublished(); });
            capture.Publish(history, "Create " + name + " from selection");
        }
        catch (Exception failure)
        {
            if (capture?.IsPublished != true)
            {
                try
                {
                    using var restoreScopes = nativeSelectionScopes.Suppress();
                    RestoreCreationSelection(before);
                    if (ReferenceEquals(authoring?.Find(source.Asset.Id)?.Document, source))
                        ownedUIEditor?.RestoreNativeSelection(source, uiSelection);
                }
                catch (Exception recovery) { throw new AggregateException("UI export failed and its selection could not be restored.", failure, recovery); }
            }
            throw;
        }
        finally { capture?.Dispose(); }
    }
}
