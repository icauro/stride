using Stride.Core.Assets;
using Stride.Core.Assets.Analysis;
using Stride.Core.IO;
using Stride.Core.Serialization.Contents;
using Stride.Core.Transactions;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void RelocateSourceAsset(AssetEntry entry, string url, Package? destinationPackage = null)
    {
        var before = workspace!.FindAsset(entry.Id); var package = before.Package; var targetPackage = destinationPackage ?? package;
        if (!IsEditableAssetPackage(targetPackage) || !ReferenceEquals(package.Session, targetPackage.Session)) throw new InvalidOperationException("The target package cannot be edited.");
        if (!CanRelocateAsset(entry) || before.Asset is not SourceCodeAsset) throw new InvalidOperationException("The source can't be moved.");
        url = url.Replace('\\', '/');
        if (Path.IsPathRooted(url) || url.Split('/').Any(part => !IsValidAssetLeafName(part))) throw new ArgumentException("Invalid source asset name.");
        var location = targetPackage.Container?.Qualify(new UFile(url)) ?? new UFile(url);
        if (ReferenceEquals(package, targetPackage) && string.Equals(location.FullPath, before.Location.FullPath, StringComparison.Ordinal)) return;
        if (targetPackage.Assets.Any(item => item.Id != before.Id && item.Location == location || !ReferenceEquals(package, targetPackage) && item.Id == before.Id)) throw new IOException("A source asset already exists at that URL.");
        ValidatePendingPropertyStrings();
        var after = ReferenceEquals(package, targetPackage) ? before.Clone(keepPackage: true, newLocation: location, newAsset: before.Asset) : before.CloneForPackage(targetPackage, location, before.Asset);
        var source = GetCanonicalDocument(entry.Path, () => CreateScriptDocument(entry.Path)) as ScriptSourceDocument
            ?? throw new InvalidOperationException("The source has another authored owner.");
        source.CommitEdits();
        if (!source.CanEditText) throw new InvalidOperationException("The source can't be moved.");
        var state = authoring!.Find(before.Id) ?? throw new InvalidOperationException("The source has no canonical authored identity.");
        using var retention = state.AcquireView();
        var project = package.Container as SolutionProject;
        var projectDocument = before.Asset is IProjectAsset ? GetProjectBuildSettingsDocument(project!, sessionHistory!) : null;
        projectDocument?.ValidateProjectFileBaseline();
        var targetProject = targetPackage.Container as SolutionProject;
        var targetProjectDocument = before.Asset is IProjectAsset ? GetProjectBuildSettingsDocument(targetProject ?? throw new InvalidOperationException("The source requires an actual project."), sessionHistory!) : null;
        targetProjectDocument?.ValidateProjectFileBaseline();
        var locations = new Dictionary<AssetId, string> { [before.Id] = location.ToString() };
        var referenceEdits = new List<PreparedAssetReferenceEdit>();
        foreach (var consumerItem in workspace.AssetItems)
        {
            var retained = authoring.Find(consumerItem.Id)?.Document as IAssetReferenceDocument;
            var current = retained?.AuthoredAsset ?? consumerItem.Asset;
            if (!AssetReferenceAnalysis.Visit(current).Any(link => link.Reference is IReference reference && reference.Id == before.Id && reference.Location != location.ToString())) continue;
            if (consumerItem.Package.IsReadOnly) throw new InvalidOperationException("An asset referencing this source cannot be edited.");
            var consumerEntry = workspace.Assets.FirstOrDefault(candidate => candidate.Id == consumerItem.Id)
                ?? throw new InvalidOperationException("An asset referencing this source is unavailable.");
            var consumer = retained ?? GetAssetTagDocument(consumerEntry, false) as IAssetReferenceDocument
                ?? throw new InvalidOperationException("An asset referencing this source has no native authored graph.");
            consumer.CommitEdits();
            if (consumer.PrepareReferenceLocations(locations) is { } edit) referenceEdits.Add(edit);
        }
        var rootPackages = workspace.Session.LocalPackages.Where(candidate => candidate.RootAssets.Any(reference => reference.Id == before.Id)).ToArray();
        if (rootPackages.Any(candidate => candidate.IsReadOnly)) throw new InvalidOperationException("A package referencing this source cannot be edited.");
        var metadata = PackageMetadataRevision.Prepare(sessionHistory, workspace, rootPackages.Append(package).Append(targetPackage));
        var directories = new OwnedSourceDirectoriesOperation(targetPackage.RootDirectory.ToOSPath());
        void Apply()
        {
            try { directories.Ensure(Path.GetDirectoryName(after.FullPath.ToOSPath())!); }
            catch { directories.CancelInitial(); throw; }
            if (directories.HasDirectories)
            {
                try { sessionHistory.PushOperation(directories, state); }
                catch { directories.CancelInitial(); throw; }
            }
            if (projectDocument != null)
            {
                var itemType = projectDocument.ProjectSourceItemType(before);
                targetProjectDocument!.TransferProjectItemFrom(projectDocument, itemType, before.GetProjectInclude(), after.GetProjectInclude());
            }
            foreach (var edit in referenceEdits)
            {
                edit.Apply();
                try { edit.Commit(); } catch { edit.Restore(); throw; }
            }
            var operation = new SourceAssetRelocationOperation(this, before, after, source, metadata, rootPackages);
            operation.ApplyInitial();
            try { sessionHistory.PushOperation(operation, [state, .. metadata.Select(change => change.State)]); }
            catch { operation.CancelInitial(); throw; }
        }
        if (sessionHistory!.IsCapturingOperations) Apply();
        else { using var capture = sessionHistory.CaptureOperationsAtomic(Apply); capture.Publish(sessionHistory, "Rename source asset"); }
        assetRelocationCommandContext = true; assetDeletionCommandContext = assetTagBatchCommandContext = packageFolderCommandContext = false;
        RefreshRelocatedAssets(entry.Id);
    }
    private sealed class SourceAssetRelocationOperation(EditorGame game, AssetItem before, AssetItem after,
        ScriptSourceDocument source, PackageMetadataRevision[] metadata, Package[] rootPackages) : Operation
    {
        private readonly Dictionary<Package, AssetReference[]> beforeRoots = rootPackages.ToDictionary(package => package, package => package.RootAssets.ToArray());
        private void ApplyRoots(bool forward)
        {
            foreach (var pair in beforeRoots)
            {
                pair.Key.RootAssets.Clear();
                foreach (var reference in pair.Value)
                    pair.Key.RootAssets.Add(forward && reference.Id == before.Id ? new AssetReference(reference.Id, after.Location) : reference);
                pair.Key.IsDirty = true;
            }
        }
        internal void ApplyInitial() => Apply(true);
        internal void CancelInitial() => Apply(false);
        protected override void Rollback() => Apply(false);
        protected override void Rollforward() => Apply(true);
        private void Apply(bool forward)
        {
            var from = forward ? before : after; var to = forward ? after : before; var package = from.Package;
            if (package.IsReadOnly || to.Package.IsReadOnly || !ReferenceEquals(package.Assets.Find(from.Id), from)) throw new InvalidOperationException("The source package identity changed.");
            if (!string.Equals(source.Path, Path.GetFullPath(from.FullPath.ToOSPath()), StringComparison.Ordinal)) throw new InvalidOperationException("The source document path changed.");
            var moved = false; var replaced = false;
            try
            {
                game.RelocateAssetDocumentPath(from.FullPath.ToOSPath(), to.FullPath.ToOSPath()); moved = true;
                if (!package.Assets.RemoveById(from.Id)) throw new InvalidOperationException("The source package item disappeared.");
                try { to.Package.Assets.Add(to); replaced = true; }
                catch { package.Assets.Add(from); throw; }
                source.BindSourceAssetItem(to);
                if (to.Package.Container is SolutionProject owningProject) source.SetProjectOwnership(owningProject.FullPath.ToOSPath(), Path.GetFileName(source.Path), to.UnqualifiedUrl.ToString().Split('/')[..^1]);
                else source.ClearProjectOwnership();
                ApplyRoots(forward);
                foreach (var change in metadata) change.Apply(forward);
                package.IsDirty = to.Package.IsDirty = true; to.IsDirty = source.IsDirty;
                game.assetRelocationHistory.TrackSourceMove(from.FullPath.ToOSPath(), to.FullPath.ToOSPath());
                game.workspace!.RefreshAssets();
            }
            catch
            {
                if (replaced) { to.Package.Assets.RemoveById(to.Id); package.Assets.Add(from); }
                if (moved) game.RelocateAssetDocumentPath(to.FullPath.ToOSPath(), from.FullPath.ToOSPath());
                source.BindSourceAssetItem(from);
                if (from.Package.Container is SolutionProject restoredProject) source.SetProjectOwnership(restoredProject.FullPath.ToOSPath(), Path.GetFileName(source.Path), from.UnqualifiedUrl.ToString().Split('/')[..^1]);
                else source.ClearProjectOwnership();
                ApplyRoots(!forward);
                foreach (var change in metadata) change.Apply(!forward);
                game.workspace!.RefreshAssets(); throw;
            }
        }
    }
}
