using Stride.Core.Assets;
using Stride.Core.Assets.Quantum;
using Stride.Core.Assets.Editor.Quantum.NodePresenters.Keys;
using Stride.Core.Assets.Editor.Quantum.NodePresenters.Updaters;
using Stride.Core.Presentation.Quantum;
using Stride.Core.Presentation.Quantum.Presenters;
using Stride.Core.Quantum;

namespace Stride.Editor.ImGui;

/// <summary>Native presenter projection over the canonical asset graph, without a WPF view model.</summary>
internal sealed partial class EditorAssetPropertyPresentation : IPropertyProviderViewModel, IDisposable
{
    private readonly AssetPropertyGraph graph;
    private readonly EditorExtensionPresenterLease lease;
    private readonly INodePresenter root;
    private readonly IObjectNode authoredRoot;
    private readonly Func<IMemberNode, bool>? memberFilter;
    private readonly Dictionary<IMemberNode, INodePresenter> members = [];
    private readonly HashSet<INodePresenter> observed = [];
    private List<INodePresenter>? retiring;
    private bool dirty = true, disposed;
    private int uses;
    private bool disposeRequested;
    internal EditorAssetPropertyPresentation(AssetPropertyGraph graph, EditorExtensionSession extensions, IObjectNode? authoredRoot = null, Func<IEnumerable<string>>? shaderNames = null, Func<IMemberNode, bool>? memberFilter = null)
    {
        this.authoredRoot = authoredRoot ?? graph.RootNode; this.memberFilter = memberFilter;
        this.graph = graph; lease = extensions.AcquirePresenters(graph.AuthoredItem.Asset, this.authoredRoot.Type);
        var factory = new Factory(graph, [new EditorCategoryNodeUpdater(), new DisplayAttributeNodeUpdater(), new DictionaryNodeUpdater(), new EditorMathematicsNodeUpdater(), new EditorAbstractNodeEntryUpdater(), new NumericValueNodeUpdater(), new InlineMemberNodeUpdater(),
            new EditorMaterialPropertyUpdater(graph.AuthoredItem.Asset, Stride.Assets.Presentation.NodePresenters.Keys.MaterialData.Key, shaderNames ?? (() => [])),
            new EditorSpritePropertyUpdater(graph.AuthoredItem.Asset), .. lease.Updaters], lease.Commands.ToArray());
        try { root = factory.CreateNodeHierarchy(this.authoredRoot, new GraphNodePath(this.authoredRoot), this); Rebuild(); }
        catch { lease.Dispose(); throw; }
    }
    internal IObjectNode AuthoredRoot => authoredRoot;
    internal INodePresenter RootPresenter { get { Rebuild(); return root; } }
    public bool CanProvidePropertiesViewModel => true;
    public IObjectNode GetRootNode() => authoredRoot;
    public bool ShouldConstructMember(IMemberNode member) => !UnknownAssetData.IsProxyNode(member) && memberFilter?.Invoke(member) != false;
    public bool ShouldConstructItem(IObjectNode collection, NodeIndex index) => !UnknownAssetData.IsProxyNode(collection);
    private void Changed(object? sender, ValueChangedEventArgs args) => dirty = true;
    private void Rebuild()
    {
        if (!dirty) return;
        foreach (var node in observed) node.ValueChanged -= Changed;
        observed.Clear(); members.Clear();
        void Visit(INodePresenter node)
        {
            observed.Add(node); node.ValueChanged += Changed;
            if (node.GetNodeAccessor().Node is IMemberNode member) members.TryAdd(member, node);
            foreach (var child in node.Children) Visit(child);
        }
        Visit(root); dirty = false;
    }
    internal INodePresenter? Find(IMemberNode node) { Rebuild(); return members.GetValueOrDefault(node); }
    internal bool OwnsPresenter(INodePresenter presenter)
    { Rebuild(); return !disposed && !disposeRequested && observed.Contains(presenter); }
    internal bool IsVisible(IMemberNode node) => Find(node)?.IsVisible != false;
    internal bool IsReadOnly(IMemberNode node) => Find(node) is { } presenter && (presenter.IsReadOnly || EditorPresenterItemMetadata.HasReadOnlyItemAncestor(presenter));
    internal string? Label(IMemberNode node) => Find(node) is { } presenter && presenter.DisplayName != node.Name ? presenter.DisplayName : null;
    internal IReadOnlyList<VirtualNodePresenter> VirtualRows
    { get { Rebuild(); return observed.OfType<VirtualNodePresenter>().Where(Visible).OrderBy(node => node.Order ?? int.MaxValue).ThenBy(node => node.Name, StringComparer.Ordinal).ToArray(); } }
    private static bool Visible(INodePresenter node)
    {
        for (INodePresenter? current = node; current != null; current = current.Parent) if (!current.IsVisible) return false;
        return true;
    }
    internal EditorPresenterTarget AcquireVirtualTarget(VirtualNodePresenter presenter) => AcquireTarget(presenter);
    internal EditorPresenterTarget AcquireTarget(INodePresenter presenter)
    {
        Rebuild();
        if (disposed || disposeRequested || !observed.Contains(presenter)) throw new InvalidOperationException("The actual virtual presenter is no longer active.");
        uses++;
        var released = false;
        return new(presenter, () => { if (!released) { uses--; released = true; } if (uses == 0 && disposeRequested) Dispose(); });
    }
    internal EditorPresenterTarget? AcquireCommandTarget(IMemberNode node)
    {
        if (disposed || disposeRequested || Find(node) is not { } presenter) return null;
        uses++;
        var released = false;
        return new(presenter, () => { if (!released) { uses--; released = true; } if (uses == 0 && disposeRequested) Dispose(); });
    }
    public void Dispose()
    {
        if (disposed) return;
        disposeRequested = true;
        if (uses != 0) return;
        foreach (var node in observed) node.ValueChanged -= Changed;
        observed.Clear(); members.Clear();
        if (retiring == null)
        {
            retiring = [];
            void Collect(INodePresenter node) { foreach (var child in node.Children) Collect(child); retiring.Add(node); }
            Collect(root);
        }
        while (retiring.Count != 0) { retiring[0].Dispose(); retiring.RemoveAt(0); }
        lease.Dispose(); disposed = true;
    }
    private sealed class Factory(AssetPropertyGraph graph, IReadOnlyCollection<INodePresenterUpdater> updaters, IReadOnlyCollection<INodePresenterCommand> commands)
        : EditorNodePresenterFactory(graph.Container.NodeContainer.NodeBuilder, commands, updaters)
    {
        protected override bool ShouldCreateMemberPresenter(INodePresenter parent, IMemberNode member, IPropertyProviderViewModel? provider)
        {
            var depth = 0;
            for (var ancestor = parent.Parent; ancestor != null; ancestor = ancestor.Parent)
                if (++depth >= 16 || parent.Value != null && ReferenceEquals(parent.Value, ancestor.Value)) return false;
            if (parent.Value != null && graph.Definition.IsObjectReference(parent.GetNodeAccessor(), parent.Value)) return false;
            return base.ShouldCreateMemberPresenter(parent, member, provider);
        }
        protected override bool ShouldCreateItemPresenter(INodePresenter parent, IObjectNode collection, NodeIndex index, IPropertyProviderViewModel? provider)
        {
            var depth = 0;
            for (var ancestor = parent.Parent; ancestor != null; ancestor = ancestor.Parent)
                if (++depth >= 16 || parent.Value != null && ReferenceEquals(parent.Value, ancestor.Value)) return false;
            if (graph.Definition.IsTargetItemObjectReference(collection, index, collection.Retrieve(index))) return false;
            return base.ShouldCreateItemPresenter(parent, collection, index, provider);
        }
    }
}
