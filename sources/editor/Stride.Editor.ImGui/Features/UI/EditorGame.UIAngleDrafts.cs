using Stride.Core.Mathematics;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawRetainedUIAngle(UIDocument source, object identity, Func<bool> canEdit,
        Func<object?[]> read, Action<AngleSingle> commit, int valueCount, Func<IDisposable>? acquireTarget)
    {
        bool Editable() => ReferenceEquals(ownedUIEditor?.Document, source) && CanStartAssetEdit
            && sessionHistory?.Registry.Find(source.Asset.Id) is { IsPublished: true, IsDisposed: false } owner
            && ReferenceEquals(owner.Document, source) && canEdit();
        DrawRetainedAngle(identity, read, Editable, commit, () =>
        {
            if (!Editable()) throw new InvalidOperationException("The angle property owner is no longer current.");
            var leases = new List<IDisposable>();
            try
            {
                leases.Add(sessionHistory!.Registry.Find(source.Asset.Id)!.AcquireView());
                if (acquireTarget != null) leases.Add(acquireTarget());
                return new PropertyStringOwners(leases, [source]);
            }
            catch { foreach (var lease in leases.AsEnumerable().Reverse()) lease.Dispose(); throw; }
        }, valueCount);
    }
}
