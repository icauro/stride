namespace Stride.Editor.ImGui;

internal sealed class ContentReferenceEditCertificate(Func<bool, bool> current, Action rebaseline, IReadOnlyList<IDisposable>? ownerLeases = null,
    Func<IReadOnlyList<IDisposable>>? acquireOwnerLeases = null) : IDisposable
{
    private readonly List<IDisposable> leases = ownerLeases?.ToList() ?? [];
    private bool retiring;
    internal bool IsCurrent(bool includeRevisions = true) => !retiring && current(includeRevisions);
    internal ContentReferenceEditCertificate Retain()
    {
        if (!IsCurrent()) throw new InvalidOperationException("The reference picker owner is no longer current.");
        if (acquireOwnerLeases == null) throw new InvalidOperationException("The reference picker has no canonical owner lease factory.");
        return new ContentReferenceEditCertificate(current, rebaseline, acquireOwnerLeases(), acquireOwnerLeases);
    }
    internal bool TryRebaselineAfterAuthorizedMutation()
    {
        if (!IsCurrent(false)) return false;
        rebaseline();
        return IsCurrent();
    }
    public void Dispose()
    {
        retiring = true;
        List<Exception>? failures = null;
        for (var index = leases.Count - 1; index >= 0; index--)
        {
            try { leases[index].Dispose(); leases.RemoveAt(index); }
            catch (Exception error) { (failures ??= []).Add(error); }
        }
        if (failures != null) throw new AggregateException("Reference picker owner cleanup is incomplete.", failures);
    }
}
