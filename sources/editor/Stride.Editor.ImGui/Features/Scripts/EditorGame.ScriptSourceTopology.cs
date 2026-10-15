using System.Security.Cryptography;
using Stride.Assets.Scripts;
using Stride.Core.Assets;
using Stride.Core.IO;
using Stride.Core.Transactions;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private readonly Dictionary<string, ScriptSourceTopologyOperation> scriptSourceRemovals = new(StringComparer.OrdinalIgnoreCase);
    private sealed record PreparedScriptSource(ScriptSourceDocument Document, ScriptCodeActionSourceEdit Edit, EditorAuthoringState State,
        AssetItem? Item = null, ProjectBuildSettingsDocument? Project = null, ScriptSourceTopologyOperation? Topology = null,
        OwnedSourceDirectoriesOperation? Directories = null);

    private PreparedScriptSource PrepareScriptSourceTopology(ScriptCodeActionBatch batch, ScriptCodeActionSourceEdit edit)
    {
        var history = SessionAuthoringHistory!;
        var project = workspace!.Projects.SingleOrDefault(candidate => edit.ProjectPath != null && SamePath(candidate.FullPath.ToOSPath(), edit.ProjectPath))
            ?? throw new InvalidOperationException("The provider source has no actual loaded project owner.");
        if (project.Package.IsReadOnly) throw new InvalidOperationException("The source project is read-only.");
        var root = Path.GetDirectoryName(project.FullPath.ToOSPath())!;
        var relative = Path.GetRelativePath(root, edit.Path);
        if (Path.IsPathRooted(relative) || relative.StartsWith("..", StringComparison.Ordinal) || relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => part is "obj" or "bin"))
            throw new InvalidOperationException("The provider destination is outside the authored project source directory.");
        for (var directory = Path.GetDirectoryName(edit.Path); directory != null; directory = Path.GetDirectoryName(directory))
        {
            if (File.Exists(directory) || !Directory.Exists(directory) && edit.Kind != ScriptSourceChangeKind.Added
                || Directory.Exists(directory) && (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("The provider destination has an unavailable or redirected directory.");
            if (SamePath(directory, root)) break;
        }
        var projectState = authoring!.GetOrCreate(project.FullPath.ToOSPath(), null, () => new ProjectBuildSettingsDocument(project, history));
        projectState.Document.ValidateProjectFileBaseline();
        ScriptSourceDocument document;
        EditorAuthoringState state;
        AssetItem item;
        if (edit.Kind == ScriptSourceChangeKind.Added)
        {
            if (authoring.Find(edit.Path) != null || File.Exists(edit.Path) || Directory.Exists(edit.Path)) throw new InvalidOperationException("The source destination is occupied.");
            var origin = batch.Context.OrderBy(snapshot => !string.Equals(snapshot.Path, batch.OriginPath, StringComparison.OrdinalIgnoreCase)).Select(snapshot => FindAuthoredScript(snapshot.Path)).FirstOrDefault(source => source?.ProjectPath != null && SamePath(source.ProjectPath, project.FullPath.ToOSPath()))
                ?? throw new InvalidOperationException("The new source has no canonical originating project source.");
            var originItem = project.Package.Assets.FirstOrDefault(asset => SamePath(asset.FullPath.ToOSPath(), origin.Path) && asset.Asset is ScriptSourceFileAsset && asset.AlternativePath == null)
                ?? throw new InvalidOperationException("The originating source has no physical package asset owner.");
            var sourceRoot = Path.GetFullPath(Path.Combine(project.Package.RootDirectory.ToOSPath(), originItem.SourceFolder?.ToOSPath() ?? ""));
            var sourceRelative = Path.GetRelativePath(sourceRoot, edit.Path);
            if (Path.IsPathRooted(sourceRelative) || sourceRelative.StartsWith("..", StringComparison.Ordinal)) throw new InvalidOperationException("The new source leaves its actual package source folder.");
            var url = sourceRelative[..^Path.GetExtension(sourceRelative).Length].Replace('\\', '/');
            var location = project.Qualify(new UFile(url));
            var asset = new ScriptSourceFileAsset { Id = SourceCodeAsset.GenerateIdFromLocation(project.Package.Meta.Name, location.ToOSPath()) };
            item = originItem.Clone(true, location, asset);
            if (project.Package.Assets.Any(existing => existing.Id == item.Id || existing.Location == item.Location) || !SamePath(item.FullPath.ToOSPath(), edit.Path))
                throw new InvalidOperationException("The source asset identity or path is occupied.");
            var text = Microsoft.CodeAnalysis.Text.SourceText.From(edit.Before).WithChanges(edit.Changes).ToString();
            document = new AssetScriptSourceDocument(edit.Path, asset, text, origin.CopySourceEncoding(), history);
            document.SetProjectOwnership(project.FullPath.ToOSPath(), edit.Name, edit.Folders); document.BindSourceAssetItem(item);
            try { state = authoring.RegisterUnpublished(document, item.Id); }
            catch { document.Dispose(); throw; }
        }
        else
        {
            item = project.Package.Assets.FirstOrDefault(asset => SamePath(asset.FullPath.ToOSPath(), edit.Path) && asset.Asset is ScriptSourceFileAsset && asset.AlternativePath == null)
                ?? throw new InvalidOperationException("The removed source has no physical package asset owner.");
            document = GetCanonicalDocument(edit.Path, () => CreateScriptDocument(edit.Path)) as ScriptSourceDocument
                ?? throw new InvalidOperationException("The source has another canonical owner.");
            state = authoring.Find(edit.Path)!;
            document.ValidateCodeActionBaseline(edit);
        }
        try
        {
            var metadata = PackageMetadataRevision.Prepare(history, workspace, [project.Package]);
            var operation = new ScriptSourceTopologyOperation(this, project.Package, item, state, projectState, edit.Kind == ScriptSourceChangeKind.Added, metadata);
            return new(document, edit, state, item, projectState.Document, operation,
                edit.Kind == ScriptSourceChangeKind.Added ? new OwnedSourceDirectoriesOperation(root) : null);
        }
        catch { if (!state.IsPublished) authoring.ReleaseIfUnused(state); throw; }
    }

    private bool TryGuardScriptSourceFileRemoval(Package package, string path, PackageFileRemovalKind kind)
    {
        if (kind != PackageFileRemovalKind.Asset || !scriptSourceRemovals.TryGetValue(Path.GetFullPath(path), out var operation)) return false;
        operation.RemoveSavedFile(package); return true;
    }
    private void ClearScriptSourceTopology()
    {
        if (scriptSourceRemovals.Values.Any(operation => operation.HasPendingRetention))
            throw new InvalidOperationException("Save or restore removed source ownership before closing the session.");
        scriptSourceRemovals.Clear();
    }
    private ScriptProjectMembershipSnapshot[] CaptureScriptProjectMembership() => authoring?.PublishedStates
        .Select(state => state.Document).OfType<ProjectBuildSettingsDocument>()
        .OrderBy(document => document.Path, StringComparer.OrdinalIgnoreCase)
        .Select(document => new ScriptProjectMembershipSnapshot(Path.GetFullPath(document.Path), document.AuthoredCompileItems.ToArray(), document.AuthoringRevision)).ToArray() ?? [];
    private ScriptSourceSnapshot[] CaptureScriptSourceSnapshots() => documents.AuthoredDocuments.OfType<ScriptSourceDocument>().Where(source => source.IsCSharpSource)
        .Select(source => new ScriptSourceSnapshot(source.Path, source.Version, source.EditorText, source.ProjectPath,
            string.IsNullOrEmpty(source.SourceName) ? null : source.SourceName, source.SourceFolders.ToArray(), source.WasCreatedInSession,
            source is AssetScriptSourceDocument && source.CanEditText)).ToArray();

    private sealed class ScriptSourceTopologyOperation(EditorGame game, Package package, AssetItem item, EditorAuthoringState state, EditorAuthoringState? projectState,
        bool creating, PackageMetadataRevision[] metadata) : Operation
    {
        private bool applied, frozen, ownedFileRemoved;
        private bool preparingInitial, pendingInitialRecovery;
        private Action[]? pendingRecovery;
        private byte[]? savedBytes;
        private IDisposable? removalLease;
        private EditorDocumentEntry? detachedEntry;
        private int detachedIndex;
        private readonly bool initialDirty = package.IsDirty;
        private readonly AssetReference[] roots = package.RootAssets.Where(reference => reference.Id == item.Id).ToArray();
        internal bool HasPendingRetention => removalLease != null;
        private ScriptSourceDocument Source => (ScriptSourceDocument)state.Document;
        internal EditorAuthoringState[] Participants => projectState == null ? [state, .. metadata.Select(change => change.State)]
            : [state, projectState, .. metadata.Select(change => change.State)];
        internal void ApplyInitial()
        {
            preparingInitial = true;
            try { SetIncluded(creating, false); applied = true; }
            finally { preparingInitial = false; }
        }
        internal void CancelInitial() { if (applied) { SetIncluded(!creating, true); package.IsDirty = initialDirty; applied = false; } }
        protected override void Rollback()
        {
            var initial = pendingInitialRecovery;
            RestorePending();
            if (!initial) SetIncluded(!creating, true);
        }
        protected override void Rollforward() { RestorePending(); SetIncluded(creating, true); }
        private void RestorePending()
        {
            if (pendingRecovery == null) return;
            var errors = new List<Exception>();
            foreach (var restore in pendingRecovery) try { restore(); } catch (Exception error) { errors.Add(error); }
            if (errors.Count != 0) throw new AggregateException("The source topology recovery remains incomplete.", errors);
            pendingRecovery = null; pendingInitialRecovery = false;
        }

        private void SetIncluded(bool included, bool replay)
        {
            if (included && !game.IsCurrentEditableAssetPackage(package)) throw new InvalidOperationException("The source package is no longer available.");
            if (package.IsReadOnly) throw new InvalidOperationException("The source package is read-only.");
            if (included && (package.Assets.Find(item.Id) != null || package.Assets.Any(candidate => candidate.Location == item.Location) || game.authoring!.Find(Source.Path) != null))
                throw new InvalidOperationException("The restored source path is occupied.");
            if (!included && (!ReferenceEquals(package.Assets.Find(item.Id), item) || !state.IsPublished))
                throw new InvalidOperationException("The source is no longer its actual package owner.");
            var wasDirty = package.IsDirty; var wasItemDirty = item.IsDirty; var wasDeleted = item.IsDeleted;
            var previousRevisions = metadata.Select(change => change.State.Document.Revision).ToArray();
            var previousRoots = package.RootAssets.ToArray();
            var previousEntry = detachedEntry; var previousIndex = detachedIndex;
            var previousRemoval = game.scriptSourceRemovals.GetValueOrDefault(Source.Path);
            var previouslyFileRemoved = ownedFileRemoved;
            var fileRestored = false; var fileRemoved = false; var itemChanged = false; var published = false; var viewChanged = false;
            IDisposable? acquiredRemoval = null;
            try
            {
                if (included)
                {
                    if (ownedFileRemoved && savedBytes != null) { WindowsOwnedAssetFileWriter.Write(Source.Path, null, savedBytes); fileRestored = true; }
                    else ValidateOwnedFile();
                    item.IsDeleted = false; package.Assets.Add(item); itemChanged = true;
                    game.authoring!.Publish(state); published = true;
                    if (detachedEntry != null) { game.documents.Restore(detachedEntry, detachedIndex); viewChanged = true; }
                    item.IsDirty = Source.IsDirty;
                    foreach (var reference in roots) if (!package.RootAssets.Any(current => current.Id == reference.Id)) package.RootAssets.Add(reference);
                    game.scriptSourceRemovals.Remove(Source.Path);
                }
                else
                {
                    ValidateOwnedFile();
                    if (creating && replay) { RemoveSavedFile(package); fileRemoved = ownedFileRemoved && !previouslyFileRemoved; }
                    if (removalLease == null) acquiredRemoval = state.AcquireHistory();
                    detachedEntry = game.documents.Find(Source); detachedIndex = detachedEntry == null ? -1 : game.documents.IndexOf(detachedEntry);
                    if (detachedEntry != null) { game.documents.Detach(detachedEntry); viewChanged = true; }
                    game.authoring!.Unpublish(state); published = true;
                    if (!package.Assets.RemoveById(item.Id)) throw new InvalidOperationException("The source is no longer in its package.");
                    itemChanged = true; item.IsDeleted = true; game.scriptSourceRemovals[Source.Path] = this;
                    foreach (var reference in package.RootAssets.Where(reference => reference.Id == item.Id).ToArray()) package.RootAssets.Remove(reference);
                }
                foreach (var change in metadata) change.Apply(included == creating);
                package.IsDirty = true; game.workspace!.RefreshAssets();
                game.activateAfterAssetChange = game.documents.Active;
                if (included) { removalLease?.Dispose(); removalLease = null; ownedFileRemoved = false; }
                else { removalLease ??= acquiredRemoval; acquiredRemoval = null; }
            }
            catch (Exception failure)
            {
                var errors = new List<Exception>();
                var recovery = new List<Action>();
                void Restore(Action action) { recovery.Add(action); try { action(); } catch (Exception error) { errors.Add(error); } }
                if (included)
                {
                    if (viewChanged) Restore(() => { if (game.documents.Find(Source) != null) game.documents.Detach(detachedEntry!); });
                    if (published) Restore(() => { if (state.IsPublished) game.authoring!.Unpublish(state); });
                    if (itemChanged) Restore(() => { if (ReferenceEquals(package.Assets.Find(item.Id), item)) package.Assets.RemoveById(item.Id); });
                }
                else
                {
                    if (itemChanged) Restore(() => { if (package.Assets.Find(item.Id) == null) package.Assets.Add(item); else if (!ReferenceEquals(package.Assets.Find(item.Id), item)) throw new InvalidOperationException("The source recovery identity is occupied."); });
                    if (published) Restore(() => { if (!state.IsPublished) game.authoring!.Publish(state); });
                    if (viewChanged) { var view = detachedEntry!; var position = detachedIndex; Restore(() => { if (game.documents.Find(Source) == null) game.documents.Restore(view, position); }); }
                }
                for (var index = 0; index < metadata.Length; index++)
                { var captured = index; Restore(() => metadata[captured].State.Document.RestoreRevision(previousRevisions[captured])); }
                Restore(() => { package.RootAssets.Clear(); foreach (var reference in previousRoots) package.RootAssets.Add(reference); });
                if (fileRestored) Restore(() => { if (File.Exists(Source.Path)) WindowsOwnedAssetFileDeletion.Delete(Source.Path, Source.DiskContentHash.Span); else if (Directory.Exists(Source.Path)) throw new IOException("The source recovery path is occupied."); });
                if (fileRemoved && savedBytes != null) Restore(() => { if (File.Exists(Source.Path)) ValidateOwnedFile(); else WindowsOwnedAssetFileWriter.Write(Source.Path, null, savedBytes); });
                Restore(() =>
                {
                    if (previousRemoval != null) game.scriptSourceRemovals[Source.Path] = previousRemoval; else game.scriptSourceRemovals.Remove(Source.Path);
                    detachedEntry = previousEntry; detachedIndex = previousIndex; ownedFileRemoved = previouslyFileRemoved;
                });
                Restore(() => { item.IsDeleted = wasDeleted; item.IsDirty = wasItemDirty; package.IsDirty = wasDirty; });
                Restore(() => game.workspace!.RefreshAssets());
                if (errors.Count != 0)
                {
                    pendingRecovery = recovery.ToArray(); pendingInitialRecovery = preparingInitial;
                    if (preparingInitial) game.sessionHistory!.RetainFailedInitialOperation(this, Participants);
                    errors.Insert(0, failure); throw new AggregateException("The source topology operation could not be restored.", errors);
                }
                throw;
            }
            finally { acquiredRemoval?.Dispose(); }
        }
        private void ValidateOwnedFile()
        {
            if (Source.DiskContentHash.IsEmpty)
            {
                if (File.Exists(Source.Path) || Directory.Exists(Source.Path)) throw new IOException("The source path is occupied.");
            }
            else if (!File.Exists(Source.Path) || !SHA256.HashData(File.ReadAllBytes(Source.Path)).AsSpan().SequenceEqual(Source.DiskContentHash.Span))
                throw new IOException("The source file changed outside the editor.");
        }
        internal void RemoveSavedFile(Package target)
        {
            if (!ReferenceEquals(target, package)) throw new InvalidOperationException("The removed source belongs to another package.");
            if (!File.Exists(Source.Path))
            {
                if (Directory.Exists(Source.Path) || !ownedFileRemoved && !Source.DiskContentHash.IsEmpty) throw new IOException("The removed source file changed outside the editor.");
            }
            else
            {
                var bytes = File.ReadAllBytes(Source.Path);
                if (Source.DiskContentHash.IsEmpty || !SHA256.HashData(bytes).AsSpan().SequenceEqual(Source.DiskContentHash.Span)) throw new IOException("The removed source file changed outside the editor.");
                WindowsOwnedAssetFileDeletion.Delete(Source.Path, Source.DiskContentHash.Span); savedBytes = bytes; ownedFileRemoved = true;
            }
            if (frozen) { game.scriptSourceRemovals.Remove(Source.Path); removalLease?.Dispose(); removalLease = null; }
        }
        protected override void FreezeContent()
        {
            frozen = true;
            if (state.IsPublished || ownedFileRemoved || Source.DiskContentHash.IsEmpty)
            { game.scriptSourceRemovals.Remove(Source.Path); removalLease?.Dispose(); removalLease = null; }
            if (detachedEntry != null && !state.IsPublished) { game.ReleaseScriptEditor(Source); detachedEntry = null; }
            savedBytes = null;
        }
    }
}
