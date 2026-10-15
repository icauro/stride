// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Stride.Core.Transactions;

namespace Stride.Editor.ImGui;

/// <summary>Records an actual operation while retaining the resources it needs until the stack discards it.</summary>
internal sealed class RetainedOperation : Operation, IReadOnlyTransaction
{
    private readonly Operation operation;
    private readonly IDisposable[] resources;
    public RetainedOperation(Operation operation, IEnumerable<IDisposable> resources)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(resources);
        this.operation = operation; this.resources = resources.ToArray(); Operations = [operation];
    }
    public IReadOnlyList<Operation> Operations { get; }
    public TransactionFlags Flags => TransactionFlags.None;
    public override bool HasEffect => operation.HasEffect;
    protected override void Rollback() => operation.Interface.Rollback();
    protected override void Rollforward() => operation.Interface.Rollforward();
    protected override void FreezeContent()
    {
        if (!operation.IsFrozen) operation.Interface.Freeze();
        List<Exception>? errors = null;
        foreach (var resource in resources)
            try { resource.Dispose(); }
            catch (Exception error) { (errors ??= []).Add(error); }
        if (errors != null) throw new AggregateException(errors);
    }
}
