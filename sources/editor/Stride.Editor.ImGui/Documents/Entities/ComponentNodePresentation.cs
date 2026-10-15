using Stride.Core;
using Stride.Core.Annotations;
using Stride.Core.Assets.Editor.Quantum.NodePresenters.Keys;
using Stride.Core.Assets.Editor.Quantum.NodePresenters.Updaters;
using Stride.Core.Presentation.Quantum;
using Stride.Core.Presentation.Quantum.Presenters;
using Stride.Core.Quantum;
using Stride.Core.Reflection;
using Stride.Assets.Presentation.NodePresenters.Updaters;
using Stride.Assets.Presentation.NodePresenters.Rules;

namespace Stride.Editor.ImGui;

/// <summary>Runs shared node updaters against the document's actual Quantum nodes.</summary>
internal sealed partial class ComponentNodePresentation : IPropertyProviderViewModel, IDisposable
{
    private readonly IObjectNode authoredRoot;
    private readonly HashSet<IMemberNode> members;
    private readonly INodePresenter root;
    private readonly EditorExtensionPresenterLease? extensionLease;
    private readonly Func<Type, bool>? hasPrimitiveEditor;
    private readonly Func<IObjectNode, NodeIndex, bool>? isOwnedItem;
    private readonly Func<IMemberNode, bool>? isOwnedMember;
    private readonly Func<IGraphNode, bool>? canEditNode;
    private readonly Dictionary<IMemberNode, INodePresenter> presenters = [];
    private readonly Dictionary<IMemberNode, PropertyNumericRange?> ranges = [];
    private readonly HashSet<INodePresenter> observed = [];
    private bool dirty = true;
    private List<INodePresenter>? retiringNodes;
    private bool disposed;
    private int uses;
    private bool disposeRequested;

    internal ComponentNodePresentation(IObjectNode authoredRoot, INodeBuilder builder, IReadOnlyList<ComponentField> fields, Type? rootDeclaredType = null,
        EditorExtensionPresenterLease? extensions = null, Func<Type, bool>? hasPrimitiveEditor = null,
        Func<IObjectNode, NodeIndex, bool>? isOwnedItem = null, Func<IMemberNode, bool>? isOwnedMember = null,
        Func<IGraphNode, bool>? canEditNode = null)
    {
        this.authoredRoot = authoredRoot;
        members = EntityHierarchyDocument.Flatten(fields).Select(member => member.Node).ToHashSet();
        extensionLease = extensions;
        this.hasPrimitiveEditor = hasPrimitiveEditor;
        this.isOwnedItem = isOwnedItem;
        this.isOwnedMember = isOwnedMember;
        this.canEditNode = canEditNode;
        INodePresenterUpdater[] updaters = [new EditorCategoryNodeUpdater(), new DisplayAttributeNodeUpdater(), new DictionaryNodeUpdater(), new EditorMathematicsNodeUpdater(),
            new EditorAbstractNodeEntryUpdater(), new AuthoredNumericMetadataUpdater(), new FXAAEffectNodeUpdater(), new InlineMemberNodeUpdater(), new CameraProjectionUpdater(), new ParticleTransformUpdater(rootDeclaredType)];
        var factory = new BoundedPresenterFactory(builder, updaters.Concat(extensions?.Updaters ?? []).ToArray(), extensions?.Commands ?? []);
        try { root = factory.CreateNodeHierarchy(authoredRoot, new GraphNodePath(authoredRoot), this); RebuildMap(); }
        catch { extensions?.Dispose(); throw; }
    }

    public bool CanProvidePropertiesViewModel => true;
    public IObjectNode GetRootNode() => authoredRoot;
    public bool ShouldConstructMember(IMemberNode member)
        => !UnknownAssetData.IsProxyNode(member) && (members.Contains(member) || IsOwnedCollectionObject(member.Parent));
    public bool ShouldConstructItem(IObjectNode collection, NodeIndex index)
        => IsOwnedCollection(collection) && (collection.Indices ?? []).Contains(index);

    private void Changed(object? sender, ValueChangedEventArgs change) => dirty = true;

    private void RebuildMap()
    {
        if (!dirty) return;
        foreach (var node in observed) node.ValueChanged -= Changed;
        observed.Clear(); presenters.Clear(); ranges.Clear();
        void Visit(INodePresenter node)
        {
            observed.Add(node); node.ValueChanged += Changed;
            if (node is MemberNodePresenter && node.GetNodeAccessor().Node is IMemberNode member) presenters[member] = node;
            foreach (var child in node.Children) Visit(child);
        }
        Visit(root); dirty = false;
    }

    private INodePresenter? Find(ComponentField field)
    { RebuildMap(); return presenters.GetValueOrDefault(field.Node); }

    internal bool IsVisible(ComponentField field)
        => Find(field) is not { } presenter || EditorPresenterItemMetadata.IsVisible(presenter);
    internal bool IsReadOnly(ComponentField field) => IsAccessorReadOnly(field.Node, NodeIndex.Empty) || canEditNode?.Invoke(field.Node) == false;
    internal bool HasPrimitiveEditor(ComponentField field) => hasPrimitiveEditor?.Invoke(field.DeclaredType) == true;
    internal IEnumerable<Stride.Core.Assets.Editor.Quantum.NodePresenters.Commands.AbstractNodeEntry>? AbstractEntries(ComponentField field)
        => Find(field) is { } presenter && presenter.AttachedProperties.TryGetValue(AbstractNodeEntryData.Key, out var entries) ? entries : null;
    internal EditorPresenterTarget? AcquireCommandTarget(ComponentField field)
        => AcquireCommandTarget(field.Node);
    internal EditorPresenterTarget? AcquireCommandTarget(IMemberNode node)
    {
        RebuildMap();
        if (disposed || disposeRequested || !presenters.TryGetValue(node, out var presenter)) return null;
        uses++;
        var released = false;
        return new(presenter, () => { if (!released) { uses--; released = true; } if (uses == 0 && disposeRequested) Dispose(); });
    }
    internal ExpandRule? Expansion(ComponentField field)
    {
        var node = Find(field);
        return node != null && node.AttachedProperties.TryGetValue(DisplayData.AutoExpandRuleKey, out var expand) ? expand : null;
    }

    internal ComponentField? InlineMember(ComponentField field) => field.Children.FirstOrDefault(member => Find(member)?.Name == InlineData.InlinedProperty);

    internal PropertyNumericRange? NumericRange(ComponentField field)
    {
        var node = Find(field);
        return node == null ? null : ReadItemNumericRange(node, field.Type);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposeRequested = true;
        if (uses != 0) return;
        foreach (var node in observed) node.ValueChanged -= Changed;
        observed.Clear(); presenters.Clear(); ranges.Clear();
        if (retiringNodes == null)
        {
            retiringNodes = [];
            void Collect(INodePresenter node) { foreach (var child in node.Children) Collect(child); retiringNodes.Add(node); }
            Collect(root);
        }
        while (retiringNodes.Count != 0) { retiringNodes[0].Dispose(); retiringNodes.RemoveAt(0); }
        extensionLease?.Dispose();
        disposed = true;
    }

    private sealed class BoundedPresenterFactory(INodeBuilder builder, IReadOnlyCollection<INodePresenterUpdater> updaters, IReadOnlyCollection<INodePresenterCommand> commands)
        : EditorNodePresenterFactory(builder, commands, updaters)
    {
        protected override bool ShouldCreateMemberPresenter(INodePresenter parent, IMemberNode member, IPropertyProviderViewModel? provider)
        {
            var depth = 0;
            for (var ancestor = parent.Parent; ancestor != null; ancestor = ancestor.Parent)
                if (++depth >= 16 || ReferenceEquals(parent.Value, ancestor.Value)) return false;
            return base.ShouldCreateMemberPresenter(parent, member, provider);
        }
    }

    private sealed class AuthoredNumericMetadataUpdater : NodePresenterUpdaterBase
    {
        public override void UpdateNode(INodePresenter node)
        {
            if (node is not MemberNodePresenter || node.GetNodeAccessor().Node is not IMemberNode member) return;
            new NumericValueNodeUpdater().UpdateNode(node, member.MemberDescriptor.MemberInfo);
        }
    }

    private sealed class ParticleTransformUpdater(Type? rootDeclaredType) : NodePresenterUpdaterBase
    {
        public override void FinalizeTree(INodePresenter root) => ParticleTransformNodeRules.Apply(root, rootDeclaredType);
    }

    private sealed class CameraProjectionUpdater : NodePresenterUpdaterBase
    {
        public override void FinalizeTree(INodePresenter root) => CameraComponentNodeRules.Apply(root);
    }
}
