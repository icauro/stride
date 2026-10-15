using System.ComponentModel;
using System.Globalization;
using Stride.Core;
using Stride.Core.Assets;
using Stride.Core.Extensions;
using Stride.Core.Quantum;
using Stride.Core.Reflection;
using Stride.Core.Settings;
using Stride.Core.Annotations;
using Stride.Core.Assets.Editor.Quantum.NodePresenters.Keys;
using Stride.Core.Assets.Editor.Quantum.NodePresenters.Commands;
using Stride.Core.Presentation.Quantum.Presenters;
using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private sealed record SettingStep(string? Member, NodeIndex Index);
    private sealed class SettingProjection : IDisposable
    {
        internal readonly SettingProjection? Parent;
        internal readonly VirtualNodePresenter Source;
        internal readonly string[] Path;
        internal readonly Type Type;
        internal readonly NodeContainer Nodes = new();
        internal IObjectNode Root => Presentation.ValueGraphRoot;
        internal readonly PackageSettingPresentation Presentation;
        internal readonly Dictionary<VirtualNodePresenter, SettingProjection> Children = [];
        internal SettingProjection(SettingProjection? parent, string[] path, VirtualNodePresenter source, Package package, PackageSettingAccessor accessor, EditorSessionHistory? history)
        {
            Parent = parent; Source = source; Path = path; Type = source.Type;
            var root = Nodes.GetOrCreateNode(AssetCloner.Clone(source.Value)) ?? throw new InvalidOperationException("The registered package value has no native graph.");
            Presentation = new(root, Nodes.NodeBuilder, history?.Extensions?.AcquirePackagePresenters(package, accessor.Key, source.Type), declaredType: source.Type);
        }
        internal string[][] Address()
            => Parent == null ? [Path] : [.. Parent.Address(), Path];
        public void Dispose()
        {
            foreach (var child in Children.Values) child.Dispose();
            Presentation.Dispose();
        }
    }
    private sealed class SettingTree(object? source, object? snapshot, long version) : IDisposable
    {
        internal readonly object? Source = source;
        internal readonly object? Snapshot = snapshot;
        internal readonly long Version = version;
        internal PackageSettingPresentation? KeyPresentation;
        internal SettingProjection? ActiveProjection;
        internal readonly Dictionary<VirtualNodePresenter, SettingProjection> Projections = [];
        internal PackageSettingPresentation? Presentation => ActiveProjection?.Presentation ?? KeyPresentation;
        public void Dispose()
        {
            foreach (var projection in Projections.Values) projection.Dispose();
            KeyPresentation?.Dispose();
        }
        internal readonly NodeContainer Nodes = new();
        internal readonly List<Func<IObjectNode, IObjectNode>> Pending = [];
        internal readonly List<Func<IObjectNode, IObjectNode>> Structural = [];
        internal IObjectNode? Root => ActiveProjection?.Root ?? KeyPresentation?.ValueGraphRoot ?? Nodes.GetOrCreateNode(Snapshot);
    }
    private readonly Dictionary<(Package, SettingsKey), SettingTree> packageSettingTrees = [];

    private void RemovePackageSettingTree((Package, SettingsKey) key)
    {
        if (!packageSettingTrees.TryGetValue(key, out var tree)) return;
        tree.Dispose(); packageSettingTrees.Remove(key);
    }
    private void ClearPackageSettingTrees()
    {
        foreach (var key in packageSettingTrees.Keys.ToArray()) RemovePackageSettingTree(key);
    }
    private SettingTree GetPackageSettingTree(Package package, PackageSettingAccessor accessor)
    {
        var cacheKey = (package, accessor.Key); var value = accessor.Get(package);
        var version = SessionAuthoringHistory?.Extensions?.Version ?? -1;
        if (!packageSettingTrees.TryGetValue(cacheKey, out var tree) || tree.Version != version || !SameSettingValue(accessor, value, tree.Source))
        {
            RemovePackageSettingTree(cacheKey);
            packageSettingTrees[cacheKey] = tree = new(value, accessor.Snapshot(package), version);
        }
        if (tree.KeyPresentation == null && tree.Root is { } root)
            tree.KeyPresentation = new(root, tree.Nodes.NodeBuilder, SessionAuthoringHistory?.Extensions?.AcquirePackagePresenters(package, accessor.Key, accessor.Key.Type), accessor.Key.DisplayName.GetFileName(), accessor.Key.Type);
        return tree;
    }
    private void DrawPackageSettingTree(Package package, PackageSettingAccessor accessor, string label)
    {
        var cacheKey = (package, accessor.Key); var value = accessor.Get(package);
        var tree = GetPackageSettingTree(package, accessor);
        var rootPresenter = tree.Root is { } rootNode ? tree.Presentation?.Find(rootNode) : null;
        if (rootPresenter?.IsVisible == false) return;
        var open = Gui.TreeNodeEx(label, SettingExpansionFlags(rootPresenter));
        DrawPackageSettingCommands(package, accessor, rootPresenter);
        if (!open) return;
        tree.Pending.Clear(); tree.Structural.Clear();
        Gui.BeginDisabled(!PackageSettingsEditable(package) || PackagePresenterReadOnly(rootPresenter));
        DrawPackageObjectChoice(package, accessor, [], accessor.Key.Type, value, updated => accessor.Set(GetPackageSettingsDocument(package, SessionAuthoringHistory!), updated), false, rootPresenter);
        Gui.EndDisabled();
        if (SameSettingValue(accessor, accessor.Get(package), tree.Source) && tree.Root is { } root)
            DrawPackageSettingNode(package, accessor, root, [], new HashSet<object>(ReferenceEqualityComparer.Instance));
        if (tree.Pending.Count != 0 || tree.Structural.Count != 0) Execute(() => CommitPackageSettingEdits(package, accessor));
        Gui.TreePop();
    }
    private void CommitPackageSettingEdits(Package package, PackageSettingAccessor accessor, bool retainPresentation = false)
    {
        var cacheKey = (package, accessor.Key);
        if (!packageSettingTrees.TryGetValue(cacheKey, out var tree) || tree.Pending.Count == 0 && tree.Structural.Count == 0) return;
        if (!SameSettingValue(accessor, accessor.Get(package), tree.Source)) throw new InvalidOperationException("The package setting changed before the edit was committed.");
        accessor.EditValueReplacingRoot(GetPackageSettingsDocument(package, SessionAuthoringHistory!), edited =>
        {
            foreach (var apply in tree.Pending.Concat(tree.Structural)) edited = apply(edited);
            return edited.Retrieve();
        });
        tree.Pending.Clear(); tree.Structural.Clear();
        if (!retainPresentation) RemovePackageSettingTree(cacheKey);
    }
    private static ImGuiTreeNodeFlags SettingExpansionFlags(INodePresenter? presenter)
        => presenter != null && presenter.AttachedProperties.TryGetValue(DisplayData.AutoExpandRuleKey, out var expand) && expand == ExpandRule.Never
            ? ImGuiTreeNodeFlags.None : ImGuiTreeNodeFlags.DefaultOpen;
    private static INodePresenter? SettingNodePresenter(SettingTree tree, SettingStep[] path)
    {
        if (tree.Root is not { } root) return null;
        if (path.Length == 0) return tree.Presentation?.Find(root);
        var parent = ResolveSettingNode(root, path[..^1]); var step = path[^1];
        return step.Member != null ? tree.Presentation?.Find(parent[step.Member]) : tree.Presentation?.Find(parent, step.Index);
    }
    private static IObjectNode ResolveSettingNode(IObjectNode root, IReadOnlyList<SettingStep> path)
    {
        foreach (var step in path)
            root = step.Member != null ? root[step.Member].Target ?? throw new InvalidOperationException("The setting member changed.")
                : root.IndexedTarget(step.Index) ?? throw new InvalidOperationException("The setting collection item changed.");
        return root;
    }
    private void EditPackageSetting(Package package, PackageSettingAccessor accessor, IReadOnlyList<SettingStep> path, Action<IObjectNode> mutate, bool structural = false)
    {
        var expected = packageSettingTrees[(package, accessor.Key)].Source;
        if (!SameSettingValue(accessor, accessor.Get(package), expected)) throw new InvalidOperationException("The package setting changed before the edit was committed.");
        var tree = packageSettingTrees[(package, accessor.Key)];
        QueueSettingMutation(package, accessor, tree, structural, root => { mutate(ResolveSettingNode(root, path)); return root; });
    }
    private static bool SameSettingValue(PackageSettingAccessor accessor, object? left, object? right)
        => ReferenceEquals(left, right) || accessor.Key.Type.IsValueType && Equals(left, right);
    private string SettingPath(Package package, PackageSettingAccessor accessor, IReadOnlyList<SettingStep> path, string? member = null)
    {
        var projection = packageSettingTrees[(package, accessor.Key)].ActiveProjection;
        var prefix = projection == null ? "" : string.Join("/", projection.Address().Select(address => "{virtual:" + string.Join("/", address) + "}"));
        return prefix + string.Join("/", path.Select(step => step.Member ?? "[" + step.Index + "]")) + "/" + member;
    }
    private static string[] PresenterPath(INodePresenter presenter) => PackageSettingPresentation.RelativePath(presenter);
    private static INodePresenter ResolvePresenter(PackageSettingPresentation presentation, IObjectNode root, string[] path)
    {
        var node = presentation.Find(root) ?? throw new InvalidOperationException("The native package root presenter changed.");
        foreach (var step in path) node = node.Children.SingleOrDefault(child => child.Name == step)
            ?? throw new InvalidOperationException("The native package presenter changed.");
        return node;
    }
    private void QueueSettingMutation(Package package, PackageSettingAccessor accessor, SettingTree tree, bool structural, Func<IObjectNode, IObjectNode> apply, string[][]? capturedAddress = null)
    {
        var address = capturedAddress ?? tree.ActiveProjection?.Address() ?? [];
        (structural ? tree.Structural : tree.Pending).Add(root => ApplyProjectedSettingMutation(package, accessor, tree, root, address, 0, accessor.Key.Type, apply));
    }
    private IObjectNode ApplyProjectedSettingMutation(Package package, PackageSettingAccessor accessor, SettingTree tree, IObjectNode root,
        string[][] address, int depth, Type declared, Func<IObjectNode, IObjectNode> apply)
    {
        if (depth == address.Length) return apply(root);
        using var presentation = new PackageSettingPresentation(root, tree.Nodes.NodeBuilder,
            SessionAuthoringHistory?.Extensions?.AcquirePackagePresenters(package, accessor.Key, declared), declaredType: declared);
        var target = ResolvePresenter(presentation, root, address[depth]) as VirtualNodePresenter
            ?? throw new InvalidOperationException("The registered package value changed.");
        if (target.IsReadOnly || target.Parent?.IsReadOnly == true) throw new InvalidOperationException("The registered package value is read-only.");
        var nodes = new NodeContainer();
        var projected = nodes.GetOrCreateNode(AssetCloner.Clone(target.Value)) ?? throw new InvalidOperationException("The registered package value has no native graph.");
        projected = ApplyProjectedSettingMutation(package, accessor, tree, projected, address, depth + 1, target.Type, apply);
        target.UpdateValue(projected.Retrieve()!);
        return presentation.ValueGraphRoot;
    }

    private void DrawPackageSettingVirtualValue(Package package, PackageSettingAccessor accessor, SettingStep[] path, VirtualNodePresenter virtualNode)
    {
        var tree = packageSettingTrees[(package, accessor.Key)];
        var label = virtualNode.DisplayName; var value = virtualNode.Value;
        var editable = PackageSettingsEditable(package) && !PackagePresenterReadOnly(virtualNode);
        var key = SettingPath(package, accessor, path, "virtual/" + virtualNode.Name);
        var nativePath = PresenterPath(virtualNode);
        var declared = tree.ActiveProjection?.Type ?? accessor.Key.Type;
        var address = tree.ActiveProjection?.Address() ?? [];
        void Set(object? updated)
        {
            QueueSettingMutation(package, accessor, tree, true, root =>
            {
                using var presentation = new PackageSettingPresentation(root, tree.Nodes.NodeBuilder,
                    SessionAuthoringHistory?.Extensions?.AcquirePackagePresenters(package, accessor.Key, declared), declaredType: declared);
                var target = ResolvePresenter(presentation, root, nativePath) as VirtualNodePresenter
                    ?? throw new InvalidOperationException("The registered package property changed.");
                if (!target.IsVisible || target.IsReadOnly || target.Parent?.IsReadOnly == true) throw new InvalidOperationException("The registered package property is read-only.");
                target.UpdateValue(updated!);
                return presentation.ValueGraphRoot;
            }, address);
        }
        var allowsNull = !virtualNode.MemberAttributes.OfType<NotNullAttribute>().Any()
            && !virtualNode.MemberAttributes.OfType<System.Diagnostics.CodeAnalysis.NotNullAttribute>().Any();
        Gui.PushID(key);
        if (TryDrawPackageSettingValue(package, accessor, virtualNode.Type, label, value, key, allowsNull, editable, Set, virtualNode))
            DrawPackageSettingCommands(package, accessor, virtualNode);
        else if (IsPackageSettingScalar(Nullable.GetUnderlyingType(virtualNode.Type) ?? virtualNode.Type) && value != null)
        {
            DrawPackageSettingScalar(package, accessor, Nullable.GetUnderlyingType(virtualNode.Type) ?? virtualNode.Type, label, value, key, Set, virtualNode, editable);
            DrawPackageSettingCommands(package, accessor, virtualNode);
        }
        else
        {
            var open = Gui.TreeNodeEx(label, SettingExpansionFlags(virtualNode));
            DrawPackageSettingCommands(package, accessor, virtualNode);
            if (open)
            {
                Gui.BeginDisabled(!editable);
                DrawPackageObjectChoice(package, accessor, path, virtualNode.Type, value, Set, allowsNull, virtualNode);
                Gui.EndDisabled();
                if (value != null && !ReferenceEquals(value, tree.Root?.Retrieve()))
                {
                    var parent = tree.ActiveProjection;
                    var projections = parent?.Children ?? tree.Projections;
                    if (!projections.TryGetValue(virtualNode, out var projection))
                    {
                        projections.Add(virtualNode, projection = new(parent, nativePath, virtualNode, package, accessor, SessionAuthoringHistory));
                        packageProjectionParents.Add(projection.Presentation.RootPresenter.Root, projection);
                    }
                    tree.ActiveProjection = projection;
                    try { DrawPackageSettingNode(package, accessor, projection.Root, [], new HashSet<object>(ReferenceEqualityComparer.Instance)); }
                    finally { tree.ActiveProjection = parent; }
                }
                Gui.TreePop();
            }
        }
        Gui.PopID();
    }
    private void DrawPackageSettingInlineValues(Package package, PackageSettingAccessor accessor, IObjectNode node, SettingStep[] path)
    {
        var tree = packageSettingTrees[(package, accessor.Key)];
        var header = SettingNodePresenter(tree, path);
        if (node.TryGetChild("Enabled") is { Type: var enabledType } enabledNode && enabledType == typeof(bool)
            && tree.Presentation?.Find(enabledNode)?.IsVisible == false)
        {
            var enabled = (bool)enabledNode.Retrieve()!;
            Gui.BeginDisabled(!PackageSettingsEditable(package) || !enabledNode.MemberDescriptor.HasSet || PackagePresenterReadOnly(header));
            if (Gui.Checkbox("##Enabled", ref enabled))
            {
                var updated = enabled;
                Execute(() => EditPackageSetting(package, accessor, path, parent => parent[enabledNode.Name].Update(updated)));
            }
            Gui.EndDisabled();
        }
        var inline = header?.Children.FirstOrDefault(child => child.Name == InlineData.InlinedProperty);
        if (inline?.GetNodeAccessor().Node is not IMemberNode member || !node.Members.Contains(member)) return;
        var value = member.Retrieve(); var type = Nullable.GetUnderlyingType(member.Type) ?? member.Type;
        var editable = PackageSettingsEditable(package) && member.MemberDescriptor.HasSet && !PackagePresenterReadOnly(inline);
        var allowsNull = TypeDescriptorFactory.Default.AttributeRegistry.GetAttribute<NotNullAttribute>(member.MemberDescriptor.MemberInfo) == null;
        var key = SettingPath(package, accessor, path, member.Name);
        Gui.PushID(key);
        void Set(object? updated) => EditPackageSetting(package, accessor, path,
            parent => parent[member.Name].Update(tree.Presentation?.NumericRange(member, type)?.Normalize(updated) ?? updated));
        if (!TryDrawPackageSettingValue(package, accessor, member.Type, "", value, key, allowsNull, editable, Set, inline)
            && IsPackageSettingScalar(type) && value != null) DrawPackageSettingScalar(package, accessor, type, "", value, key, Set, inline, editable);
        DrawPackageSettingCommands(package, accessor, inline);
        Gui.PopID();
    }
    private void DrawPackageSettingNode(Package package, PackageSettingAccessor accessor, IObjectNode node, SettingStep[] path, HashSet<object> visited)
    {
        var instance = node.Retrieve();
        if (instance == null || !visited.Add(instance)) return;
        try
        {
            var tree = packageSettingTrees[(package, accessor.Key)];
            DrawPackageSettingInlineValues(package, accessor, node, path);
            if (node.IsEnumerable) { DrawPackageSettingCollection(package, accessor, node, path, visited); return; }
            static IEnumerable<INodePresenter> CategoryChildren(IEnumerable<INodePresenter> children)
            {
                foreach (var child in children)
                {
                    if (child is VirtualNodePresenter && NativePresenterCategories.IsCategory(child))
                    { foreach (var member in CategoryChildren(child.Children)) yield return member; }
                    else yield return child;
                }
            }
            var virtuals = CategoryChildren(SettingNodePresenter(tree, path)?.Children ?? [])
                .OfType<VirtualNodePresenter>().Where(row => row.IsVisible);
            var rows = node.Members.Select(member => (Presenter: tree.Presentation?.Find(member),
                    Member: member, Virtual: (VirtualNodePresenter?)null))
                .Concat(virtuals.Select(row => (Presenter: (INodePresenter?)row, Member: (IMemberNode)null!, Virtual: (VirtualNodePresenter?)row)))
                .Select(row => (row.Presenter, row.Member, row.Virtual,
                    Path: row.Presenter == null ? new[] { new NativeRowOrder(row.Member.MemberDescriptor.Order ?? int.MaxValue, row.Member.Name) } : NativeRowPath(row.Presenter)))
                .OrderBy(row => row.Path, NativeRowPathComparer.Instance);
            var categories = new NativePresenterCategories(category => DrawPackageSettingCommands(package, accessor, category));
            foreach (var row in rows)
            {
                if (row.Presenter != null && (!PresenterPathVisible(row.Presenter) || !categories.Before(row.Presenter, includeSelf: false, includeMemberCategories: false))) continue;
                if (row.Virtual != null) DrawPackageSettingVirtualValue(package, accessor, path, row.Virtual);
                else DrawPackageSettingMember(package, accessor, node, path, visited, row.Member);
            }
        }
        finally { visited.Remove(instance); }
    }
    private void DrawPackageSettingMember(Package package, PackageSettingAccessor accessor, IObjectNode node, SettingStep[] path, HashSet<object> visited, IMemberNode member)
    {
        var presentation = packageSettingTrees[(package, accessor.Key)].Presentation;
        if (TypeDescriptorFactory.Default.AttributeRegistry.GetAttribute<BrowsableAttribute>(member.MemberDescriptor.MemberInfo)?.Browsable == false) return;
        var display = TypeDescriptorFactory.Default.AttributeRegistry.GetAttribute<DisplayAttribute>(member.MemberDescriptor.MemberInfo);
        var presenter = presentation?.Find(member);
        if (presenter?.IsVisible == false) return;
        var label = presenter?.DisplayName ?? display?.Name ?? member.Name; var key = SettingPath(package, accessor, path, member.Name);
        var editable = PackageSettingsEditable(package) && member.MemberDescriptor.HasSet && !PackagePresenterReadOnly(presenter)
            && !PackagePresenterReadOnly(SettingNodePresenter(packageSettingTrees[(package, accessor.Key)], path));
        Gui.PushID(key);
        var value = member.Retrieve(); var type = Nullable.GetUnderlyingType(member.Type) ?? member.Type;
        var allowsNull = TypeDescriptorFactory.Default.AttributeRegistry.GetAttribute<NotNullAttribute>(member.MemberDescriptor.MemberInfo) == null
            && TypeDescriptorFactory.Default.AttributeRegistry.GetAttribute<System.Diagnostics.CodeAnalysis.NotNullAttribute>(member.MemberDescriptor.MemberInfo) == null;
        if (TryDrawPackageSettingValue(package, accessor, member.Type, label, value, key, allowsNull, editable,
            updated => EditPackageSetting(package, accessor, path, parent => parent[member.Name].Update(updated)), presenter))
        { DrawPackageSettingCommands(package, accessor, presenter); }
        else if (IsPackageSettingScalar(type) && value != null)
        {
            DrawPackageSettingScalar(package, accessor, type, label, value, key,
                updated => EditPackageSetting(package, accessor, path, parent => parent[member.Name].Update(presentation?.NumericRange(member, type)?.Normalize(updated) ?? updated)), presenter, editable);
            DrawPackageSettingCommands(package, accessor, presenter);
        }
        else
        {
            var open = Gui.TreeNodeEx(label, SettingExpansionFlags(presenter));
            DrawPackageSettingCommands(package, accessor, presenter);
            if (open)
            {
            Gui.BeginDisabled(!editable);
            DrawPackageObjectChoice(package, accessor, path, member.Type, value,
                updated => EditPackageSetting(package, accessor, path, parent => parent[member.Name].Update(updated), true), allowsNull, presenter);
            Gui.EndDisabled();
            if (member.Target is { } child) DrawPackageSettingNode(package, accessor, child, [.. path, new(member.Name, NodeIndex.Empty)], visited);
            Gui.TreePop();
            }
        }
        Gui.PopID();
    }
    private void DrawPackageObjectChoice(Package package, PackageSettingAccessor accessor, SettingStep[] path, Type declared, object? value,
        Action<object?> set, bool allowNull = true, INodePresenter? presenter = null)
    {
        if (declared.IsValueType && Nullable.GetUnderlyingType(declared) == null) return;
        var entries = PackageFactoryEntries(presenter, Nullable.GetUnderlyingType(declared) ?? declared, allowNull);
        if (!Gui.BeginCombo("##Type", NativeAbstractEntryControl.Label(entries, value))) return;
        try
        {
            NativeAbstractEntryControl.Choices(entries, value, entry => Execute(() =>
                SelectPackageFactoryEntry(package, accessor, presenter, entries, entry, value, set, "CreateNewInstance")));
        }
        finally { Gui.EndCombo(); }
    }
    private static MemberCollectionAttribute? SettingCollectionMetadata(IObjectNode root, SettingStep[] path, IObjectNode node)
    {
        if (path.Length != 0 && path[^1].Member is { } name)
        {
            var parent = ResolveSettingNode(root, path[..^1]);
            var memberMetadata = TypeDescriptorFactory.Default.AttributeRegistry.GetAttribute<MemberCollectionAttribute>(parent[name].MemberDescriptor.MemberInfo);
            if (memberMetadata != null) return memberMetadata;
        }
        return node.Descriptor.Attributes.OfType<MemberCollectionAttribute>().FirstOrDefault();
    }
    private void DrawPackageSettingCollection(Package package, PackageSettingAccessor accessor, IObjectNode node, SettingStep[] path, HashSet<object> visited)
    {
        var value = node.Retrieve()!;
        var dictionary = node.Descriptor as DictionaryDescriptor;
        var collection = node.Descriptor as CollectionDescriptor;
        var array = node.Descriptor as ArrayDescriptor;
        if (dictionary == null && collection == null && array == null) return;
        var readOnly = !PackageSettingsEditable(package) || (dictionary?.IsReadOnly(value) ?? collection?.IsReadOnly(value) ?? false)
            || PackagePresenterReadOnly(SettingNodePresenter(packageSettingTrees[(package, accessor.Key)], path));
        var element = dictionary?.ValueType ?? collection?.ElementType ?? array!.ElementType;
        var root = packageSettingTrees[(package, accessor.Key)].Root!;
        var collectionMetadata = SettingCollectionMetadata(root, path, node);
        var canChangeStructure = collectionMetadata?.ReadOnly != true;
        var virtualRows = SettingNodePresenter(packageSettingTrees[(package, accessor.Key)], path)?.Children.OfType<VirtualNodePresenter>().Where(row => row.IsVisible) ?? [];
        var rows = node.Indices!.Select((index, ordinal) => (Order: packageSettingTrees[(package, accessor.Key)].Presentation?.Find(node, index)?.Order ?? ordinal,
                Index: index, Virtual: (VirtualNodePresenter?)null))
            .Concat(virtualRows.Select(row => (Order: row.Order ?? int.MaxValue, Index: NodeIndex.Empty, Virtual: (VirtualNodePresenter?)row)))
            .OrderBy(row => row.Order).ToArray();
        foreach (var row in rows)
        {
            if (row.Virtual != null) { DrawPackageSettingVirtualValue(package, accessor, path, row.Virtual); continue; }
            var index = row.Index;
            var item = node.Retrieve(index); var key = SettingPath(package, accessor, path, "[" + index + "]");
            var itemPresenter = packageSettingTrees[(package, accessor.Key)].Presentation?.Find(node, index);
            if (itemPresenter?.IsVisible == false) continue;
            Gui.PushID(key);
            var renamedKeyHeader = DrawPackageDictionaryKey(package, accessor, node, path, index,
                itemPresenter, !readOnly && canChangeStructure && !PackagePresenterReadOnly(itemPresenter));
            if (!SameSettingValue(accessor, accessor.Get(package), packageSettingTrees[(package, accessor.Key)].Source))
            { Gui.PopID(); return; }
            var itemLabel = renamedKeyHeader ? "" : itemPresenter?.DisplayName ?? index.ToString();
            var type = Nullable.GetUnderlyingType(element) ?? element;
            if (TryDrawPackageSettingValue(package, accessor, element, itemLabel, item, key, collectionMetadata?.NotNullItems != true, !readOnly && itemPresenter?.IsReadOnly != true,
                updated => EditPackageSetting(package, accessor, path, parent => parent.Update(updated, index)), itemPresenter))
            { DrawPackageSettingCommands(package, accessor, itemPresenter); }
            else if (IsPackageSettingScalar(type) && item != null)
            {
                DrawPackageSettingScalar(package, accessor, type, itemLabel.Length == 0 ? "##DictionaryValue" : itemLabel, item, key,
                    updated => EditPackageSetting(package, accessor, path, parent => parent.Update(updated, index)), itemPresenter, !readOnly && itemPresenter?.IsReadOnly != true);
                DrawPackageSettingCommands(package, accessor, itemPresenter);
            }
            else
            {
                var open = Gui.TreeNodeEx(itemLabel.Length == 0 ? "##DictionaryValue" : itemLabel, SettingExpansionFlags(itemPresenter));
                DrawPackageSettingCommands(package, accessor, itemPresenter);
                if (open)
                {
                Gui.BeginDisabled(readOnly || PackagePresenterReadOnly(itemPresenter));
                DrawPackageObjectChoice(package, accessor, path, element, item,
                    updated => EditPackageSetting(package, accessor, path, parent => parent.Update(updated, index), true), collectionMetadata?.NotNullItems != true, itemPresenter);
                Gui.EndDisabled();
                if (node.ItemReferences != null && node.IndexedTarget(index) is { } child) DrawPackageSettingNode(package, accessor, child, [.. path, new(null, index)], visited);
                Gui.TreePop();
                }
            }
            if (canChangeStructure && array == null && (dictionary != null || collection != null && (collection.HasRemoveAt || collection.HasRemove)
                && AssetCollectionCommandRules.CanAdd(element)))
            {
                DrawPackageCollectionRemove(package, accessor, node, path, index, itemPresenter, readOnly);
            }
            Gui.PopID();
            if (!SameSettingValue(accessor, accessor.Get(package), packageSettingTrees[(package, accessor.Key)].Source)) return;
        }
        // Original array providers expose fixed element editing, not collection
        // resize commands. Explicit extension commands remain on the native menu.
        if (array != null || !canChangeStructure) return;
        if (dictionary != null)
        {
            DrawPackageDictionaryAdd(package, accessor, node, path, readOnly, collectionMetadata);
            return;
        }
        void Add(object? newItem)
        {
            if (newItem == null && collectionMetadata?.NotNullItems == true) throw new InvalidOperationException("This setting collection does not allow null items.");
            EditPackageSetting(package, accessor, path, parent => parent.Add(newItem!), true);
        }
        if (collection != null && (!collection.HasAdd || !AssetCollectionCommandRules.CanAdd(element))) return;
        Gui.BeginDisabled(readOnly || !canChangeStructure);
        var collectionPresenter = SettingNodePresenter(packageSettingTrees[(package, accessor.Key)], path);
        if (collectionPresenter?.AttachedProperties.ContainsKey(AbstractNodeEntryData.Key) != true)
        {
            DrawPackageCollectionDefaultAdd(package, accessor, node, path, readOnly);
            Gui.EndDisabled();
            return;
        }
        var entries = PackageFactoryEntries(collectionPresenter, element,
            collectionMetadata?.NotNullItems != true && (!element.IsValueType || Nullable.GetUnderlyingType(element) != null));
        if (Gui.SmallButton("+"))
        {
            Gui.OpenPopup("##AddSettingItem");
        }
        if (Gui.BeginPopup("##AddSettingItem"))
        {
            using var referencePhase = BeginPackageReferenceCommitPhase(package, accessor);
            if (ContentAssetReference.Supports(element)) DrawContentReferenceValue(element, null, false, reference =>
                referencePhase.Submit(() => Add(reference)));
            NativeAbstractEntryControl.Choices(entries, null, entry =>
            {
                Execute(() => SelectPackageFactoryEntry(package, accessor, collectionPresenter, entries, entry, null, Add, "AddNewItem"));
                Gui.CloseCurrentPopup();
            });
            Gui.EndPopup();
        }
        Gui.EndDisabled();
    }
    private static Type[] SettingItemTypes(Type declared)
        => declared.GetInheritedInstantiableTypes().Append(declared).Distinct().Where(candidate => !candidate.IsAbstract && !candidate.IsInterface
            && !candidate.ContainsGenericParameters && !candidate.IsDefined(typeof(NonInstantiableAttribute), true)
            && (candidate.IsValueType || candidate.IsArray && candidate.GetArrayRank() == 1 || ObjectFactoryRegistry.CanCreateInstance(candidate)))
            .OrderBy(candidate => DisplayAttribute.GetDisplayName(candidate) ?? candidate.Name, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.FullName, StringComparer.Ordinal).ToArray();
    private static object? NewSettingItem(Type type) => type == typeof(string) ? ""
        : TypeDescriptorFactory.Default.Find(type) is ArrayDescriptor array ? array.CreateArray(0) : ObjectFactoryRegistry.NewInstance(type);
}
