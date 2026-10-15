using Stride.Core.Assets;
using Stride.Core.Annotations;
using Stride.Core.Quantum;
using Stride.Core.Reflection;
using Stride.Core.Settings;
using Stride.Core.Presentation.Quantum.Presenters;

namespace Stride.Editor.ImGui;

/// <summary>Awaited commands edit the real key's native cloned graph; only canonical key publication enters history.</summary>
internal sealed class PackageSettingsCommandMutation : IEditorCommandMutation
{
    private readonly Package package;
    private readonly PackageSettingAccessor accessor;
    private readonly PackageSettingsDocument document;
    private readonly EditorSessionHistory history;
    private readonly EditorAuthoringState owner;
    private readonly Guid ownerGeneration;
    private readonly IDisposable ownerLease;
    private readonly SettingsEntrySnapshot before;
    private EditorExtensionPresenterLease? presenterLease;
    private sealed record Projection(INodePresenter Target, IObjectNode Root, PackageSettingPresentation Presentation, byte[] InitialValue);
    private readonly List<Projection> projections = [];
    private readonly List<EditorExtensionPresenterLease> projectionLeases = [];
    internal IObjectNode CommandRoot => CommandPresentation.ValueGraphRoot;
    internal PackageSettingPresentation CommandPresentation => projections.Count == 0 ? Presentation : projections[^1].Presentation;
    private byte[] initialValue = [];
    private readonly HashSet<IMemberNode> guardedMembers = [];
    private readonly HashSet<IObjectNode> guardedObjects = [];
    private bool settled, disposed, guardsDetached, ownerReleased;
    internal IObjectNode Root { get; private set; } = null!;
    internal PackageSettingPresentation Presentation { get; private set; } = null!;
    internal PackageSettingsCommandMutation(Package package, PackageSettingAccessor accessor, PackageSettingsDocument document, EditorSessionHistory history)
    {
        this.package = package; this.accessor = accessor; this.document = document; this.history = history;
        owner = history.Registry.Find(document.Path) ?? throw new InvalidOperationException("The package setting has no canonical owner.");
        if (!ReferenceEquals(owner.Document, document)) throw new InvalidOperationException("The package setting owner changed.");
        ownerGeneration = owner.Generation;
        before = package.UserSettings.Profile.CaptureAuthoredEntry(accessor.Key);
        ownerLease = owner.AcquireView();
    }
    internal void Initialize(string[][] address)
    {
        Check();
        var nodes = new NodeContainer();
        Root = nodes.GetOrCreateNode(accessor.Snapshot(package)) ?? throw new InvalidOperationException("The native setting command has no value graph.");
        presenterLease = history.Extensions?.AcquirePackagePresenters(package, accessor.Key, accessor.Key.Type);
        Presentation = new(Root, nodes.NodeBuilder, presenterLease, accessor.Key.DisplayName.GetFileName(), accessor.Key.Type);
        foreach (var path in address)
        {
            var target = CommandPresentation.Find(CommandRoot) ?? throw new InvalidOperationException("The native package projection changed.");
            foreach (var step in path) target = target.Children.SingleOrDefault(child => child.Name == step)
                ?? throw new InvalidOperationException("The native package projection changed.");
            if (target is not VirtualNodePresenter) throw new InvalidOperationException("The native package projection is not a virtual property.");
            var projectedNodes = new NodeContainer();
            var projectedRoot = projectedNodes.GetOrCreateNode(AssetCloner.Clone(target.Value)) ?? throw new InvalidOperationException("The native package projection has no value graph.");
            var lease = history.Extensions?.AcquirePackagePresenters(package, accessor.Key, target.Type);
            if (lease != null) projectionLeases.Add(lease);
            var projectedPresentation = new PackageSettingPresentation(projectedRoot, projectedNodes.NodeBuilder, lease, declaredType: target.Type);
            projections.Add(new(target, projectedRoot, projectedPresentation, SerializeValue(projectedRoot.Retrieve())));
        }
        initialValue = SerializeRoot();
        AttachGuards();
    }
    private byte[] SerializeRoot() => SerializeValue(Presentation.Value);
    private static byte[] SerializeValue(object? value)
    {
        using var stream = new MemoryStream();
        SettingsYamlSerializer.Default.Serialize(stream, value!);
        return stream.ToArray();
    }
    private void Check()
    {
        owner.Registry.EnsureOwnerThread();
        if (!owner.IsPublished || owner.IsDisposed || owner.Generation != ownerGeneration || !ReferenceEquals(owner.Document, document)
            || !ReferenceEquals(owner.Registry.Find(owner.CanonicalPath), owner) || !package.UserSettings.Profile.CanReadRegisteredValue(accessor.Key)
            || package.UserSettings.Profile.CaptureAuthoredEntry(accessor.Key).ContentFingerprint != before.ContentFingerprint)
            throw new InvalidOperationException("The native package command's key or canonical owner changed.");
    }
    private void AttachGuards()
    {
        var visited = new HashSet<IObjectNode>();
        void Visit(IObjectNode node)
        {
            if (!visited.Add(node) || ContentAssetReference.Supports(node.Type)) return;
            if (guardedObjects.Add(node)) { node.ItemChanging += ItemChanging; node.ItemChanged += ItemChanged; }
            foreach (var member in node.Members)
            {
                if (guardedMembers.Add(member)) { member.ValueChanging += MemberChanging; member.ValueChanged += MemberChanged; }
                if (member.Target is { } child) Visit(child);
            }
            if (node.ItemReferences != null)
                foreach (var index in node.Indices!) if (node.IndexedTarget(index) is { } child) Visit(child);
        }
        Visit(Presentation.GuardRoot);
        foreach (var projection in projections) Visit(projection.Presentation.GuardRoot);
    }
    private INodePresenter? Presenter(IGraphNode node, NodeIndex index = default)
        => Presentation.Find(node, index) ?? projections.Select(projection => projection.Presentation.Find(node, index)).FirstOrDefault(presenter => presenter != null);
    private static bool ReadOnly(INodePresenter? presenter)
    {
        for (; presenter != null; presenter = presenter.Parent) if (presenter.IsReadOnly) return true;
        return false;
    }
    private void MemberChanging(object? sender, MemberNodeChangeEventArgs args)
    {
        Check();
        if (!document.CanSave || package.State < PackageState.AssetsReady) throw new InvalidOperationException("The native package is read-only.");
        if (!args.Member.MemberDescriptor.HasSet || ReadOnly(Presenter(args.Member))) throw new InvalidOperationException("The native package property is read-only.");
        if (args.NewValue == null && TypeDescriptorFactory.Default.AttributeRegistry.GetAttribute<NotNullAttribute>(args.Member.MemberDescriptor.MemberInfo) != null)
            throw new InvalidOperationException("The native package property does not allow null.");
    }
    private void ItemChanging(object? sender, ItemChangeEventArgs args)
    {
        Check();
        if (!document.CanSave || package.State < PackageState.AssetsReady) throw new InvalidOperationException("The native package is read-only.");
        var value = args.Collection.Retrieve();
        var descriptor = args.Collection.Descriptor;
        var header = Presentation.Header(args.Collection) ?? projections.Select(projection => projection.Presentation.Header(args.Collection)).FirstOrDefault(presenter => presenter != null);
        var metadata = header is MemberNodePresenter memberHeader ? memberHeader.MemberAttributes.OfType<MemberCollectionAttribute>().FirstOrDefault()
            : args.Collection.Descriptor.Attributes.OfType<MemberCollectionAttribute>().FirstOrDefault();
        if (ReadOnly(header) || ReadOnly(Presenter(args.Collection, args.Index))
            || metadata?.ReadOnly == true && (args.ChangeType is ContentChangeType.CollectionAdd or ContentChangeType.CollectionRemove)
            || metadata?.NotNullItems == true && (args.ChangeType is ContentChangeType.CollectionAdd or ContentChangeType.CollectionUpdate) && args.NewValue == null
            || descriptor is CollectionDescriptor collection && collection.IsReadOnly(value)
            || descriptor is DictionaryDescriptor dictionary && dictionary.IsReadOnly(value))
            throw new InvalidOperationException("The native package collection is read-only.");
    }
    private void MemberChanged(object? sender, MemberNodeChangeEventArgs args) => AttachGuards();
    private void ItemChanged(object? sender, ItemChangeEventArgs args) => AttachGuards();
    private void DetachGuards()
    {
        if (guardsDetached) return;
        foreach (var member in guardedMembers) { member.ValueChanging -= MemberChanging; member.ValueChanged -= MemberChanged; }
        foreach (var node in guardedObjects) { node.ItemChanging -= ItemChanging; node.ItemChanged -= ItemChanged; }
        guardsDetached = true;
    }
    public void Commit(string title)
    {
        Check();
        if (settled) throw new InvalidOperationException("The native package command is already settled.");
        for (var index = projections.Count - 1; index >= 0; index--)
        {
            var projection = projections[index];
            if (projection.InitialValue.AsSpan().SequenceEqual(SerializeValue(projection.Presentation.Value))) continue;
            if (ReadOnly(projection.Target)) throw new InvalidOperationException("The native package projection is read-only.");
            projection.Target.UpdateValue(projection.Presentation.Value!);
        }
        if (!initialValue.AsSpan().SequenceEqual(SerializeRoot()))
        {
            if (!document.CanSave || package.State < PackageState.AssetsReady) throw new InvalidOperationException("The native package is read-only.");
            using var transaction = history.CreateTransaction(title);
            accessor.Set(document, Presentation.Value);
        }
        settled = true; DetachGuards();
    }
    public void Rollback()
    {
        owner.Registry.EnsureOwnerThread();
        if (settled) return;
        DetachGuards(); settled = true;
    }
    public void Dispose()
    {
        if (disposed) return;
        owner.Registry.EnsureOwnerThread();
        if (!settled) Rollback();
        for (var index = projections.Count - 1; index >= 0; index--) projections[index].Presentation.Dispose();
        for (var index = projectionLeases.Count - 1; index >= 0; index--) projectionLeases[index].Dispose();
        Presentation?.Dispose(); presenterLease?.Dispose();
        if (!ownerReleased) { ownerLease.Dispose(); ownerReleased = true; }
        disposed = true;
    }
}
