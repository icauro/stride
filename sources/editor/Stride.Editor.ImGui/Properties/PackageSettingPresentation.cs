using Stride.Core;
using Stride.Core.Annotations;
using Stride.Core.Assets.Editor.Quantum.NodePresenters.Keys;
using Stride.Core.Assets.Editor.Quantum.NodePresenters.Updaters;
using Stride.Core.Presentation.Quantum;
using Stride.Core.Presentation.Quantum.Presenters;
using Stride.Core.Quantum;

namespace Stride.Editor.ImGui;

/// <summary>Native presenter metadata for a real settings-key value snapshot, with retained provider ownership.</summary>
internal sealed class PackageSettingPresentation : IPropertyProviderViewModel, IDisposable
{
    private readonly IObjectNode authoredRoot;
    private readonly IObjectNode wrapperRoot;
    private readonly IMemberNode valueMember;
    private readonly NodeContainer nodes;
    private readonly INodePresenter hierarchyRoot;
    private readonly HashSet<IMemberNode> members = [];
    private readonly HashSet<(IObjectNode, NodeIndex)> items = [];
    private readonly Dictionary<(IGraphNode, NodeIndex), INodePresenter> presenters = [];
    private readonly EditorExtensionPresenterLease? extensions;
    private readonly INodePresenter root;
    internal INodePresenter RootPresenter => root;
    internal IObjectNode GuardRoot => wrapperRoot;
    internal object? Value => valueMember.Retrieve();
    internal IObjectNode ValueGraphRoot => valueMember.Target ?? nodes.GetOrCreateNode(Value) ?? throw new InvalidOperationException("The native setting value has no graph.");
    internal bool Owns(INodePresenter presenter) => ReferenceEquals(presenter.Root, hierarchyRoot);
    private bool disposed, disposeRequested;
    private int uses;
    private bool dirty = true;
    private readonly HashSet<INodePresenter> observed = [];
    internal EditorPresenterTarget AcquireTarget(INodePresenter presenter)
    {
        if (disposed || disposeRequested || !ReferenceEquals(presenter.Root, hierarchyRoot)) throw new InvalidOperationException("The package presenter is no longer active.");
        uses++;
        var released = false;
        return new(presenter, () =>
        {
            if (!released) { uses--; released = true; }
            if (uses == 0 && disposeRequested) Dispose();
        });
    }
    internal PackageSettingPresentation(IObjectNode authoredRoot, INodeBuilder builder, EditorExtensionPresenterLease? extensions, string? rootLabel = null, Type? declaredType = null)
    {
        this.authoredRoot = authoredRoot; this.extensions = extensions; nodes = builder.NodeContainer;
        var wrapper = Activator.CreateInstance(typeof(PackageSettingValue<>).MakeGenericType(declaredType ?? authoredRoot.Type), new object?[] { authoredRoot.Retrieve() })
            ?? throw new InvalidOperationException("The native setting value wrapper could not be created.");
        wrapperRoot = nodes.GetOrCreateNode(wrapper) ?? throw new InvalidOperationException("The native setting wrapper has no graph.");
        valueMember = wrapperRoot[nameof(PackageSettingValue<object>.TypedValue)];
        CollectBounds();
        INodePresenterUpdater[] native = [new EditorCategoryNodeUpdater(), new DisplayAttributeNodeUpdater(), new DictionaryNodeUpdater(), new EditorMathematicsNodeUpdater(), new EditorAbstractNodeEntryUpdater(), new NumericMetadataUpdater(), new PackageInlineMemberUpdater(), new SettingsRootLabelUpdater(rootLabel, wrapperRoot)];
        var factory = new EditorNodePresenterFactory(builder, extensions?.Commands ?? [], native.Concat(extensions?.Updaters ?? []).ToArray());
        try
        {
            hierarchyRoot = factory.CreateNodeHierarchy(wrapperRoot, new GraphNodePath(wrapperRoot), this);
            root = FindValueMember(hierarchyRoot, valueMember) ?? throw new InvalidOperationException("The native setting value presenter is missing.");
            RebuildMap();
        }
        catch { extensions?.Dispose(); throw; }
    }
    public bool CanProvidePropertiesViewModel => true;
    public IObjectNode GetRootNode() => wrapperRoot;
    private void CollectBounds()
    {
        members.Clear(); items.Clear();
        void Visit(IObjectNode node, HashSet<IObjectNode> ancestors)
        {
            if (!ancestors.Add(node)) return;
            if (ContentAssetReference.Supports(node.Type)) { ancestors.Remove(node); return; }
            foreach (var member in node.Members)
            {
                if (member.Target is { } target && ancestors.Contains(target)) continue;
                members.Add(member);
                if (member.Target is { } child) Visit(child, ancestors);
            }
            if (node.Indices != null)
                foreach (var index in node.Indices)
                {
                    var child = node.ItemReferences == null ? null : node.IndexedTarget(index);
                    if (child != null && ancestors.Contains(child)) continue;
                    items.Add((node, index));
                    if (child != null) Visit(child, ancestors);
                }
            ancestors.Remove(node);
        }
        Visit(wrapperRoot, []);
    }
    public bool ShouldConstructMember(IMemberNode member)
    {
        if (!members.Contains(member)) CollectBounds();
        return members.Contains(member);
    }
    public bool ShouldConstructItem(IObjectNode collection, NodeIndex index)
    {
        if (!items.Contains((collection, index))) CollectBounds();
        return items.Contains((collection, index));
    }
    private void Changed(object? sender, ValueChangedEventArgs args) => dirty = true;
    private void RebuildMap()
    {
        if (!dirty) return;
        foreach (var node in observed) node.ValueChanged -= Changed;
        observed.Clear(); presenters.Clear();
        void Map(INodePresenter node)
        {
            observed.Add(node); node.ValueChanged += Changed;
            var accessor = node.GetNodeAccessor();
            if (accessor.Node != null && node is not VirtualNodePresenter) presenters[(accessor.Node, accessor.Index)] = node;
            foreach (var child in node.Children) Map(child);
        }
        Map(hierarchyRoot); presenters[(authoredRoot, NodeIndex.Empty)] = root; dirty = false;
    }
    internal INodePresenter? Find(IGraphNode node, NodeIndex index = default)
    {
        RebuildMap();
        if (index.IsEmpty && (ReferenceEquals(node, authoredRoot) || ReferenceEquals(node, valueMember.Target))) return root;
        return presenters.GetValueOrDefault((node, index));
    }
    internal INodePresenter? Header(IObjectNode node)
    {
        RebuildMap();
        if (ReferenceEquals(node, authoredRoot)) return root;
        return presenters.Values.FirstOrDefault(presenter => presenter.GetNodeAccessor().Node is IMemberNode member && ReferenceEquals(member.Target, node)
            || presenter.GetNodeAccessor() is { Node: IObjectNode collection, Index: var index } && !index.IsEmpty
            && collection.ItemReferences != null && ReferenceEquals(collection.IndexedTarget(index), node));
    }
    internal PropertyNumericRange? NumericRange(IGraphNode node, Type type, NodeIndex index = default)
        => NumericRange(Find(node, index), type);
    internal PropertyNumericRange? NumericRange(INodePresenter? presenter, Type type)
    {
        if (presenter == null) return null;
        var properties = presenter.AttachedProperties;
        properties.TryGetValue(NumericData.MinimumKey, out var minimum); properties.TryGetValue(NumericData.MaximumKey, out var maximum);
        properties.TryGetValue(NumericData.SmallStepKey, out var small); properties.TryGetValue(NumericData.LargeStepKey, out var large);
        properties.TryGetValue(NumericData.DecimalPlacesKey, out var places);
        if (minimum == null && maximum == null && small == null && large == null && places == null) return null;
        return new(new PropertyNumericMetadata(minimum == null ? null : Convert.ToDouble(minimum),
            maximum == null ? null : Convert.ToDouble(maximum), small, large, places), type, places != null);
    }
    public void Dispose()
    {
        if (disposed) return;
        disposeRequested = true;
        if (uses != 0) return;
        foreach (var node in observed) node.ValueChanged -= Changed;
        observed.Clear();
        hierarchyRoot.Dispose(); extensions?.Dispose(); disposed = true;
    }
    private static INodePresenter? FindValueMember(INodePresenter presenter, IMemberNode value)
    {
        if (presenter is MemberNodePresenter && ReferenceEquals(presenter.GetNodeAccessor().Node, value)) return presenter;
        foreach (var child in presenter.Children) if (FindValueMember(child, value) is { } found) return found;
        return null;
    }
    internal static string[] RelativePath(INodePresenter presenter)
    {
        var path = new List<string>();
        for (; presenter.Parent != null; presenter = presenter.Parent)
        {
            if (presenter is MemberNodePresenter && presenter.GetNodeAccessor().Node is IMemberNode member && member.Parent.Retrieve() is IPackageSettingValue) break;
            path.Insert(0, presenter.Name);
        }
        return path.ToArray();
    }
    private sealed class PackageInlineMemberUpdater : NodePresenterUpdaterBase
    {
        private readonly InlineMemberNodeUpdater inline = new();

        public override void FinalizeTree(INodePresenter root)
        {
            // The native key row itself supplies the inline wrapper header.
            // Apply stock inline rules to the value and its descendants without
            // hiding the effective value member behind a second wrapper row.
            var value = root.Children.FirstOrDefault(node => node is MemberNodePresenter && node.GetNodeAccessor().Node is IMemberNode member && member.Parent.Retrieve() is IPackageSettingValue);
            inline.FinalizeTree(value ?? root);
        }
    }
    private sealed class SettingsRootLabelUpdater(string? label, IObjectNode wrapperRoot) : NodePresenterUpdaterBase
    {
        public override void UpdateNode(INodePresenter node)
        {
            // Original SettingsPropertyNodeUpdater uses the key's filename label;
            // contextual extension updaters run after this native default.
            if (node.GetNodeAccessor().Node is IMemberNode member && member.Name == nameof(PackageSettingValue<object>.TypedValue) && member.Parent == wrapperRoot && label != null) node.DisplayName = label;
        }
    }
    private sealed class NumericMetadataUpdater : NodePresenterUpdaterBase
    {
        public override void UpdateNode(INodePresenter node)
        {
            if (node is MemberNodePresenter && node.GetNodeAccessor().Node is IMemberNode member)
                new NumericValueNodeUpdater().UpdateNode(node, member.MemberDescriptor.MemberInfo);
        }
    }
}
