using Stride.Core.Annotations;
using Stride.Core.Assets.Quantum;
using Stride.Core.Presentation.Quantum.Presenters;
using Stride.Core.Quantum;

namespace Stride.Editor.ImGui;

internal sealed partial class ProceduralAssetDocument
{
    private VirtualNodePresenter? activeVirtual;
    private EditorPresenterTarget? activeVirtualTarget;
    private object? virtualBefore, virtualPending;
    private bool committingVirtual;
    private readonly List<EditorPresenterTarget> virtualCleanup = [];
    private readonly List<EditorDeferredAssetMutation> virtualMutationCleanup = [];
    internal IReadOnlyList<VirtualNodePresenter> VirtualProperties => NumericPresentation?.VirtualRows ?? [];
    internal INodePresenter? PresentationRoot => NumericPresentation?.RootPresenter;
    private bool HasVirtualChange => activeVirtual != null && !ContentAssetReference.AreEqual(virtualBefore, virtualPending);
    internal object? ReadVirtualProperty(VirtualNodePresenter presenter)
        => ReferenceEquals(activeVirtual, presenter) ? virtualPending : presenter.Value;
    internal PropertyNumericRange? VirtualNumericRange(INodePresenter presenter) => NumericPresentation?.NumericRange(presenter);
    internal bool CanCopyVirtualProperty(VirtualNodePresenter presenter)
    {
        if (!VirtualProperties.Contains(presenter)) return false;
        var accessor = presenter.GetNodeAccessor();
        return accessor.Node is IMemberNode member ? CanCopyProperty(member)
            : accessor.Node is IAssetObjectNode collection && authoringNodes.Contains(collection) && !accessor.Index.IsEmpty
                && (collection.Indices ?? []).Contains(accessor.Index);
    }
    internal string CopyVirtualProperty(VirtualNodePresenter presenter)
    {
        EndEdit();
        if (!CanCopyVirtualProperty(presenter)) throw new InvalidOperationException("The virtual property has no canonical accessor.");
        return PropertyClipboard().CopyValue(presenter.Value, presenter.GetNodeAccessor());
    }
    private bool NativeVirtualClipboardTarget(VirtualNodePresenter presenter, out NodeAccessor accessor, out Type targetType)
    {
        accessor = presenter.GetNodeAccessor(); targetType = presenter.Type;
        if (accessor.Node is IMemberNode member && member.Type == presenter.Type && member.Target is { IsEnumerable: true })
            return true;
        if (accessor.Node is IAssetObjectNode owner && !accessor.Index.IsEmpty
            && PropertyClipboardRules.ElementType(owner) == presenter.Type)
        { targetType = owner.Type; return true; }
        return false;
    }
    private bool CanMutateVirtualClipboardTarget(NodeAccessor accessor)
        => accessor.Node is IMemberNode member && member.Target is { IsEnumerable: true } collection
            ? CanEditNativeCollection(collection) && !PropertyClipboardRules.IsReadOnly(member)
            : accessor.Node is IAssetObjectNode owner && !accessor.Index.IsEmpty
                && (owner.Indices ?? []).Contains(accessor.Index) && CanEditNativeCollectionItem(owner, accessor.Index);
    internal bool CanPasteVirtualProperty(VirtualNodePresenter presenter, string text)
        => CanCopyVirtualProperty(presenter) && (presenter.IsEnumerable || !presenter.IsReadOnly)
            && !presenter.MemberAttributes.OfType<MemberCollectionAttribute>().Any(attribute => attribute.ReadOnly)
            && (NativeVirtualClipboardTarget(presenter, out var accessor, out var targetType)
            ? CanMutateVirtualClipboardTarget(accessor) && PropertyClipboard().CanPaste(text, targetType)
            : CanEditVirtualProperty(presenter) && PropertyClipboard().CanPaste(text, presenter.Type));
    internal void PasteVirtualProperty(VirtualNodePresenter presenter, string text, bool replace)
    {
        EndEdit();
        if (!CanPasteVirtualProperty(presenter, text)) throw new InvalidOperationException("The clipboard is incompatible with the virtual property.");
        if (NativeVirtualClipboardTarget(presenter, out var accessor, out var targetType))
        {
            var clipboard = PropertyClipboard();
            ApplyInheritedAssetMutation(() => clipboard.Paste(text, targetType, accessor, replace), replace ? "Replace property" : "Paste property");
        }
        else { BufferVirtualProperty(presenter, PropertyClipboard().PrepareValue(text, presenter.Type)); EndVirtualEdit(); }
    }
    internal bool CanEditVirtualProperty(VirtualNodePresenter presenter)
    {
        if (!VirtualProperties.Contains(presenter)) return false;
        for (INodePresenter? current = presenter; current != null; current = current.Parent) if (current.IsReadOnly) return false;
        var accessor = presenter.GetNodeAccessor();
        return accessor.Node is IMemberNode member ? CanEditAssetSettingNode(member) && !NumericReadOnly(member)
            : accessor.Node is IAssetObjectNode collection && !accessor.Index.IsEmpty
                && (collection.Indices ?? []).Contains(accessor.Index) && CanEditNativeCollectionItem(collection, accessor.Index);
    }
    internal void BufferVirtualProperty(VirtualNodePresenter presenter, object? value)
    {
        if (!CanEditVirtualProperty(presenter)) throw new InvalidOperationException("The virtual procedural property is read-only or sealed.");
        var type = Nullable.GetUnderlyingType(presenter.Type) ?? presenter.Type;
        if (value == null ? presenter.Type.IsValueType && Nullable.GetUnderlyingType(presenter.Type) == null
            || presenter.MemberAttributes.OfType<NotNullAttribute>().Any()
            || presenter.MemberAttributes.OfType<System.Diagnostics.CodeAnalysis.NotNullAttribute>().Any()
            : !type.IsInstanceOfType(value)) throw new ArgumentException("The virtual editor value is incompatible.");
        if (!ReferenceEquals(activeVirtual, presenter))
        {
            EndEdit();
            activeVirtualTarget = AcquirePresenterTarget(presenter);
            activeVirtual = presenter; virtualBefore = presenter.Value;
        }
        virtualPending = value;
    }
    internal void EndVirtualEdit()
    {
        DrainVirtualMutationCleanup();
        if (activeVirtual == null || committingVirtual) return;
        var presenter = activeVirtual;
        committingVirtual = true;
        try
        {
            if (HasVirtualChange)
            {
                if (!CanEditVirtualProperty(presenter) || !ContentAssetReference.AreEqual(virtualBefore, presenter.Value))
                    throw new InvalidOperationException("The virtual procedural property's authored source changed.");
                var value = PropertyValueValidation.Normalize(virtualPending, VirtualNumericRange(presenter));
                var ranges = NativeNumericAxes.Names(presenter.Type).Select(name =>
                    presenter.TryGetChild(name) is { } child ? VirtualNumericRange(child) : null).ToArray();
                value = NativeNumericAxes.Normalize(value, presenter.Value, ranges);
                NativeNumericAxes.AssertEditable(presenter, presenter.Value, value);
                var mutation = BeginExtensionCommandMutation();
                try
                {
                    presenter.UpdateValue(value!);
                    mutation.Commit("Edit " + presenter.DisplayName);
                    // A completed journal must never be replayed after a disposal failure.
                    virtualBefore = virtualPending = presenter.Value;
                }
                finally { try { mutation.Dispose(); } catch { virtualMutationCleanup.Add(mutation); throw; } }
            }
            var target = activeVirtualTarget;
            activeVirtual = null; activeVirtualTarget = null; virtualBefore = virtualPending = null;
            if (target != null) try { target.Dispose(); } catch { virtualCleanup.Add(target); throw; }
        }
        finally { committingVirtual = false; }
    }
    private void CancelVirtualEdit()
    {
        if (activeVirtualTarget != null)
        {
            var target = activeVirtualTarget;
            activeVirtualTarget = null; activeVirtual = null; virtualBefore = virtualPending = null;
            try { target.Dispose(); } catch { virtualCleanup.Add(target); throw; }
        }
        foreach (var target in virtualCleanup.ToArray()) { target.Dispose(); virtualCleanup.Remove(target); }
        DrainVirtualMutationCleanup();
    }
    private void DrainVirtualMutationCleanup()
    { foreach (var mutation in virtualMutationCleanup.ToArray()) { mutation.Dispose(); virtualMutationCleanup.Remove(mutation); } }
}
