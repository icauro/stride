using Stride.Core.Quantum;
using Stride.Core.Presentation.Quantum.Presenters;

namespace Stride.Editor.ImGui;

internal sealed partial class UIDocument
{
    private readonly Dictionary<Guid, EditorAssetPropertyPresentation> elementPresentations = [];
    private readonly List<EditorAssetPropertyPresentation> retiringElementPresentations = [];
    private long elementPresentationVersion = -1;
    internal void SelectExtensionElements(IReadOnlyCollection<Guid> ids)
    {
        DrainUIRenderTargets();
        var extensions = SessionHistory?.Extensions;
        var active = extensions != null ? ids.Where(id => Find(id) != null).ToHashSet() : [];
        bool Changed(Guid id) => !active.Contains(id) || elementPresentationVersion != extensions?.Version
            || !ReferenceEquals(elementPresentations[id].AuthoredRoot, ObjectNode(Require(id)));
        if (elementPresentations.Keys.Any(Changed)) EndExtensionVirtualEdit();
        foreach (var id in elementPresentations.Keys.Where(Changed).ToArray())
        {
            retiringElementPresentations.Add(elementPresentations[id]); elementPresentations.Remove(id);
        }
        foreach (var presentation in retiringElementPresentations.ToArray())
        { presentation.Dispose(); retiringElementPresentations.Remove(presentation); }
        elementPresentationVersion = extensions?.Version ?? -1;
        foreach (var id in active)
            if (!elementPresentations.ContainsKey(id)) elementPresentations.Add(id, new(AuthoringGraph, extensions!, ObjectNode(Require(id)), memberFilter: member => !typeof(Stride.Core.PropertyContainerClass).IsAssignableFrom(member.Type)));
    }
    private EditorAssetPropertyPresentation? ElementPresentation(IMemberNode node)
        => elementPresentations.Values.FirstOrDefault(presentation => presentation.Find(node) != null);
    internal override bool IsExtensionPropertyVisible(IMemberNode node)
        => ElementPresentation(node)?.IsVisible(node) ?? true;
    internal override bool IsExtensionPropertyReadOnly(IMemberNode node)
        => ElementPresentation(node)?.IsReadOnly(node) ?? false;
    internal override string? ExtensionPropertyLabel(IMemberNode node)
        => ElementPresentation(node)?.Label(node) ;
    internal override EditorPresenterTarget? AcquireExtensionCommandTarget(IMemberNode node)
        => ElementPresentation(node)?.AcquireCommandTarget(node) ;
    internal override PropertyNumericRange? ExtensionNumericRange(IMemberNode member) => ElementPresentation(member)?.NumericRange(member);
    internal override PropertyNumericRange? ExtensionNumericRange(VirtualNodePresenter presenter)
        => elementPresentations.Values.FirstOrDefault(presentation => presentation.VirtualRows.Contains(presenter))?.NumericRange(presenter);
    internal override IReadOnlyList<VirtualNodePresenter> ExtensionVirtualProperties
        => elementPresentations.Values.SelectMany(presentation => presentation.VirtualRows).Distinct().ToArray();
    internal override EditorPresenterTarget AcquireExtensionVirtualTarget(VirtualNodePresenter presenter)
        => elementPresentations.Values.FirstOrDefault(presentation => presentation.VirtualRows.Contains(presenter))?.AcquireVirtualTarget(presenter)
            ?? throw new InvalidOperationException("The selected UI element's native presenter is no longer active.");
    internal override bool CanEditExtensionVirtualProperty(VirtualNodePresenter presenter)
    {
        if (!ExtensionVirtualProperties.Contains(presenter) || !base.CanEditExtensionVirtualProperty(presenter)) return false;
        var accessor = presenter.GetNodeAccessor();
        foreach (var id in elementPresentations.Keys)
        {
            if (accessor.Node is IMemberNode member && Properties(id, true).Any(field => ReferenceEquals(field.Node, member) && CanEdit(field))) return true;
            if (accessor.Node is Stride.Core.Assets.Quantum.IAssetObjectNode node)
            {
                if (ReferenceEquals(node, DependencyPropertyNode(id)) && accessor.Index.Value is Stride.Core.PropertyKey key && CanChangeDependencyProperty(id, key)) return true;
                if (PropertyCollections(id).Any(collection => ReferenceEquals(collection.Collection, node)
                    && collection.Items.Any(item => item.Index == accessor.Index && CanEditCollectionItem(item)))) return true;
            }
        }
        return false;
    }
    internal IReadOnlyList<IReadOnlyList<VirtualNodePresenter>> CombinedExtensionVirtualProperties()
    {
        var result = new List<IReadOnlyList<VirtualNodePresenter>>();
        Visit(elementPresentations.Values.Select(presentation => presentation.RootPresenter).ToArray());
        return result;
        void Visit(IReadOnlyList<INodePresenter> parents)
        {
            foreach (var group in NodePresenterCombination.CombineChildren(parents))
            {
                if (!NodePresenterCombination.CanConstruct(group) || group.All(presenter => !presenter.IsVisible)) continue;
                if (group.All(presenter => presenter is VirtualNodePresenter)) result.Add(group.Cast<VirtualNodePresenter>().ToArray());
                Visit(group);
            }
        }
    }
    internal EditorPresenterTarget AcquireCombinedVirtualTarget(IReadOnlyList<VirtualNodePresenter> presenters)
    {
        var targets = new List<EditorPresenterTarget>();
        try { foreach (var presenter in presenters) targets.Add(AcquireExtensionVirtualTarget(presenter)); return new(targets); }
        catch { foreach (var target in targets) target.Dispose(); throw; }
    }
    internal bool HasNativeMemberPresenter(IMemberNode node) => ElementPresentation(node)?.Find(node) != null;
    internal bool CanEditSelectedExtensionMembers(IReadOnlyList<IMemberNode> members)
        => members.Count != 0 && members.All(member => elementPresentations.Keys.Any(id =>
            Properties(id, true).Any(field => ReferenceEquals(field.Node, member) && CanEdit(field))));
    internal EditorPresenterTarget AcquireCombinedMemberTarget(IReadOnlyList<IMemberNode> members)
    {
        var targets = new List<EditorPresenterTarget>();
        try
        {
            foreach (var member in members)
            {
                if (!elementPresentations.Keys.Any(id => Properties(id, true).Any(field => ReferenceEquals(field.Node, member))))
                    throw new InvalidOperationException("The selected UI property's authored owner changed.");
                var target = AcquireExtensionCommandTarget(member) ?? throw new InvalidOperationException("The selected UI property's native presenter is no longer active.");
                targets.Add(target);
            }
            if (targets.Count == 0) throw new InvalidOperationException("No selected native UI property is available.");
            return new(targets);
        }
        catch { foreach (var target in targets) target.Dispose(); throw; }
    }
    internal void TransformExtensionVirtualProperties(IReadOnlyList<VirtualNodePresenter> presenters, Func<object?, object?> transform)
    {
        EndEdit(); EndExtensionVirtualEdit();
        if (presenters.Count == 0 || presenters.Any(presenter => !CanEditExtensionVirtualProperty(presenter)))
            throw new InvalidOperationException("The selected native UI properties cannot be edited.");
        using var target = AcquireCombinedVirtualTarget(presenters);
        var values = presenters.Select(presenter => (Presenter: presenter, Value: NormalizeUIVirtualValue(presenter, transform(presenter.Value)))).ToArray();
        foreach (var item in values)
            if (item.Value != null && !ReferenceEquals(item.Value, item.Presenter.Value)) Stride.Core.Assets.AssetCollectionItemIdHelper.GenerateMissingItemIds(item.Value);
        if (values.Any(item => item.Value == null ? item.Presenter.Type.IsValueType && Nullable.GetUnderlyingType(item.Presenter.Type) == null
            : !item.Presenter.Type.IsInstanceOfType(item.Value))) throw new ArgumentException("The native UI value has an incompatible type.");
        Mutate(() => { foreach (var item in values) item.Presenter.UpdateValue(item.Value!); }, "Edit " + presenters[0].DisplayName);
    }
    private object? NormalizeUIVirtualValue(VirtualNodePresenter presenter, object? value)
    {
        NativeNumericAxes.AssertEditable(presenter, presenter.Value, value);
        if (value is float floating && !float.IsFinite(floating) || value is double precise && !double.IsFinite(precise)) return value;
        return NativeNumericAxes.Normalize(PropertyValueValidation.Normalize(value, ExtensionNumericRange(presenter)), presenter.Value, UIAxisNumericRanges(presenter));
    }
    internal override void DisposeExtensionAssetPresentation()
    {
        DrainUIRenderTargets();
        foreach (var presentation in elementPresentations.Values) retiringElementPresentations.Add(presentation);
        elementPresentations.Clear();
        foreach (var presentation in retiringElementPresentations.ToArray())
        { presentation.Dispose(); retiringElementPresentations.Remove(presentation); }
        elementPresentationVersion = -1;
        base.DisposeExtensionAssetPresentation();
    }
}
