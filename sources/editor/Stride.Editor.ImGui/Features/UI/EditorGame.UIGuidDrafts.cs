namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawRetainedUIGuid(UIDocument source, object identity, Func<bool> canEdit,
        Func<object?[]> read, Action<Guid> commit, int valueCount, Func<IDisposable>? acquireTarget)
    {
        bool Editable() => ReferenceEquals(ownedUIEditor?.Document, source) && CanStartAssetEdit
            && sessionHistory?.Registry.Find(source.Asset.Id) is { IsPublished: true, IsDisposed: false } owner
            && ReferenceEquals(owner.Document, source) && canEdit();
        DrawRetainedGuid(identity, read, Editable, commit,
            () => AcquireUIValueDraftOwners(source, Editable, acquireTarget), valueCount);
    }
    private IDisposable AcquireUIValueDraftOwners(UIDocument source, Func<bool> canEdit, Func<IDisposable>? acquireTarget)
    {
        if (!canEdit()) throw new InvalidOperationException("The property owner is no longer current.");
        var leases = new List<IDisposable>();
        try
        {
            leases.Add(sessionHistory!.Registry.Find(source.Asset.Id)!.AcquireView());
            if (acquireTarget != null) leases.Add(acquireTarget());
            return new PropertyStringOwners(leases, [source]);
        }
        catch { foreach (var lease in leases.AsEnumerable().Reverse()) lease.Dispose(); throw; }
    }
}
