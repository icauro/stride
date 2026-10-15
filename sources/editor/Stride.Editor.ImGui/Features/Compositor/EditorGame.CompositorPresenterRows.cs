using Stride.Core.Presentation.Quantum.Presenters;
using Stride.Core.Quantum;
using System.Runtime.CompilerServices;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private sealed class CompositorPresenterRenderScope(int frame)
    {
        internal readonly int Frame = frame;
        internal readonly Dictionary<INodePresenter, INodePresenter[]> Groups = [];
        internal NativePresenterCategories Categories = null!;
    }
    private readonly ConditionalWeakTable<AssetMetadataDocument, CompositorPresenterRenderScope> compositorPresenterRenderScopes = new();
    private CompositorPresenterRenderScope GetCompositorPresenterRenderScope(AssetMetadataDocument source, CompositorEditorState state)
    {
        var frame = Gui.GetFrameCount();
        if (compositorPresenterRenderScopes.TryGetValue(source, out var existing) && existing.Frame == frame) return existing;
        var scope = new CompositorPresenterRenderScope(frame);
        var blocks = state.SelectedBlocks.ToArray(); var items = state.SelectedItems.ToArray(); var epoch = source.SessionHistory?.Extensions?.Version;
        scope.Categories = new NativePresenterCategories(presenter =>
        {
            if (!scope.Groups.TryGetValue(presenter, out var group) || !Gui.BeginPopupContextItem("Category actions")) return;
            try
            {
                bool Current() => compositorEditors.TryGetValue(source, out var current) && ReferenceEquals(current, state)
                    && current.SelectedBlocks.SetEquals(blocks) && current.SelectedItems.SetEquals(items)
                    && epoch == source.SessionHistory?.Extensions?.Version
                    && group.All(node => node is VirtualNodePresenter virtualNode && source.ExtensionVirtualProperties.Contains(virtualNode));
                DrawNativePresenterCommands(source, Current,
                    () => AcquireHierarchyFactoryTargets(group.Length, index => source.AcquireExtensionVirtualTarget((VirtualNodePresenter)group[index])),
                    () => new EditorAssetCommandMutation(source.BeginExtensionCommandMutation()));
            }
            finally { Gui.EndPopup(); }
        }, presenter => scope.Groups.TryGetValue(presenter, out var group) ? group.Any(PresenterPathVisible) : PresenterPathVisible(presenter));
        compositorPresenterRenderScopes.Remove(source); compositorPresenterRenderScopes.Add(source, scope);
        return scope;
    }
    private bool DrawNativeCompositorAggregateRows(AssetMetadataDocument source, CompositorEditorState state,
        IReadOnlyList<CompositorAggregateProperty> properties, IReadOnlyList<IObjectNode>? roots,
        IReadOnlyList<INodePresenter>? nativeParents = null)
    {
        var leases = new List<EditorPresenterTarget>();
        try
        {
            var authored = properties.Select(property =>
            {
                var presenters = property.Fields.Select(field =>
                {
                    var target = source.AcquireCompositorFactoryTarget(field);
                    if (target != null) leases.Add(target);
                    return target?.Presenter;
                }).ToArray();
                return (Property: property, Presenters: presenters);
            }).ToArray();
            var containers = new List<INodePresenter[]>();
            if (nativeParents != null && nativeParents.Count != 0) containers.Add(nativeParents.ToArray());
            if (roots != null)
            {
                var actual = roots.Select(root =>
                {
                    var target = source.AcquireCompositorRootPresenter(root);
                    if (target != null) leases.Add(target);
                    return target?.Presenter;
                }).ToArray();
                if (actual.Length != 0 && actual.All(presenter => presenter != null)) containers.Add(actual.Select(presenter => presenter!).ToArray());
            }
            foreach (var row in authored.Where(row => row.Presenters.All(presenter => presenter != null)))
            {
                var parents = row.Presenters.Select(presenter => NativeRowContainer(presenter!)).ToArray();
                if (parents.Any(parent => parent == null)) continue;
                if (!containers.Any(existing => existing.SequenceEqual(parents))) containers.Add(parents.Select(parent => parent!).ToArray());
            }
            if (containers.Count == 0) return false;
            var virtuals = new List<VirtualNodePresenter[]>();
            var scope = GetCompositorPresenterRenderScope(source, state);
            var categoryGroups = scope.Groups;
            void Visit(IReadOnlyList<INodePresenter> parents)
            {
                foreach (var group in NodePresenterCombination.CombineChildren(parents))
                {
                    if (!NodePresenterCombination.CanConstruct(group) || group.All(presenter => !PresenterPathVisible(presenter))) continue;
                    if (group.All(NativePresenterCategories.IsCategory))
                    {
                        categoryGroups[group[0]] = group.ToArray(); Visit(group); continue;
                    }
                    if (group.All(presenter => presenter is VirtualNodePresenter))
                    {
                        var actual = group.Cast<VirtualNodePresenter>().ToArray();
                        if (!virtuals.Any(existing => existing.SequenceEqual(actual))) virtuals.Add(actual);
                    }
                }
            }
            foreach (var container in containers) Visit(container);
            var categories = scope.Categories;
            var rows = authored.Select(row => (Property: (CompositorAggregateProperty?)row.Property, Virtual: (VirtualNodePresenter[]?)null,
                Presenters: row.Presenters, Path: row.Presenters[0] is { } first ? NativeRowPath(first)
                    : new[] { new NativeRowOrder(row.Property.First.NativeCategoryOrder ?? row.Property.First.CategoryMetadata?.Order ?? int.MaxValue, row.Property.First.Category),
                        new NativeRowOrder(row.Property.First.Order, row.Property.Key) }))
                .Concat(virtuals.Select(group => (Property: (CompositorAggregateProperty?)null, Virtual: (VirtualNodePresenter[]?)group,
                    Presenters: group.Select(node => (INodePresenter?)node).ToArray(), Path: NativeRowPath(group[0]))))
                .OrderBy(row => row.Path, NativeRowPathComparer.Instance);
            foreach (var row in rows)
            {
                if (row.Virtual != null) DrawCompositorVirtualRow(source, row.Virtual, categories);
                else
                {
                    if (row.Presenters.Where(presenter => presenter != null).Any()
                        && row.Presenters.Where(presenter => presenter != null).All(presenter => !PresenterPathVisible(presenter!))) continue;
                    var presenter = row.Presenters.FirstOrDefault(presenter => presenter != null);
                    if (presenter != null && !categories.Before(presenter, includeSelf: false, includeMemberCategories: false)) continue;
                    DrawCompositorAggregateProperty(source, state, row.Property!);
                }
            }
            return true;
        }
        finally
        {
            List<Exception>? failures = null;
            foreach (var lease in leases)
                try { lease.Dispose(); }
                catch (Exception error) { packageCommandCleanup.Add(lease.Dispose); (failures ??= []).Add(error); }
            if (failures != null) throw new AggregateException("Native compositor presenter release requires recovery.", failures);
        }
    }
}
