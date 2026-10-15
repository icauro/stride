using Stride.Core.Presentation.Quantum.Presenters;

namespace Stride.Editor.ImGui;

internal sealed partial class UIEditorPanel
{
    private void DrawPresentedUIProperties(UIDocument source, Guid id)
    {
        var targets = new List<EditorPresenterTarget>();
        try
        {
            var authored = source.CommonProperties(selected).Select(field =>
            {
                var target = source.AcquireExtensionCommandTarget(field.Node);
                if (target != null) targets.Add(target);
                return (Field: field, Presenter: target?.Presenter);
            }).ToArray();
            var groups = source.CombinedExtensionVirtualProperties();
            var categories = CreateUIPresenterCategories(source, groups);
            var containers = authored.Where(row => row.Presenter != null).Select(row => EditorGame.NativeRowContainer(row.Presenter!)).ToHashSet();
            var virtuals = groups.Where(group => !NativePresenterCategories.IsCategory(group[0])
                && containers.Contains(EditorGame.NativeRowContainer(group[0]))).ToArray();
            var rows = authored.Select(row => (row.Field, row.Presenter, Virtual: (IReadOnlyList<VirtualNodePresenter>?)null,
                    Path: row.Presenter == null ? new[] { new EditorGame.NativeRowOrder(row.Field.Node.MemberDescriptor.Order ?? int.MaxValue, row.Field.Name) } : EditorGame.NativeRowPath(row.Presenter)))
                .Concat(virtuals.Select(group => (Field: (UIElementProperty)null!, Presenter: (INodePresenter?)group[0],
                    Virtual: (IReadOnlyList<VirtualNodePresenter>?)group, Path: EditorGame.NativeRowPath(group[0]))))
                .OrderBy(row => row.Path, EditorGame.NativeRowPathComparer.Instance);
            foreach (var row in rows)
            {
                if (row.Presenter != null && !categories.Before(row.Presenter, includeSelf: false, includeMemberCategories: false)) continue;
                if (row.Virtual != null) DrawNativeExtensionRows(source, [row.Virtual], categories);
                else DrawPresentedUIField(id, row.Field);
            }
            var drawn = virtuals.Select(group => group[0]).ToHashSet();
            DrawNativeExtensionRows(source, groups.Where(group => !NativePresenterCategories.IsCategory(group[0]) && !drawn.Contains(group[0])).ToArray(), categories);
        }
        finally
        {
            List<Exception>? failures = null;
            foreach (var target in targets)
                try { target.Dispose(); }
                catch (Exception error) { source.RetainFailedPresenterTarget(target); (failures ??= []).Add(error); }
            if (failures != null) throw new AggregateException("Native UI presenter release requires recovery.", failures);
        }
    }
}
