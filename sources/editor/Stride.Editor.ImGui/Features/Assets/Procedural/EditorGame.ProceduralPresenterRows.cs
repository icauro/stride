using Stride.Core.Presentation.Quantum.Presenters;
using Stride.Core.Quantum;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private static void ApplyProceduralExpansion(Stride.Core.ExpandRule expansion)
    {
        if (expansion != Stride.Core.ExpandRule.Auto)
            Gui.SetNextItemOpen(expansion != Stride.Core.ExpandRule.Never, Hexa.NET.ImGui.ImGuiCond.Once);
    }
    private void DrawProceduralFields(ProceduralAssetDocument asset, IEnumerable<AssetPropertyField> fields,
        HashSet<IObjectNode>? ancestors = null)
    {
        var categories = PropertyGrid.ProceduralRows.Categories(asset, Gui.GetFrameCount(), () => new(presenter =>
        {
            if (!Gui.BeginPopupContextItem("Category actions")) return;
            try { DrawProceduralPresenterCommands(asset, presenter); }
            finally { Gui.EndPopup(); }
        }));
        var targets = new List<EditorPresenterTarget>();
        try
        {
            var authored = fields.Select(field =>
            {
                var target = asset.AcquireExtensionCommandTarget(field.Node);
                if (target != null) targets.Add(target);
                return (Field: field, Presenter: target?.Presenter);
            }).ToArray();
            var containers = authored.Where(row => row.Presenter != null).Select(row => NativeRowContainer(row.Presenter!)).ToHashSet();
            if (ancestors == null && asset.PresentationRoot is { } root) containers.Add(root);
            foreach (var row in authored.Where(row => row.Presenter != null))
                for (var ancestor = NativeVirtualAncestor(row.Presenter!); ancestor != null; ancestor = NativeVirtualAncestor(ancestor))
                    containers.Add(NativeRowContainer(ancestor));
            var virtuals = asset.VirtualProperties.Where(presenter => !NativePresenterCategories.IsCategory(presenter)
                && NativeVirtualAncestor(presenter) == null && containers.Contains(NativeRowContainer(presenter)));
            var rows = authored.Where(row => row.Presenter == null || NativeVirtualAncestor(row.Presenter) == null)
                .Select(row => (row.Field, row.Presenter, Virtual: (VirtualNodePresenter?)null,
                    Path: row.Presenter != null ? NativeRowPath(row.Presenter) : [new NativeRowOrder(int.MaxValue, row.Field.Name)]))
                .Concat(virtuals.Select(presenter => (Field: (AssetPropertyField)null!, Presenter: (INodePresenter?)presenter,
                    Virtual: (VirtualNodePresenter?)presenter, Path: NativeRowPath(presenter))))
                .OrderBy(row => row.Path, NativeRowPathComparer.Instance).ToArray();
            foreach (var row in rows)
            {
                if (row.Presenter != null && (!PresenterPathVisible(row.Presenter)
                    || !categories.Before(row.Presenter, includeSelf: false, includeMemberCategories: false))) continue;
                if (row.Virtual != null)
                {
                    if (PropertyGrid.ProceduralRows.Consume(asset, row.Virtual)) DrawProceduralVirtualProperty(asset, row.Virtual, categories, ancestors);
                }
                else DrawAssetProperty(asset, row.Field, ancestors);
            }
        }
        finally
        {
            List<Exception>? failures = null;
            foreach (var target in targets)
                try { target.Dispose(); } catch (Exception error) { packageCommandCleanup.Add(target.Dispose); (failures ??= []).Add(error); }
            if (failures != null) throw new AggregateException("Native procedural presenter release requires recovery.", failures);
        }
    }
    private void DrawProceduralPresenterCommands(ProceduralAssetDocument asset, INodePresenter presenter)
    {
        foreach (var command in presenter.Commands)
            if (DrawNativeMenuItem(command.Name, "", false, !IsAuthoringPipelineUpdating && ReferenceEquals(assetDocument, asset)
                && asset.CanEdit && command.CanExecute([presenter], null)))
                Execute(() =>
                {
                    ValidatePendingPropertyStrings();
                    if (!ReferenceEquals(assetDocument, asset)) throw new InvalidOperationException("The procedural selection changed.");
                    var target = asset.AcquirePresenterTarget(presenter);
                    StartEditorExtensionCommand(asset, target, command, null,
                        () => new EditorAssetCommandMutation(asset.BeginExtensionCommandMutation()));
                });
    }
}
