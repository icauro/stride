using Stride.Core.Assets;
using Stride.Core.Assets.Analysis;
using Stride.Core.Assets.Tracking;
using Stride.Core.IO;
using Stride.Core.Serialization.Contents;
using Stride.Core.Transactions;

namespace Stride.Editor.ImGui;

/// <summary>Prepared ID-based URL and source-path updates owned by an authored document.</summary>
internal sealed class PreparedAssetReferenceEdit
{
    private readonly ReferenceOperation? operation;
    private readonly Func<Guid>? nativeApply;
    private readonly Func<long>? nativeRevision;
    private readonly bool nativeHasChanges;
    private Guid? nativeOperationId;
    private readonly Action<Operation> record;
    private readonly Action undoRevision;
    private bool applied;
    public Guid OperationId => nativeOperationId ?? operation?.Id ?? throw new InvalidOperationException("The prepared reference edit has not been applied.");
    public long BeforeRevision { get; }
    public long AfterRevision { get; private set; }
    public bool HasChanges => operation?.HasChanges ?? nativeHasChanges;
    internal PreparedAssetReferenceEdit(long beforeRevision, bool hasChanges, Func<long> currentRevision, Func<Guid> apply)
    {
        BeforeRevision = AfterRevision = beforeRevision; nativeHasChanges = hasChanges;
        nativeRevision = currentRevision; nativeApply = apply;
        record = _ => { }; undoRevision = () => { };
    }
    public PreparedAssetReferenceEdit(Asset asset, IReadOnlyDictionary<AssetId, string> locations,
        string? previousPath, string? nextPath, long beforeRevision, long afterRevision,
        Action<Operation> record, Action undoRevision, Action redoRevision)
    {
        BeforeRevision = beforeRevision; AfterRevision = afterRevision;
        this.record = record; this.undoRevision = undoRevision;
        operation = new ReferenceOperation(asset, locations, previousPath, nextPath, undoRevision, redoRevision);
    }
    public void Apply()
    {
        if (nativeApply != null) { nativeOperationId = nativeApply(); AfterRevision = nativeRevision!(); }
        else operation!.Apply(true);
        applied = true;
    }
    public void Commit() { if (operation != null) record(operation); }
    public void Restore()
    {
        // Native journals belong to the enclosing atomic relocation capture.
        if (operation == null) return;
        if (applied) operation.Apply(false); undoRevision(); operation.Cancelled = true;
    }

    private sealed class ReferenceOperation : Operation
    {
        private sealed record Change(AssetReferenceLink Link, AssetId? Id, string Before, string After);
        private readonly List<Change> changes = [];
        private readonly Asset asset;
        private readonly string? previousPath, nextPath;
        private readonly Action undoRevision, redoRevision;
        public bool Cancelled { get; set; }
        public bool HasChanges => changes.Count > 0 || previousPath != null && nextPath != null
            && !string.Equals(Path.GetDirectoryName(previousPath), Path.GetDirectoryName(nextPath), StringComparison.OrdinalIgnoreCase);
        public override bool HasEffect => !Cancelled && HasChanges;
        public ReferenceOperation(Asset asset, IReadOnlyDictionary<AssetId, string> locations,
            string? previousPath, string? nextPath, Action undoRevision, Action redoRevision)
        {
            this.asset = asset; this.previousPath = previousPath; this.nextPath = nextPath;
            this.undoRevision = undoRevision; this.redoRevision = redoRevision;
            foreach (var link in AssetReferenceAnalysis.Visit(asset))
            {
                if (link.Reference is IReference reference && locations.TryGetValue(reference.Id, out var location)
                    && !string.Equals(reference.Location, location, StringComparison.Ordinal))
                    changes.Add(new Change(link, reference.Id, reference.Location, location));
                else if (previousPath != null && nextPath != null && link.Reference is UPath source
                    && !source.IsAbsolute && !string.IsNullOrEmpty(source.FullPath)
                    && link.Path.GetCustomAttribute<UPathAttribute>()?.RelativeTo != UPathRelativeTo.None)
                {
                    var absolute = Path.GetFullPath(source.ToOSPath(), Path.GetDirectoryName(previousPath)!);
                    var rebased = Path.GetRelativePath(Path.GetDirectoryName(nextPath)!, absolute).Replace('\\', '/');
                    if (!string.Equals(source.FullPath, rebased, StringComparison.Ordinal)) changes.Add(new Change(link, null, source.FullPath, rebased));
                }
            }
        }
        public void Apply(bool forward)
        {
            var applied = new List<Change>();
            try
            {
                foreach (var change in changes)
                { change.Link.UpdateReference(change.Id, forward ? change.After : change.Before); applied.Add(change); }
                if (previousPath != null && nextPath != null)
                    SourceHashesHelper.RebaseRelativePaths(asset,
                        new UDirectory(Path.GetDirectoryName(forward ? previousPath : nextPath)!),
                        new UDirectory(Path.GetDirectoryName(forward ? nextPath : previousPath)!));
            }
            catch
            {
                foreach (var change in applied.AsEnumerable().Reverse()) change.Link.UpdateReference(change.Id, forward ? change.Before : change.After);
                throw;
            }
        }
        protected override void Rollback() { if (!Cancelled) { Apply(false); undoRevision(); } }
        protected override void Rollforward() { if (!Cancelled) { Apply(true); redoRevision(); } }
    }
}
