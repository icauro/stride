using System.Security.Cryptography;
using Stride.Core.Assets;
using Stride.Core.Assets.Analysis;
using Stride.Core.Serialization.Contents;
using Stride.Core.Transactions;

namespace Stride.Editor.ImGui;

/// <summary>Package deletion, retained authored documents and reversible reference cleanup.</summary>
internal sealed class AssetDeletionHistory
{
    private readonly EditorSessionHistory? sessionHistory;
    public AssetDeletionHistory(EditorSessionHistory? sessionHistory = null) => this.sessionHistory = sessionHistory;
    internal sealed class Target(AssetItem item, IAssetReferenceDocument document, byte[]? bytes)
    {
        public AssetItem Item { get; } = item;
        public IAssetReferenceDocument Document { get; } = document;
        public long Revision { get; } = document.EditRevision;
        public byte[]? Bytes { get; set; } = bytes;
        public bool Detached { get; set; }
    }
    internal sealed record ReferenceChange(IAssetDeletionDocument Document, PreparedAssetDeletionReferences Edit);
    internal sealed record RootChange(Package Package, AssetReference Reference);
    internal sealed record ReferenceRow(AssetEntry Owner, AssetDeletionReferenceChoice Choice);
    internal sealed class Plan
    {
        internal Guid Id { get; } = Guid.NewGuid();
        internal ProjectWorkspace Workspace = null!;
        internal Target[] Deleted = [];
        internal ReferenceChange[] References = [];
        internal RootChange[] Roots = [];
        internal PackageMetadataRevision[] Metadata = [];
        internal bool Forgotten;
        internal bool PreserveReferences;
        internal IReadOnlyDictionary<AssetId, Asset> AuthoredAssets = new Dictionary<AssetId, Asset>();
        internal Func<IReadOnlyDictionary<AssetId, Asset>>? GetAuthoredAssets;
        internal Action<IReadOnlyList<IAssetReferenceDocument>, bool> SetDocuments = null!;
        internal Action<IReadOnlyList<IAssetReferenceDocument>> ForgetDocuments = null!;
        public IReadOnlyList<AssetEntry> Targets { get; internal set; } = [];
        public IReadOnlyList<AssetEntry> Referencers { get; internal set; } = [];
        public IReadOnlyList<AssetEntry> ReferencedTargets { get; internal set; } = [];
        public IReadOnlyList<ReferenceRow> ReferenceRows { get; internal set; } = [];
        public bool HasReferences => References.Length > 0;
    }
    private readonly List<Plan> history = [];
    private readonly Dictionary<IAssetDeletionDocument, HashSet<Guid>> orphaned = [];
    private readonly Dictionary<string, byte[]> pendingFingerprints = new(StringComparer.OrdinalIgnoreCase);
    private int position;
    public bool CanUndo => sessionHistory?.CanUndo ?? (position > 0 && CanApply(history[position - 1], false));
    public bool CanRedo => sessionHistory?.CanRedo ?? (position < history.Count && CanApply(history[position], true));
    public AssetId[] DesiredSelection { get; private set; } = [];
    internal void SetDesiredSelection(IEnumerable<AssetId> ids) => DesiredSelection = ids.ToArray();
    public EditorHistorySnapshot History => sessionHistory?.History ?? new(history.Select((plan, index) => new EditorHistoryEntry(plan.Id,
        plan.Targets.Count == 1 ? "Delete asset '" + plan.Targets[0].Name + "'" : "Delete " + plan.Targets.Count + " assets",
        index < position, false)).ToArray(), position);
    public Plan Prepare(ProjectWorkspace workspace, IReadOnlyList<AssetEntry> targets,
        IReadOnlyDictionary<AssetId, IAssetReferenceDocument> retained, Func<AssetEntry, IAssetReferenceDocument> getDocument,
        Action<IReadOnlyList<IAssetReferenceDocument>, bool> setDocuments, Action<IReadOnlyList<IAssetReferenceDocument>> forgetDocuments,
        Func<IReadOnlyDictionary<AssetId, Asset>>? getAuthoredAssets = null, bool preserveReferences = false, IReadOnlyList<AssetEntry>? referenceTargets = null)
    {
        var allTargets = referenceTargets ?? targets;
        if (allTargets.Count == 0) throw new InvalidOperationException("No assets are selected.");
        var ids = allTargets.Select(asset => asset.Id).ToHashSet();
        var deleted = new List<Target>();
        foreach (var entry in targets.DistinctBy(asset => asset.Id))
        {
            var item = workspace.FindAsset(entry.Id);
            if (item.Package.IsReadOnly || item.Asset is IProjectAsset && !((preserveReferences || referenceTargets != null) && item.Asset is Stride.Assets.Scripts.VisualScriptAsset && item.Package.Container is SolutionProject) || item.AlternativePath != null)
                throw new InvalidOperationException("This asset cannot be deleted.");
            var document = retained.GetValueOrDefault(entry.Id) ?? getDocument(entry);
            document.CommitEdits();
            if (!document.CanSave) throw new InvalidOperationException("This asset cannot be deleted.");
            deleted.Add(new Target(item.Clone(true, newAsset: document.AuthoredAsset), document, ReadOwnedFile(document)));
        }
        var authored = retained.ToDictionary(pair => pair.Key, pair => pair.Value.AuthoredAsset);
        foreach (var target in deleted) authored[target.Item.Id] = target.Document.AuthoredAsset;
        var dependencies = workspace.Session.DependencyManager.CaptureDependencies(authored);
        var consumers = ids.SelectMany(id => dependencies.GetValueOrDefault(id)?.LinksIn ?? [])
            .Where(link => (link.Type & ContentLinkType.Reference) != 0 && !ids.Contains(link.Item.Id)).Select(link => link.Item.Id).Distinct().ToArray();
        var referenceChanges = new List<ReferenceChange>(); var consumerEntries = new List<AssetEntry>();
        foreach (var id in preserveReferences ? [] : consumers)
        {
            var entry = workspace.Assets.FirstOrDefault(asset => asset.Id == id)
                ?? throw new InvalidOperationException("An asset referencing this asset can't be edited.");
            var document = retained.GetValueOrDefault(id) ?? getDocument(entry);
            if (document is not IAssetDeletionDocument editable) throw new InvalidOperationException("These references can't be cleared.");
            var edit = editable.PrepareDeletedReferences(ids)
                ?? throw new InvalidOperationException("These references can't be cleared.");
            referenceChanges.Add(new ReferenceChange(editable, edit)); consumerEntries.Add(entry);
            authored[id] = editable.AuthoredAsset;
        }
        return new Plan
        {
            Workspace = workspace, Targets = allTargets.DistinctBy(asset => asset.Id).ToArray(), Referencers = consumerEntries.ToArray(),
            ReferencedTargets = allTargets.Where(target => dependencies.GetValueOrDefault(target.Id)?.LinksIn.Any(link =>
                (link.Type & ContentLinkType.Reference) != 0 && !ids.Contains(link.Item.Id)) == true).DistinctBy(target => target.Id).ToArray(),
            Deleted = deleted.ToArray(), References = referenceChanges.ToArray(), SetDocuments = setDocuments, ForgetDocuments = forgetDocuments,
            AuthoredAssets = authored,
            GetAuthoredAssets = getAuthoredAssets,
            PreserveReferences = preserveReferences,
            ReferenceRows = referenceChanges.SelectMany(change => change.Edit.Choices.Select(choice =>
                new ReferenceRow(consumerEntries.Single(entry => entry.Id == change.Document.AuthoredAsset.Id), choice))).ToArray(),
            Roots = workspace.Session.LocalPackages.Where(package => !package.IsReadOnly).SelectMany(package =>
                package.RootAssets.Where(root => ids.Contains(root.Id)).Select(root => new RootChange(package, root))).ToArray(),
        };
    }
    public void Delete(Plan plan, bool clearReferences)
    {
        if (sessionHistory != null && !sessionHistory.IsCapturingOperations)
        {
            using var capture = sessionHistory.CaptureOperationsAtomic(() => Delete(plan, clearReferences));
            capture.Publish(sessionHistory, plan.PreserveReferences ? "Cut selection" : "Delete assets"); return;
        }
        if (clearReferences)
            foreach (var row in plan.ReferenceRows.Where(row => !row.Choice.IsResolved)) row.Choice.Clear();
        if (plan.ReferenceRows.Any(row => !row.Choice.IsResolved)) throw new InvalidOperationException("Resolve asset references before deleting.");
        var deletedIds = plan.Targets.Select(target => target.Id).ToHashSet();
        foreach (var row in plan.ReferenceRows.Where(row => !row.Choice.IsClear && row.Choice.Replacement != null))
        {
            var replacement = row.Choice.Replacement!;
            var current = plan.Workspace.Session.FindAsset(replacement.Id);
            if (deletedIds.Contains(replacement.Id) || current == null || !row.Choice.Accepts(current)
                || ContentAssetReference.Read(row.Choice.After)?.Location != current.Location.ToString())
                throw new InvalidOperationException("The replacement asset changed. Select it again before deleting.");
        }
        foreach (var target in plan.Deleted)
        {
            target.Document.CommitEdits();
            if (target.Document.EditRevision != target.Revision || target.Item.Package.Assets.Find(target.Item.Id)?.Location != target.Item.Location)
                throw new InvalidOperationException("The asset changed. Select it again before deleting.");
            ReadOwnedFile(target.Document);
        }
        foreach (var reference in plan.References)
        { reference.Document.CommitEdits(); if (reference.Document.EditRevision != reference.Edit.BeforeRevision) throw new InvalidOperationException("The references changed. Select the assets again before deleting."); }
        if (sessionHistory != null && plan.References.Select(reference => (IEditorDocument)reference.Document).Concat(plan.Deleted.Select(target => target.Document))
            .Any(document => document is not ISessionAuthoringDocument owner || !ReferenceEquals(owner.SessionHistory, sessionHistory)))
            throw new InvalidOperationException("Asset deletion requires documents from the same authored session.");
        EditorAuthoringState[] targetStates = sessionHistory == null ? [] : plan.Deleted.Select(target => sessionHistory.Registry.Find(target.Item.Id)
            ?? throw new InvalidOperationException("The deleted asset has no authored session state.")).ToArray();
        using var targetRetention = new TargetRetention(targetStates);
        plan.Metadata = PackageMetadataRevision.Prepare(sessionHistory, plan.Workspace,
            plan.Roots.Select(root => root.Package).Concat(plan.Deleted.Select(target => target.Item.Package)));
        var removed = new List<Target>(); var detached = false; var rootsChanged = false;
        try
        {
            foreach (var reference in plan.References) reference.Edit.Apply();
            var remaining = plan.Workspace.Session.DependencyManager.CaptureDependencies(plan.GetAuthoredAssets?.Invoke() ?? plan.AuthoredAssets);
            if (!plan.PreserveReferences && deletedIds.Any(id => remaining.GetValueOrDefault(id)?.LinksIn.Any(link =>
                (link.Type & ContentLinkType.Reference) != 0 && !deletedIds.Contains(link.Item.Id)) == true))
                throw new InvalidOperationException("These references can't be edited.");
            foreach (var target in plan.Deleted) { RemoveTarget(target); removed.Add(target); }
            rootsChanged = true; ApplyRoots(plan, true);
            plan.Workspace.RefreshAssets();
            plan.SetDocuments(Documents(plan), true); detached = true;
            foreach (var target in plan.Deleted) target.Detached = true;
            foreach (var metadata in plan.Metadata) metadata.Apply(true);
            if (sessionHistory == null)
                foreach (var reference in plan.References) reference.Edit.Commit();
            else if (sessionHistory.IsCapturingOperations)
            {
                foreach (var reference in plan.References) reference.Edit.Commit();
                sessionHistory.PushOperation(new DeletionOperation(this, plan), targetStates
                    .Concat(plan.Metadata.Select(metadata => metadata.State)).Distinct().ToArray());
            }
            else
            {
                using var capture = sessionHistory.CaptureOperations(() =>
                {
                    foreach (var reference in plan.References) reference.Edit.Commit();
                });
                using var transaction = sessionHistory.CreateTransaction(plan.PreserveReferences ? "Cut selection" : plan.Targets.Count == 1
                    ? "Delete asset '" + plan.Targets[0].Name + "'" : "Delete " + plan.Targets.Count + " assets");
                sessionHistory.PushOperation(new CompositeOperation(capture.Entries.Select(record => record.Operation)
                    .Append(new DeletionOperation(this, plan))), capture.Participants.Concat(targetStates)
                    .Concat(plan.Metadata.Select(metadata => metadata.State)).Distinct().ToArray());
            }
        }
        catch
        {
            foreach (var target in removed.AsEnumerable().Reverse()) RestoreTarget(target);
            if (rootsChanged) ApplyRoots(plan, false);
            foreach (var reference in plan.References.Reverse()) reference.Edit.Restore();
            plan.Workspace.RefreshAssets();
            if (detached) { plan.SetDocuments(Documents(plan), false); foreach (var target in plan.Deleted) target.Detached = false; }
            foreach (var metadata in plan.Metadata) metadata.Apply(false);
            plan.Workspace.RefreshAssets(); throw;
        }
        if (sessionHistory != null)
        {
            if (plan.GetAuthoredAssets != null) plan.AuthoredAssets = new Dictionary<AssetId, Asset>();
            TrackPendingFiles(plan, true); DesiredSelection = []; plan.Workspace.RefreshAssets(); return;
        }
        if (position < history.Count)
        {
            foreach (var discarded in history.Skip(position)) Discard(discarded);
            history.RemoveRange(position, history.Count - position);
        }
        history.Add(plan); position++;
        if (plan.GetAuthoredAssets != null) plan.AuthoredAssets = new Dictionary<AssetId, Asset>();
        if (history.Count > 256) { Discard(history[0]); history.RemoveAt(0); position--; }
        TrackPendingFiles(plan, true); DesiredSelection = []; plan.Workspace.RefreshAssets();
    }
    public void Undo() { if (sessionHistory != null) { sessionHistory.Undo(); return; } if (CanUndo && Roll(history[position - 1], false)) { position--; DesiredSelection = history[position].Deleted.Select(target => target.Item.Id).ToArray(); } }
    public void Redo() { if (sessionHistory != null) { sessionHistory.Redo(); return; } if (CanRedo && Roll(history[position], true)) { position++; DesiredSelection = []; } }
    public GroupedHistoryOwnership GetOwnership(IEditorDocument? document, bool redo)
    {
        if (sessionHistory != null) return GroupedHistoryOwnership.None;
        if (document is not IAssetDeletionDocument authored) return GroupedHistoryOwnership.None;
        var operation = redo ? authored.NextRedoOperationId : authored.NextUndoOperationId;
        if (operation == null) return GroupedHistoryOwnership.None;
        for (var index = 0; index < history.Count; index++)
            if (history[index].References.Any(edit => ReferenceEquals(edit.Document, authored) && edit.Edit.OperationId == operation))
                return (redo ? index == position && CanRedo : index == position - 1 && CanUndo) ? GroupedHistoryOwnership.Ready : GroupedHistoryOwnership.Blocked;
        return orphaned.TryGetValue(authored, out var ids) && ids.Contains(operation.Value) ? GroupedHistoryOwnership.Blocked : GroupedHistoryOwnership.None;
    }
    public IDisposable LockPendingDeletionFiles()
    {
        var locks = new FileLocks();
        try
        {
            foreach (var (path, fingerprint) in pendingFingerprints)
                if (File.Exists(path))
                {
                    var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                    locks.Streams.Add(stream);
                    if (!SHA256.HashData(stream).AsSpan().SequenceEqual(fingerprint))
                        throw new IOException("A deleted asset changed on disk. Reload the project before saving.");
                }
            return locks;
        }
        catch { locks.Dispose(); throw; }
    }
    public void RemoveDocument(IEditorDocument document)
    {
        if (sessionHistory != null) return;
        if (history.Any(plan => plan.References.Any(edit => ReferenceEquals(edit.Document, document))))
        {
            foreach (var plan in history) Discard(plan);
            history.Clear(); position = 0;
        }
        if (document is IAssetDeletionDocument authored) orphaned.Remove(authored);
    }
    public void NotifySaved()
    { foreach (var path in pendingFingerprints.Keys.Where(path => !File.Exists(path)).ToArray()) pendingFingerprints.Remove(path); }
    public bool TryRemoveOwnedFile(string path)
    {
        path = Path.GetFullPath(path);
        if (!pendingFingerprints.TryGetValue(path, out var fingerprint)) return false;
        WindowsOwnedAssetFileDeletion.Delete(path, fingerprint); return true;
    }
    public void Clear() { foreach (var plan in history) Discard(plan); history.Clear(); orphaned.Clear(); pendingFingerprints.Clear(); position = 0; DesiredSelection = []; }
    private void Discard(Plan plan)
    {
        foreach (var edit in plan.References)
        { if (!orphaned.TryGetValue(edit.Document, out var ids)) orphaned.Add(edit.Document, ids = []); ids.Add(edit.Edit.OperationId); }
        var detached = plan.Deleted.Where(target => target.Detached).Select(target => target.Document).ToArray();
        if (detached.Length > 0)
        {
            plan.ForgetDocuments(detached);
            foreach (var document in detached) { if (document is IAssetDeletionDocument authored) orphaned.Remove(authored); document.Dispose(); }
        }
    }
    private static bool CanApply(Plan plan, bool deleting, bool references = true) => plan.Deleted.All(target => target.Document.EditRevision == target.Revision
        && (deleting ? target.Item.Package.Assets.Find(target.Item.Id)?.Location == target.Item.Location : target.Item.Package.Assets.Find(target.Item.Id) == null))
        && (!references || plan.References.All(edit => edit.Document.EditRevision == (deleting ? edit.Edit.BeforeRevision : edit.Edit.AfterRevision)
            && (deleting ? edit.Document.NextRedoOperationId : edit.Document.NextUndoOperationId) == edit.Edit.OperationId))
        && RootsMatch(plan, deleting);
    private static bool RootsMatch(Plan plan, bool deleting)
    {
        var ids = plan.Deleted.Select(target => target.Item.Id).ToHashSet();
        var roots = plan.Workspace.Session.LocalPackages.SelectMany(package => package.RootAssets.Where(root => ids.Contains(root.Id))
            .Select(root => new RootChange(package, root))).ToArray();
        return deleting ? roots.Length == plan.Roots.Length && roots.All(root => plan.Roots.Any(expected =>
            ReferenceEquals(expected.Package, root.Package) && expected.Reference.Id == root.Reference.Id && expected.Reference.Location == root.Reference.Location)) : roots.Length == 0;
    }
    private bool Roll(Plan plan, bool deleting, bool references = true)
    {
        if (references)
        {
            foreach (var edit in plan.References) edit.Document.CommitEdits();
            foreach (var target in plan.Deleted) target.Document.CommitEdits();
        }
        if (!CanApply(plan, deleting, references)) return false;
        var changedTargets = new List<Target>(); var changedReferences = new List<ReferenceChange>(); var changedHost = false; var rootsChanged = false;
        var createdFiles = new List<string>();
        try
        {
            if (!deleting) RestoreFiles(plan, createdFiles);
            else
            {
                var ids = plan.Deleted.Select(target => target.Item.Id).ToHashSet();
                var expectedConsumers = plan.References.Select(reference => reference.Document.AuthoredAsset.Id).ToHashSet();
                var current = plan.Workspace.Session.DependencyManager.CaptureDependencies(plan.GetAuthoredAssets?.Invoke() ?? plan.AuthoredAssets);
                if (!plan.PreserveReferences && ids.Any(id => current.GetValueOrDefault(id)?.LinksIn.Any(link => (link.Type & ContentLinkType.Reference) != 0
                    && !ids.Contains(link.Item.Id) && !expectedConsumers.Contains(link.Item.Id)) == true))
                    throw new InvalidOperationException("The asset references changed. Select the assets again before deleting.");
                foreach (var target in plan.Deleted) target.Bytes = ReadOwnedFile(target.Document);
            }
            foreach (var target in plan.Deleted) { if (deleting) RemoveTarget(target); else RestoreTarget(target); changedTargets.Add(target); }
            if (references) foreach (var edit in deleting ? plan.References : plan.References.Reverse())
            { if (deleting) edit.Document.Redo(); else edit.Document.Undo(); changedReferences.Add(edit); }
            rootsChanged = true; ApplyRoots(plan, deleting); plan.Workspace.RefreshAssets(); plan.SetDocuments(Documents(plan), deleting); changedHost = true;
            foreach (var target in plan.Deleted) target.Detached = deleting;
            foreach (var metadata in plan.Metadata) metadata.Apply(deleting);
        }
        catch (Exception failure)
        {
            var errors = new List<Exception> { failure };
            void Restore(Action action) { try { action(); } catch (Exception error) { errors.Add(error); } }
            foreach (var target in changedTargets.AsEnumerable().Reverse()) Restore(() => { if (deleting) RestoreTarget(target); else RemoveTarget(target); });
            foreach (var edit in changedReferences.AsEnumerable().Reverse()) Restore(() => { if (deleting) edit.Document.Undo(); else edit.Document.Redo(); });
            if (rootsChanged) Restore(() => ApplyRoots(plan, !deleting));
            Restore(plan.Workspace.RefreshAssets);
            if (changedHost) Restore(() => plan.SetDocuments(Documents(plan), !deleting));
            foreach (var file in createdFiles)
            {
                Restore(() =>
                {
                    var target = plan.Deleted.Single(target => string.Equals(target.Document.Path, file, StringComparison.OrdinalIgnoreCase));
                    WindowsOwnedAssetFileDeletion.Delete(file, SHA256.HashData(target.Bytes!));
                });
            }
            Restore(plan.Workspace.RefreshAssets);
            if (errors.Count > 1) throw new AggregateException("The deletion failed and could not be fully restored.", errors);
            throw;
        }
        TrackPendingFiles(plan, deleting); plan.Workspace.RefreshAssets(); return true;
    }
    private sealed class DeletionOperation(AssetDeletionHistory owner, Plan plan) : Operation
    {
        protected override void Rollback() => Replay(false);
        protected override void Rollforward() => Replay(true);
        private void Replay(bool deleting)
        {
            if (!owner.Roll(plan, deleting, false)) throw new InvalidOperationException("The deleted assets changed.");
            owner.DesiredSelection = deleting ? [] : plan.Deleted.Select(target => target.Item.Id).ToArray();
        }
        protected override void FreezeContent()
        {
            if (plan.Forgotten) return;
            var detached = plan.Deleted.Where(target => target.Detached).Select(target => target.Document).ToArray();
            if (detached.Length > 0) plan.ForgetDocuments(detached);
            plan.Forgotten = true;
            // The actual composite's typed authoring leases own graph disposal.
            // Pending file ownership remains until the package save succeeds.
        }
    }
    private void TrackPendingFiles(Plan plan, bool deleting)
    {
        foreach (var target in plan.Deleted)
            if (deleting && target.Bytes != null) pendingFingerprints[target.Document.Path] = SHA256.HashData(target.Bytes);
            else pendingFingerprints.Remove(target.Document.Path);
    }
    private static void RemoveTarget(Target target)
    {
        if (!target.Item.Package.Assets.RemoveById(target.Item.Id)) throw new InvalidOperationException("The asset no longer exists.");
        target.Item.IsDeleted = true; target.Item.Package.IsDirty = true;
    }
    private static void RestoreTarget(Target target)
    { target.Item.IsDeleted = false; target.Item.Package.Assets.Add(target.Item); target.Item.IsDirty = true; target.Item.Package.IsDirty = true; }
    private static void ApplyRoots(Plan plan, bool deleting)
    {
        foreach (var root in plan.Roots)
        { root.Package.RootAssets.Remove(root.Reference.Id); if (!deleting) root.Package.RootAssets.Add(root.Reference); root.Package.IsDirty = true; }
    }
    private static IAssetReferenceDocument[] Documents(Plan plan) => plan.Deleted.Select(target => target.Document).ToArray();
    private static byte[]? ReadOwnedFile(IAssetReferenceDocument document)
    {
        if (document is not IRelocatableEditorDocument owned) throw new InvalidOperationException("This asset cannot be deleted.");
        if (!File.Exists(document.Path) && !Directory.Exists(document.Path) && owned.DiskContentHash.IsEmpty) return null;
        using var stream = new FileStream(document.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var bytes = new MemoryStream(); stream.CopyTo(bytes); var data = bytes.ToArray();
        if (!SHA256.HashData(data).AsSpan().SequenceEqual(owned.DiskContentHash.Span)) throw new IOException("The asset changed on disk. Reload it before deleting it.");
        return data;
    }
    private static void RestoreFiles(Plan plan, List<string> created)
    {
        foreach (var target in plan.Deleted)
        {
            var path = target.Document.Path;
            if (target.Bytes == null)
            {
                if (File.Exists(path) || Directory.Exists(path)) throw new IOException("The unsaved asset path is occupied.");
                continue;
            }
            if (File.Exists(path))
            {
                if (!SHA256.HashData(File.ReadAllBytes(path)).AsSpan().SequenceEqual(SHA256.HashData(target.Bytes)))
                    throw new IOException("The deleted asset path is occupied by another file.");
                continue;
            }
            if (Directory.Exists(path)) throw new IOException("The deleted asset path is occupied by a directory.");
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(temporary, target.Bytes); File.Move(temporary, path); created.Add(path); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
    private sealed class FileLocks : IDisposable
    {
        public List<FileStream> Streams { get; } = [];
        public void Dispose() { foreach (var stream in Streams) stream.Dispose(); }
    }
    private sealed class TargetRetention : IDisposable
    {
        private readonly List<IDisposable> leases = [];
        public TargetRetention(IEnumerable<EditorAuthoringState> targets)
        {
            try { foreach (var target in targets) leases.Add(target.AcquireHistory()); }
            catch { Dispose(); throw; }
        }
        public void Dispose() { foreach (var lease in leases) lease.Dispose(); leases.Clear(); }
    }
}
