using Stride.Core.Assets;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private sealed partial class ProjectLocationRemoval
    {
        internal EmptyWorkspaceTransition? EmptyTransition;
        internal bool EmptyReady;
        internal AssetBrowserFolder[] MixedFolders = [];
        internal bool MixedReady, MixedPrepared, MixedClearReferences, MixedFinished;
        internal AssetId[] MixedAssetIds = [];
        internal HashSet<string> ExcludedCompilePaths = new(StringComparer.OrdinalIgnoreCase);
    }

    private void BeginMixedProjectFolderDeletion(ProjectLocationRemoval run)
    {
        var selection = new AssetBrowserState(); selection.Bind(run.Workspace);
        foreach (var group in run.MixedFolders.Where(folder => folder.IsCode).GroupBy(folder => folder.Package))
            selection.AddProjectFolders(group.Key, group.Select(folder => folder.RelativePath));
        selection.RestoreContentFolderSelection(run.MixedFolders.Select(folder => folder.Id), run.MixedFolders[^1].Id);
        if (selection.SelectedContentFolders.Count != run.MixedFolders.Length)
            throw new InvalidOperationException("The selected Explorer folders are no longer available for deletion.");
        RequestAssetDeletion(selection, () => ReferenceEquals(projectLocationRemoval, run) && CurrentProjectLocationRemoval(run),
            succeeded => { if (!succeeded && ReferenceEquals(projectLocationRemoval, run)) { run.MixedFinished = true; ClearProjectLocationRemoval(); } },
            clearReferences =>
            {
                if (!ReferenceEquals(projectLocationRemoval, run) || !CurrentProjectLocationRemoval(run))
                    throw new InvalidOperationException("The selected Explorer locations changed before deletion.");
                // Only retain the actual prepared native deletion/choice state here.
                // Root confirmation and work begin after the popup has unwound.
                run.MixedPrepared = run.MixedReady = true; run.MixedClearReferences = clearReferences;
                run.MixedAssetIds = deletingAssets.Select(asset => asset.Id).ToArray();
                foreach (var asset in deletingAssets)
                {
                    if (typeof(SourceCodeAsset).IsAssignableFrom(asset.AssetType)) run.ExcludedCompilePaths.Add(Path.GetFullPath(asset.Path));
                    else if (CanCutVisualScript(asset))
                        run.ExcludedCompilePaths.Add(Path.GetFullPath(run.Workspace.FindAsset(asset.Id).GetGeneratedAbsolutePath().ToOSPath()));
                }
                return true;
            });
    }

    private bool CurrentMixedProjectFolderDeletion(ProjectLocationRemoval run)
    {
        if (!run.MixedPrepared) return true;
        if (!deletingAssets.Select(asset => asset.Id).SequenceEqual(run.MixedAssetIds)) return false;
        foreach (var folder in deletingAssetFolders)
            if (!deletingFolderContents.TryGetValue(folder.Id, out var expected)
                || !AssetFolderClipboardItems(folder).Select(item => item.Id).OrderBy(id => id.ToString(), StringComparer.Ordinal).SequenceEqual(expected)) return false;
        foreach (var (source, version) in deletingSourceVersions)
            if (source.Version != version || !ReferenceEquals(authoring?.Find(source.Path)?.Document, source)) return false;
        if (pendingAssetDeletion is { } plan && (plan.Deleted.Any(target => target.Document.EditRevision != target.Revision)
            || plan.References.Any(reference => reference.Document.EditRevision != reference.Edit.BeforeRevision))) return false;
        return true;
    }
}
