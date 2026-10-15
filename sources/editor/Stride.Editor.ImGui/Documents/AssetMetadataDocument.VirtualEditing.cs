using Stride.Core.Presentation.Quantum.Presenters;

namespace Stride.Editor.ImGui;

internal partial class AssetMetadataDocument
{
    private VirtualNodePresenter? activeExtensionVirtual;
    private EditorPresenterTarget? activeExtensionVirtualTarget;
    private object? extensionVirtualBefore, extensionVirtualPending;
    private readonly List<EditorPresenterTarget> pendingVirtualTargetCleanup = [];
    private bool HasExtensionVirtualChange => activeExtensionVirtual != null && !ContentAssetReference.AreEqual(extensionVirtualBefore, extensionVirtualPending);
    internal object? ReadExtensionVirtualProperty(VirtualNodePresenter presenter)
        => ReferenceEquals(activeExtensionVirtual, presenter) ? extensionVirtualPending : presenter.Value;
    internal void BufferExtensionVirtualProperty(VirtualNodePresenter presenter, object? value)
    {
        if (!CanEditExtensionVirtualProperty(presenter)) throw new InvalidOperationException("The virtual property's authored accessor is read-only or sealed.");
        var type = Nullable.GetUnderlyingType(presenter.Type) ?? presenter.Type;
        if (value == null ? presenter.Type.IsValueType && Nullable.GetUnderlyingType(presenter.Type) == null : !type.IsInstanceOfType(value))
            throw new ArgumentException("The virtual editor value has an incompatible type.");
        if (!ReferenceEquals(activeExtensionVirtual, presenter))
        {
            CommitEdits(); activeExtensionVirtualTarget = AcquireExtensionVirtualTarget(presenter);
            activeExtensionVirtual = presenter; extensionVirtualBefore = presenter.Value;
        }
        extensionVirtualPending = value;
    }
    internal void EndExtensionVirtualEdit()
    {
        if (activeExtensionVirtual == null) return;
        var presenter = activeExtensionVirtual; var before = extensionVirtualBefore; var after = extensionVirtualPending;
        var target = activeExtensionVirtualTarget;
        activeExtensionVirtual = null; activeExtensionVirtualTarget = null; extensionVirtualBefore = extensionVirtualPending = null;
        try { if (!ContentAssetReference.AreEqual(before, after)) UpdateExtensionVirtualProperty(presenter, after); }
        finally
        {
            if (target != null)
                try { target.Dispose(); }
                catch { pendingVirtualTargetCleanup.Add(target); throw; }
        }
    }
    private void ReleaseVirtualTargets()
    {
        foreach (var target in pendingVirtualTargetCleanup.ToArray()) { target.Dispose(); pendingVirtualTargetCleanup.Remove(target); }
    }
}
