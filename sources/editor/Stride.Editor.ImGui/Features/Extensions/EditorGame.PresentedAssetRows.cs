using System.Runtime.CompilerServices;
using Hexa.NET.ImGui;
using Stride.Core;
using Stride.Core.Quantum;
using Stride.Core.Presentation.Quantum.Presenters;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private sealed record PresentedAssetRow(IMemberNode Node, string Category, int? Order, ExpandRule Expansion, Action Draw, int? CategoryOrder = null);
    private sealed class AssetPresenterRenderScope(int frame, NativePresenterCategories categories)
    {
        internal readonly int Frame = frame;
        internal readonly NativePresenterCategories Categories = categories;
        internal readonly ConditionalWeakTable<VirtualNodePresenter, object> Consumed = new();
        internal readonly Dictionary<string, bool> FallbackCategories = new(StringComparer.Ordinal);
    }
    private readonly ConditionalWeakTable<AssetMetadataDocument, AssetPresenterRenderScope> assetPresenterRenderScopes = new();
    private AssetPresenterRenderScope GetAssetPresenterRenderScope(AssetMetadataDocument owner)
    {
        var frame = Gui.GetFrameCount();
        if (assetPresenterRenderScopes.TryGetValue(owner, out var scope) && scope.Frame == frame) return scope;
        BeginAssetPresenterRows(owner);
        return assetPresenterRenderScopes.GetValue(owner, _ => new(frame, CreateAssetPresenterCategories(owner)));
    }
    private void BeginAssetPresenterRows(AssetMetadataDocument owner)
    {
        assetPresenterRenderScopes.Remove(owner);
        assetPresenterRenderScopes.Add(owner, new(Gui.GetFrameCount(), CreateAssetPresenterCategories(owner)));
    }
    internal static INodePresenter? NativeRowContainer(INodePresenter presenter)
    {
        var parent = presenter.Parent;
        while (parent is VirtualNodePresenter && NativePresenterCategories.IsCategory(parent)) parent = parent.Parent;
        return parent;
    }
    internal static bool PresenterPathVisible(INodePresenter presenter)
    {
        for (INodePresenter? current = presenter; current != null; current = current.Parent) if (!current.IsVisible) return false;
        return true;
    }
    internal readonly record struct NativeRowOrder(int Order, string Name);
    internal static NativeRowOrder[] NativeRowPath(INodePresenter presenter)
    {
        var path = new List<NativeRowOrder>();
        for (var current = presenter; current.Parent != null; current = current.Parent)
            path.Add(new(current.Order ?? int.MaxValue, current.Name));
        path.Reverse(); return path.ToArray();
    }
    internal sealed class NativeRowPathComparer : IComparer<NativeRowOrder[]>
    {
        internal static readonly NativeRowPathComparer Instance = new();
        public int Compare(NativeRowOrder[]? left, NativeRowOrder[]? right)
        {
            if (left == null || right == null) return left == right ? 0 : left == null ? -1 : 1;
            for (var index = 0; index < Math.Min(left.Length, right.Length); index++)
            {
                var order = left[index].Order.CompareTo(right[index].Order);
                if (order != 0) return order;
                order = StringComparer.Ordinal.Compare(left[index].Name, right[index].Name);
                if (order != 0) return order;
            }
            return left.Length.CompareTo(right.Length);
        }
    }
    private void DrawPresentedAssetRows(AssetMetadataDocument owner, IEnumerable<PresentedAssetRow> source,
        IEnumerable<INodePresenter>? nativeContainers = null,
        Action<VirtualNodePresenter, NativePresenterCategories>? drawVirtual = null)
    {
        var scope = GetAssetPresenterRenderScope(owner);
        var targets = new List<EditorPresenterTarget>();
        try
        {
            var authored = source.Select(row =>
            {
                var target = owner.AcquireExtensionCommandTarget(row.Node);
                if (target != null) targets.Add(target);
                return (Row: row, Presenter: target?.Presenter);
            }).ToArray();
            var containers = authored.Where(row => row.Presenter != null).Select(row => NativeRowContainer(row.Presenter!)).ToHashSet();
            if (nativeContainers != null) foreach (var container in nativeContainers) containers.Add(container);
            var virtuals = owner.ExtensionVirtualProperties.Where(presenter => !NativePresenterCategories.IsCategory(presenter)
                && containers.Contains(NativeRowContainer(presenter)) && !scope.Consumed.TryGetValue(presenter, out _)).ToArray();
            var rows = authored.Select(row => (row.Row, row.Presenter, Virtual: (VirtualNodePresenter?)null,
                    Path: row.Presenter == null ? new[] { new NativeRowOrder(row.Row.Category.Length == 0 ? int.MinValue : row.Row.CategoryOrder ?? int.MaxValue, row.Row.Category), new NativeRowOrder(row.Row.Order ?? int.MaxValue, row.Row.Node.Name) } : NativeRowPath(row.Presenter)))
                .Concat(virtuals.Select(presenter => (Row: (PresentedAssetRow)null!, Presenter: (INodePresenter?)presenter, Virtual: (VirtualNodePresenter?)presenter, Path: NativeRowPath(presenter))))
                .OrderBy(row => row.Path, NativeRowPathComparer.Instance);
            foreach (var row in rows)
            {
                if (row.Virtual != null)
                {
                    scope.Consumed.GetValue(row.Virtual, _ => new object());
                    if (drawVirtual != null) drawVirtual(row.Virtual, scope.Categories);
                    else DrawExtensionVirtualProperties(owner, [row.Virtual], scope.Categories);
                    continue;
                }
                if (row.Presenter != null)
                {
                    if (!PresenterPathVisible(row.Presenter)) continue;
                    if (!scope.Categories.Before(row.Presenter, includeSelf: false, includeMemberCategories: false)) continue;
                }
                else if (row.Row.Category.Length > 0)
                {
                    if (!scope.FallbackCategories.TryGetValue(row.Row.Category, out var open))
                    {
                        Gui.SetNextItemOpen(row.Row.Expansion is ExpandRule.Always or ExpandRule.Once, ImGuiCond.Once);
                        open = Gui.CollapsingHeader(row.Row.Category); scope.FallbackCategories.Add(row.Row.Category, open);
                    }
                    if (!open) continue;
                }
                row.Row.Draw();
            }
        }
        finally
        {
            List<Exception>? failures = null;
            foreach (var target in targets)
            {
                try { target.Dispose(); }
                catch (Exception error) { packageCommandCleanup.Add(target.Dispose); (failures ??= []).Add(error); }
            }
            if (failures != null) throw new AggregateException("Native property presenter release requires recovery.", failures);
        }
    }
}
