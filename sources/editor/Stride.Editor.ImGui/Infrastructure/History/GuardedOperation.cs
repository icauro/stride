using Stride.Core.Transactions;

namespace Stride.Editor.ImGui;

/// <summary>Replays an actual operation inside its owner's restoration boundary.</summary>
internal sealed class GuardedOperation : Operation, IReadOnlyTransaction
{
    private readonly Operation operation;
    private readonly Action<bool, Action, Action> replay;
    public GuardedOperation(Operation operation, Action<bool, Action> replay) : this(operation, (forward, apply, _) => replay(forward, apply)) { }
    public GuardedOperation(Operation operation, Action<bool, Action, Action> replay)
    {
        ArgumentNullException.ThrowIfNull(operation); ArgumentNullException.ThrowIfNull(replay);
        this.operation = operation; this.replay = replay; Operations = [operation];
    }
    public IReadOnlyList<Operation> Operations { get; }
    public TransactionFlags Flags => TransactionFlags.None;
    public override bool HasEffect => operation.HasEffect;
    protected override void Rollback() => replay(false, operation.Interface.Rollback, operation.Interface.Rollforward);
    protected override void Rollforward() => replay(true, operation.Interface.Rollforward, operation.Interface.Rollback);
    protected override void FreezeContent() { if (!operation.IsFrozen) operation.Interface.Freeze(); }
}
