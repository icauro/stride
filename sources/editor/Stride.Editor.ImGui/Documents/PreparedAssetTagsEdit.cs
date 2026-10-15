using Stride.Core.Assets;
using Stride.Core.Transactions;

namespace Stride.Editor.ImGui;

/// <summary>Validated tag edit; a batch applies every payload before recording history.</summary>
internal sealed class PreparedAssetTagsEdit(Asset asset, string[] before, string[] after, long beforeRevision, long afterRevision,
    Action<Operation> record, Action undoRevision, Action redoRevision)
{
    private AssetTagsOperation? operation;
    private Func<Guid>? nativeApply;
    private Func<long>? nativeRevision;
    private Guid? nativeOperationId;
    public Guid? OperationId => operation?.Id ?? nativeOperationId;
    public string[] Before { get; } = before;
    public string[] After { get; } = after;
    public long BeforeRevision { get; } = beforeRevision;
    public long AfterRevision { get; private set; } = afterRevision;
    internal PreparedAssetTagsEdit(Asset asset, string[] before, string[] after, long beforeRevision,
        Func<long> currentRevision, Func<Guid> apply)
        : this(asset, before, after, beforeRevision, beforeRevision, _ => { }, () => { }, () => { })
    { nativeApply = apply; nativeRevision = currentRevision; }
    public void Apply()
    {
        if (nativeApply != null) { nativeOperationId = nativeApply(); AfterRevision = nativeRevision!(); }
        else AssetTagsOperation.Apply(asset, After);
    }
    public void Restore()
    {
        // Actual native operations are restored by the enclosing atomic session capture.
        if (nativeApply != null) return;
        AssetTagsOperation.Apply(asset, Before); undoRevision();
        if (operation != null) operation.Cancelled = true;
    }
    public void Commit()
    {
        if (nativeApply != null) return;
        operation = new AssetTagsOperation(asset, Before, After, undoRevision, redoRevision);
        record(operation);
    }
}
