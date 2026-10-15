// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Stride.Core.Transactions;

namespace Stride.Editor.ImGui;

/// <summary>One operation containing an ordered set of already applied operations.</summary>
/// <remarks>Each child must restore its own partial changes before throwing during replay.</remarks>
internal sealed class CompositeOperation : Operation, IReadOnlyTransaction
{
    private Operation[] operations;

    public CompositeOperation(IEnumerable<Operation> operations)
    {
        ArgumentNullException.ThrowIfNull(operations);
        this.operations = operations.ToArray();
        if (this.operations.Any(operation => operation == null))
            throw new ArgumentException("A composite cannot contain null operations.", nameof(operations));
        if (this.operations.Distinct().Count() != this.operations.Length)
            throw new ArgumentException("A composite cannot replay the same operation twice.", nameof(operations));
    }

    public override bool HasEffect => operations.Any(operation => operation.HasEffect);
    public IReadOnlyList<Operation> Operations => operations;
    public TransactionFlags Flags => TransactionFlags.None;

    /// <summary>Restores already applied captured operations when preparation cannot publish them.</summary>
    public void RestoreApplied() => Interface.Rollback();

    protected override void Rollback() => Replay(false);
    protected override void Rollforward() => Replay(true);

    private void Replay(bool forward)
    {
        var completed = new List<Operation>();
        try
        {
            foreach (var operation in forward ? operations : operations.Reverse())
            {
                if (forward) operation.Interface.Rollforward();
                else operation.Interface.Rollback();
                completed.Add(operation);
            }
        }
        catch (Exception error)
        {
            var failures = new List<Exception> { error };
            foreach (var operation in completed.AsEnumerable().Reverse())
            {
                try
                {
                    if (forward) operation.Interface.Rollback();
                    else operation.Interface.Rollforward();
                }
                catch (Exception compensationError) { failures.Add(compensationError); }
            }
            if (failures.Count > 1) throw new AggregateException("Composite replay and compensation failed.", failures);
            throw;
        }
    }

    protected override void FreezeContent()
    {
        List<Exception>? failures = null;
        foreach (var operation in operations)
        {
            if (operation.IsFrozen) continue;
            try { operation.Interface.Freeze(); }
            catch (Exception error) { (failures ??= []).Add(error); }
        }
        if (failures != null) throw new AggregateException("Composite resource release failed.", failures);
        operations = [];
    }
}
