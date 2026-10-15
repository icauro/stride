namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private IDisposable AcquireRetainedProceduralNumber(ProceduralAssetDocument owner, Func<EditorPresenterTarget?> acquire)
    {
        var leases = new List<IDisposable>();
        try
        {
            if (!IsCurrentProceduralControlOwner(owner)) throw new InvalidOperationException("The procedural control owner changed.");
            leases.Add(AcquirePropertyStringOwner(owner));
            if (acquire() is { } target) leases.Add(target);
            return new PropertyStringOwners(leases, [owner]);
        }
        catch
        {
            foreach (var lease in leases) packageCommandCleanup.Add(lease.Dispose);
            throw;
        }
    }
}
