using Stride.Core.Assets;
using Stride.Core.Assets.Analysis;
using Stride.Core.IO;
using Stride.Core.Serialization.Contents;
using Stride.Core.Transactions;

namespace Stride.Editor.ImGui;

/// <summary>Identity-preserving package URL moves with document-owned reference history.</summary>
internal sealed partial class AssetRelocationHistory
{
    private readonly EditorSessionHistory? sessionHistory;
    public AssetRelocationHistory(EditorSessionHistory? sessionHistory = null) => this.sessionHistory = sessionHistory;
    private sealed record DocumentChange(IAssetReferenceDocument Document, PreparedAssetReferenceEdit Edit);
    private sealed record RootChange(Package Package, AssetReference Before, AssetReference After);
    private sealed record Change(ProjectWorkspace Workspace, AssetItem Before, AssetItem After, string BeforePath, string AfterPath,
        IAssetReferenceDocument RelocatedDocument, DocumentChange[] Documents, RootChange[] Roots, Action<string, string> RelocatePath)
    {
        public Guid Id { get; } = Guid.NewGuid();
        public PackageMetadataRevision[] Metadata { get; init; } = [];
    }
    private readonly List<Change> history = [];
    private readonly Dictionary<IAssetReferenceDocument, HashSet<Guid>> orphanedOperations = [];
    private readonly HashSet<string> movedFromPaths = new(StringComparer.OrdinalIgnoreCase);
    private int position;
    public long Version { get; private set; }
    public bool CanUndo => sessionHistory?.CanUndo ?? (position > 0 && CanApply(history[position - 1], false));
    public bool CanRedo => sessionHistory?.CanRedo ?? (position < history.Count && CanApply(history[position], true));
    public GroupedHistoryOwnership GetOwnership(IEditorDocument? document, bool redo)
    {
        if (sessionHistory != null) return GroupedHistoryOwnership.None;
        if (document is not IAssetReferenceDocument authored) return GroupedHistoryOwnership.None;
        var operation = redo ? authored.NextRedoOperationId : authored.NextUndoOperationId;
        if (operation == null) return GroupedHistoryOwnership.None;
        for (var index = 0; index < history.Count; index++)
            if (history[index].Documents.Any(change => ReferenceEquals(change.Document, authored) && change.Edit.OperationId == operation))
                return (redo ? index == position && CanRedo : index == position - 1 && CanUndo)
                    ? GroupedHistoryOwnership.Ready : GroupedHistoryOwnership.Blocked;
        return orphanedOperations.TryGetValue(authored, out var ids) && ids.Contains(operation.Value)
            ? GroupedHistoryOwnership.Blocked : GroupedHistoryOwnership.None;
    }
    public bool Relocate(ProjectWorkspace workspace, AssetEntry entry, string nextUrl,
        IReadOnlyDictionary<AssetId, IAssetReferenceDocument> retained,
        Func<AssetEntry, IAssetReferenceDocument> getDocument, Action<string, string> relocatePath, Package? destinationPackage = null)
    {
        if (sessionHistory != null && !sessionHistory.IsCapturingOperations)
        {
            var changed = false;
            using var capture = sessionHistory.CaptureOperationsAtomic(() => changed = Relocate(workspace, entry, nextUrl, retained, getDocument, relocatePath, destinationPackage));
            if (changed) capture.Publish(sessionHistory, "Move asset"); return changed;
        }
        var original = workspace.FindAsset(entry.Path); var package = original.Package; var targetPackage = destinationPackage ?? package;
        if (package.IsReadOnly || targetPackage.IsReadOnly || !ReferenceEquals(targetPackage.Session, package.Session) || targetPackage.State < PackageState.AssetsReady) throw new InvalidOperationException("This asset can't be edited.");
        if (original.Asset is IProjectAsset && !(original.Asset is Stride.Assets.Scripts.VisualScriptAsset && package.Container is SolutionProject) || original.AlternativePath != null)
            throw new InvalidOperationException("This asset cannot be moved.");
        nextUrl = nextUrl.Replace('\\', '/'); ValidateUrl(nextUrl);
        var location = targetPackage.Container?.Qualify(new UFile(nextUrl)) ?? new UFile(nextUrl);
        if (ReferenceEquals(package, targetPackage) && string.Equals(location.FullPath, original.Location.FullPath, StringComparison.Ordinal)) return false;

        if (targetPackage.Assets.Any(item => item.Id != original.Id && string.Equals(item.UnqualifiedUrl.FullPath, nextUrl, StringComparison.OrdinalIgnoreCase) || !ReferenceEquals(package, targetPackage) && item.Id == original.Id))
            throw new InvalidOperationException("An asset with the same name exists in the same directory.");
        var movedDocument = retained.GetValueOrDefault(entry.Id) ?? getDocument(entry);
        movedDocument.CommitEdits();
        var before = original.Clone(true, newAsset: movedDocument.AuthoredAsset);
        var after = ReferenceEquals(package, targetPackage) ? original.Clone(true, newLocation: location, newAsset: movedDocument.AuthoredAsset)
            : original.CloneForPackage(targetPackage, location, movedDocument.AuthoredAsset);
        var beforePath = Path.GetFullPath(original.FullPath.ToOSPath()); var afterPath = Path.GetFullPath(after.FullPath.ToOSPath());
        ValidateDiskMove(beforePath, afterPath, movedDocument is IRelocatableEditorDocument owned && owned.DiskContentHash.IsEmpty);
        var locations = new Dictionary<AssetId, string> { [entry.Id] = location.FullPath };
        var authored = retained.ToDictionary(pair => pair.Key, pair => pair.Value.AuthoredAsset);
        authored[entry.Id] = movedDocument.AuthoredAsset;
        var dependencies = workspace.Session.DependencyManager.CaptureDependencies(authored);
        var affected = dependencies.GetValueOrDefault(entry.Id)?.LinksIn.Where(link => (link.Type & ContentLinkType.Reference) != 0)
            .Select(link => link.Item.Id).Append(entry.Id).Distinct().ToArray() ?? [entry.Id];
        var prepared = new List<DocumentChange>();
        foreach (var id in affected)
        {
            var asset = workspace.Assets.FirstOrDefault(asset => asset.Id == id);
            if (asset == null) continue;
            var document = id == entry.Id ? movedDocument : retained.GetValueOrDefault(id) ?? getDocument(asset);
            document.CommitEdits();
            var edit = document.PrepareReferenceLocations(locations, id == entry.Id ? beforePath : null, id == entry.Id ? afterPath : null);
            if (edit != null) prepared.Add(new DocumentChange(document, edit));
        }
        var roots = workspace.Session.LocalPackages.Where(candidate => !candidate.IsReadOnly)
            .SelectMany(candidate => candidate.RootAssets.Where(reference => reference.Id == entry.Id)
                .Select(reference => new RootChange(candidate, reference, new AssetReference(entry.Id, location)))).ToArray();
        var change = new Change(workspace, before, after, beforePath, afterPath, movedDocument, prepared.ToArray(), roots, relocatePath)
        { Metadata = PackageMetadataRevision.Prepare(sessionHistory, workspace, roots.Select(root => root.Package).Append(package).Append(targetPackage)) };
        if (sessionHistory != null && change.Documents.Select(document => document.Document).Append(movedDocument)
            .Any(document => document is not ISessionAuthoringDocument owner || !ReferenceEquals(owner.SessionHistory, sessionHistory)))
            throw new InvalidOperationException("Asset relocation requires documents from the same authored session.");
        var replaced = false; var moved = false;
        try
        {
            ReplaceItem(package, original, after); replaced = true;
            foreach (var document in change.Documents) document.Edit.Apply();
            ApplyRoots(change, true);
            relocatePath(beforePath, afterPath); moved = true;
            UpdateGraphIdentity(movedDocument, after);
            foreach (var metadata in change.Metadata) metadata.Apply(true);
            if (sessionHistory == null)
                foreach (var document in change.Documents) document.Edit.Commit();
            else if (sessionHistory.IsCapturingOperations)
            {
                // Capture retains genuine operations, but does not itself open
                // a core transaction. UI relocation has no outer caller scope.
                using var transaction = sessionHistory.CreateTransaction("Move asset '" + before.UnqualifiedUrl + "' to '" + after.UnqualifiedUrl + "'");
                foreach (var document in change.Documents) document.Edit.Commit();
                var targetState = sessionHistory.Registry.Find(entry.Id) ?? throw new InvalidOperationException("The moved asset has no authored session state.");
                sessionHistory.PushOperation(new RelocationOperation(this, change), [targetState, .. change.Metadata.Select(metadata => metadata.State)]);
            }
            else
            {
                using var capture = sessionHistory.CaptureOperations(() =>
                {
                    foreach (var document in change.Documents) document.Edit.Commit();
                });
                var targetState = sessionHistory.Registry.Find(entry.Id)
                    ?? throw new InvalidOperationException("The moved asset has no authored session state.");
                var participants = capture.Participants.Append(targetState).Concat(change.Metadata.Select(metadata => metadata.State)).Distinct().ToArray();
                using var transaction = sessionHistory.CreateTransaction("Move asset '" + before.UnqualifiedUrl + "' to '" + after.UnqualifiedUrl + "'");
                sessionHistory.PushOperation(new CompositeOperation(capture.Entries.Select(record => record.Operation)
                    .Append(new RelocationOperation(this, change))), participants);
            }
        }
        catch
        {
            foreach (var document in change.Documents.Reverse()) document.Edit.Restore();
            ApplyRoots(change, false);
            if (moved) relocatePath(afterPath, beforePath);
            if (replaced) { ReplaceItem(after.Package, after, original); UpdateGraphIdentity(movedDocument, original); }
            foreach (var metadata in change.Metadata) metadata.Apply(false);
            workspace.RefreshAssets(); throw;
        }
        if (sessionHistory != null)
        { TrackMovedPaths(beforePath, afterPath); workspace.RefreshAssets(); Version++; return true; }
        if (position < history.Count)
        {
            foreach (var discarded in history.Skip(position)) Remember(discarded);
            history.RemoveRange(position, history.Count - position);
        }
        history.Add(change); position++;
        if (history.Count > 256) { Remember(history[0]); history.RemoveAt(0); position--; }
        TrackMovedPaths(beforePath, afterPath); workspace.RefreshAssets(); Version++; return true;
    }
    public void Undo() { if (sessionHistory != null) { sessionHistory.Undo(); return; } if (CanUndo && Roll(history[position - 1], false)) { position--; Version++; } }
    public void Redo() { if (sessionHistory != null) { sessionHistory.Redo(); return; } if (CanRedo && Roll(history[position], true)) { position++; Version++; } }
    public void Clear() { history.Clear(); orphanedOperations.Clear(); movedFromPaths.Clear(); position = 0; Version++; }
    public bool TryRemoveMovedFromFile(string path)
    {
        path = Path.GetFullPath(path);
        if (!movedFromPaths.Contains(path)) return false;
        // The original object was moved away. Anything now occupying this old
        // name is a different file, even if its bytes happen to be identical.
        if (File.Exists(path) || Directory.Exists(path))
            throw new IOException("A file appeared at a previous asset path. Reload the project before saving.");
        return true;
    }
    public void NotifySaved()
    { foreach (var path in movedFromPaths.Where(path => !File.Exists(path) && !Directory.Exists(path)).ToArray()) movedFromPaths.Remove(path); }
    internal void TrackSourceMove(string from, string to) => TrackMovedPaths(from, to);
    private void TrackMovedPaths(string from, string to)
    { movedFromPaths.Add(Path.GetFullPath(from)); movedFromPaths.Remove(Path.GetFullPath(to)); }
    public void RemoveDocument(IEditorDocument document)
    {
        if (sessionHistory != null) return;
        if (history.Any(change => ReferenceEquals(change.RelocatedDocument, document) || change.Documents.Any(edit => ReferenceEquals(edit.Document, document))))
        {
            foreach (var change in history) Remember(change);
            history.Clear(); position = 0; Version++;
        }
        if (document is IAssetReferenceDocument authored) orphanedOperations.Remove(authored);
    }
    private void Remember(Change change)
    {
        foreach (var edit in change.Documents)
        {
            if (!orphanedOperations.TryGetValue(edit.Document, out var ids)) orphanedOperations.Add(edit.Document, ids = []);
            ids.Add(edit.Edit.OperationId);
        }
    }
    private static bool CanApply(Change change, bool forward, bool references = true)
    {
        var expected = forward ? change.Before : change.After;
        if (expected.Package.Assets.Find(expected.Id)?.Location != expected.Location) return false;
        return (!references || change.Documents.All(edit => edit.Document.EditRevision == (forward ? edit.Edit.BeforeRevision : edit.Edit.AfterRevision)
            && (forward ? edit.Document.NextRedoOperationId : edit.Document.NextUndoOperationId) == edit.Edit.OperationId
            && (forward ? edit.Document.CanRedo : edit.Document.CanUndo)))
            && change.Roots.All(root => root.Package.RootAssets.Any(reference => reference.Id == expected.Id
                && reference.Location == (forward ? root.Before.Location : root.After.Location)));
    }
    private bool Roll(Change change, bool forward, bool references = true)
    {
        if (references) foreach (var edit in change.Documents) edit.Document.CommitEdits();
        if (!CanApply(change, forward, references)) return false;
        var from = forward ? change.Before : change.After; var to = forward ? change.After : change.Before;
        var fromPath = forward ? change.BeforePath : change.AfterPath; var toPath = forward ? change.AfterPath : change.BeforePath;
        ValidateDiskMove(fromPath, toPath, change.RelocatedDocument is IRelocatableEditorDocument owned && owned.DiskContentHash.IsEmpty);
        var completed = new List<DocumentChange>(); var replaced = false; var moved = false;
        try
        {
            ReplaceItem(from.Package, from, to); replaced = true;
            if (references) foreach (var edit in forward ? change.Documents : change.Documents.Reverse())
            { if (forward) edit.Document.Redo(); else edit.Document.Undo(); completed.Add(edit); }
            ApplyRoots(change, forward); change.RelocatePath(fromPath, toPath); moved = true; UpdateGraphIdentity(change.RelocatedDocument, to);
            foreach (var metadata in change.Metadata) metadata.Apply(forward);
        }
        catch (Exception failure)
        {
            var errors = new List<Exception> { failure };
            void Restore(Action action) { try { action(); } catch (Exception error) { errors.Add(error); } }
            if (moved) Restore(() => change.RelocatePath(toPath, fromPath));
            Restore(() => ApplyRoots(change, !forward));
            foreach (var edit in completed.AsEnumerable().Reverse()) Restore(() => { if (forward) edit.Document.Undo(); else edit.Document.Redo(); });
            if (replaced) Restore(() => { ReplaceItem(to.Package, to, from); UpdateGraphIdentity(change.RelocatedDocument, from); });
            Restore(change.Workspace.RefreshAssets);
            if (errors.Count > 1) throw new AggregateException("The relocation failed and could not be fully restored.", errors);
            throw;
        }
        TrackMovedPaths(fromPath, toPath); change.Workspace.RefreshAssets();
        return true;
    }
    private sealed class RelocationOperation(AssetRelocationHistory owner, Change change) : Operation
    {
        protected override void Rollback() => Replay(false);
        protected override void Rollforward() => Replay(true);
        private void Replay(bool forward)
        {
            if (!owner.Roll(change, forward, false)) throw new InvalidOperationException("The asset location changed.");
            owner.Version++;
        }
    }
    private static void ReplaceItem(Package package, AssetItem from, AssetItem to)
    {
        var current = package.Assets.Find(from.Id) ?? throw new InvalidOperationException("The asset no longer exists.");
        if (current.Location != from.Location) throw new InvalidOperationException("The asset location changed.");
        package.Assets.Remove(current);
        try { to.Package.Assets.Add(to); }
        catch { package.Assets.Add(current); throw; }
        to.IsDirty = true; package.IsDirty = true; to.Package.IsDirty = true;
    }
    private static void UpdateGraphIdentity(IAssetReferenceDocument document, AssetItem item)
    {
        if (document is IEditorAssetGraphDocument graph) graph.UpdateAuthoringAssetIdentity(item);
        else if (document is EntityHierarchyDocument hierarchy) hierarchy.HierarchyGraph.UpdateAssetItemIdentity(item);
    }
    private static void ApplyRoots(Change change, bool forward)
    {
        foreach (var root in change.Roots)
        { root.Package.RootAssets.Remove(root.Before.Id); root.Package.RootAssets.Add(forward ? root.After : root.Before); root.Package.IsDirty = true; }
    }
    private static void ValidateDiskMove(string before, string after, bool unsaved = false)
    {
        if ((!unsaved && !File.Exists(before)) || unsaved && (File.Exists(before) || Directory.Exists(before))) throw new FileNotFoundException("The asset file does not exist.", before);
        if (!string.Equals(before, after, StringComparison.OrdinalIgnoreCase) && File.Exists(after))
            throw new IOException("An asset file with the same name already exists.");
    }
    internal static void ValidateUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.StartsWith('/') || Path.IsPathRooted(url)
            || url.Split('/').Any(part => string.IsNullOrWhiteSpace(part) || part is "." or ".."
                || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || part.EndsWith('.') || part.EndsWith(' ')))
            throw new ArgumentException("Invalid asset name.");
        foreach (var part in url.Split('/'))
        {
            var device = part.Split('.')[0];
            if (device.Equals("CON", StringComparison.OrdinalIgnoreCase) || device.Equals("PRN", StringComparison.OrdinalIgnoreCase)
                || device.Equals("AUX", StringComparison.OrdinalIgnoreCase) || device.Equals("NUL", StringComparison.OrdinalIgnoreCase)
                || device.Length == 4 && (device.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || device.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
                    && device[3] is >= '1' and <= '9') throw new ArgumentException("Invalid asset name.");
        }
    }
}
