using Stride.Core.Assets.Quantum;
using Stride.Core.Presentation.Quantum.Presenters;
using Stride.Core.Quantum;
using Stride.Engine;

namespace Stride.Editor.ImGui;

internal abstract partial class EntityHierarchyDocument
{
    private VirtualNodePresenter? activeComponentVirtual;
    private object? componentVirtualBefore, componentVirtualPending;
    private EditorPresenterTarget? componentVirtualTarget;
    private readonly List<EditorPresenterTarget> retiringComponentVirtualTargets = [];
    private bool HasComponentVirtualChange => activeComponentVirtual != null && !ContentAssetReference.AreEqual(componentVirtualBefore, componentVirtualPending);
    internal bool CanEditComponentVirtual(VirtualNodePresenter presenter)
    {
        var presentation = propertyPresenters.FirstOrDefault(value => value.OwnsVirtual(presenter));
        if (presentation?.AuthoredValue is not EntityComponent component || component.Entity is not { } entity || !IsSelected(entity.Id)
            || !Asset.Hierarchy.Parts.TryGetValue(entity.Id, out var design) || !ReferenceEquals(design.Entity, entity) || !entity.Components.Contains(component)) return false;
        for (INodePresenter? current = presenter; current != null; current = current.Parent) if (current.IsReadOnly) return false;
        var accessor = presenter.GetNodeAccessor();
        return accessor.Node is IMemberNode member ? CanEditHierarchyNode(member) && AssetMetadataDocument.CanAssignAuthoringNode(member)
            : accessor.Node is IAssetObjectNode collection && !accessor.Index.IsEmpty && CanEditHierarchyItem(collection, accessor.Index);
    }
    internal object? ReadComponentVirtual(VirtualNodePresenter presenter) => ReferenceEquals(activeComponentVirtual, presenter) ? componentVirtualPending : presenter.Value;
    internal void BufferComponentVirtual(VirtualNodePresenter presenter, object? value)
    {
        if (!CanEditComponentVirtual(presenter)) throw new InvalidOperationException("The component property is read-only or sealed.");
        var type = Nullable.GetUnderlyingType(presenter.Type) ?? presenter.Type;
        if (value == null ? presenter.Type.IsValueType && Nullable.GetUnderlyingType(presenter.Type) == null : !type.IsInstanceOfType(value))
            throw new ArgumentException("The component property value has an incompatible type.");
        if (!ReferenceEquals(activeComponentVirtual, presenter))
        {
            EndEdits();
            componentVirtualTarget = propertyPresenters.Single(value => value.OwnsVirtual(presenter)).AcquireVirtualTarget(presenter);
            activeComponentVirtual = presenter; componentVirtualBefore = presenter.Value;
        }
        componentVirtualPending = value;
    }
    internal void EndComponentVirtualEdit()
    {
        if (activeComponentVirtual == null) return;
        var presenter = activeComponentVirtual; var before = componentVirtualBefore; var after = componentVirtualPending;
        if (!ContentAssetReference.AreEqual(before, after))
        {
            if (!CanEditComponentVirtual(presenter) || !ContentAssetReference.AreEqual(presenter.Value, before))
                throw new InvalidOperationException("The component property changed before the edit could be committed.");
            var presentation = propertyPresenters.Single(value => value.OwnsVirtual(presenter));
            after = NormalizeComponentVirtualValue(presenter, after);
            activeComponentVirtual = null;
            try { ApplyInheritedGraphMutation(() => presenter.UpdateValue(after!), "Edit " + presenter.DisplayName); }
            catch { activeComponentVirtual = presenter; throw; }
        }
        var target = componentVirtualTarget;
        activeComponentVirtual = null; componentVirtualTarget = null; componentVirtualBefore = componentVirtualPending = null;
        if (target != null) try { target.Dispose(); } catch { retiringComponentVirtualTargets.Add(target); throw; }
    }
    internal void ApplyComponentVirtualValues(IReadOnlyList<VirtualNodePresenter> presenters, IReadOnlyList<object?> values)
    {
        EndEdits();
        if (presenters.Count == 0 || presenters.Count != values.Count || presenters.Any(node => !CanEditComponentVirtual(node)))
            throw new InvalidOperationException("The selected component virtual properties changed.");
        var changes = presenters.Select((node, index) => (Node: node, Value: NormalizeComponentVirtualValue(node, values[index]))).ToArray();
        ApplyInheritedGraphMutation(() =>
        {
            var written = new Dictionary<(IGraphNode?, NodeIndex, string), object?>();
            foreach (var change in changes)
            {
                if (!CanEditComponentVirtual(change.Node)) throw new InvalidOperationException("A selected component virtual property became read-only.");
                var accessor = change.Node.GetNodeAccessor(); var identity = (accessor.Node, accessor.Index, change.Node.CombineKey);
                if (written.TryGetValue(identity, out var prior))
                {
                    if (!ContentAssetReference.AreEqual(prior, change.Value)) throw new InvalidOperationException("Aliases of one actual virtual property cannot receive different values.");
                    continue;
                }
                written.Add(identity, change.Value); change.Node.UpdateValue(change.Value!);
            }
        }, "Edit " + presenters[0].DisplayName);
    }
    private object? NormalizeComponentVirtualValue(VirtualNodePresenter presenter, object? value)
    {
        var type = Nullable.GetUnderlyingType(presenter.Type) ?? presenter.Type;
        if (value == null ? presenter.Type.IsValueType && Nullable.GetUnderlyingType(presenter.Type) == null : !type.IsInstanceOfType(value))
            throw new ArgumentException("The component virtual value has an incompatible type.");
        if (value == null && (presenter.MemberAttributes.OfType<Stride.Core.Annotations.NotNullAttribute>().Any()
            || presenter.MemberAttributes.OfType<System.Diagnostics.CodeAnalysis.NotNullAttribute>().Any()))
            throw new ArgumentException("The component virtual property does not allow null.");
        if (ContentAssetReference.Supports(presenter.Type)) ContentAssetReference.Validate(presenter.Type, value, !presenter.MemberAttributes.OfType<Stride.Core.Annotations.NotNullAttribute>().Any()
            && !presenter.MemberAttributes.OfType<System.Diagnostics.CodeAnalysis.NotNullAttribute>().Any());
        if (SceneObjectReference.Supports(presenter.Type) && !ContainsSceneReference(presenter.Type, value))
            throw new ArgumentException("The scene reference must belong to this property's canonical document.");
        var presentation = propertyPresenters.Single(owner => owner.OwnsVirtual(presenter));
        NativeNumericAxes.AssertEditable(presenter, presenter.Value, value);
        return NativeNumericAxes.Normalize(PropertyValueValidation.Normalize(value, presentation.VirtualNumericRange(presenter)), presenter.Value, presentation.VirtualAxisNumericRanges(presenter));
    }
    private void ReleaseComponentVirtualTargets()
    {
        foreach (var target in retiringComponentVirtualTargets.ToArray()) { target.Dispose(); retiringComponentVirtualTargets.Remove(target); }
    }
}
