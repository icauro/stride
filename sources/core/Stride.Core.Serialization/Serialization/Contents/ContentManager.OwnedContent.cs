using System.Linq;

namespace Stride.Core.Serialization.Contents;

partial class ContentManager
{
    /// <summary>
    /// Releases all public references owned by this cache and collects its
    /// unreferenced cycles. Call only after its loading/using scopes have drained.
    /// GPU content still requires the caller's actual device completion proof.
    /// </summary>
    public void UnloadAll()
    {
        lock (LoadedAssetUrls)
        {
            foreach (var reference in LoadedAssetReferences.Values.ToArray())
                while (reference.Object != null && reference.PublicReferenceCount > 0)
                    DecrementReference(reference, true);
            // A failed loader/reload rollback may already have requested a
            // private final release. Resume that same recorded category, never
            // manufacture a public reference or decrement an unrelated edge.
            foreach (var reference in LoadedAssetReferences.Values.ToArray())
                if (reference.Object != null && reference.ReleasePending)
                    DecrementReference(reference, reference.ReleasePublicReference);
            CollectUnreferencedCycles();
            if (LoadedAssetReferences.Count != 0 || LoadedAssetUrls.Count != 0)
                throw new InvalidOperationException("Owned content still has unresolved references after cleanup.");
        }
    }
}
