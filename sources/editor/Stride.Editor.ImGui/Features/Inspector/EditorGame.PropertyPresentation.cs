using Hexa.NET.ImGui;
using Stride.Core;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private NativePresenterCategories? componentPresenterCategories;
    private HashSet<Stride.Core.Presentation.Quantum.Presenters.VirtualNodePresenter>? componentVirtualRowsDrawn;
    private void DrawComponentFields(IReadOnlyList<ComponentField> fields)
    {
        var nested = componentPresenterCategories != null;
        var owner = document!;
        var targets = new List<EditorPresenterTarget>();
        componentVirtualRowsDrawn ??= [];
        componentPresenterCategories ??= new(presenter =>
        {
            if (presenter is not Stride.Core.Presentation.Quantum.Presenters.VirtualNodePresenter category || !Gui.BeginPopupContextItem("Category actions")) return;
            try { DrawComponentVirtualCommands(owner, fields, category); }
            finally { Gui.EndPopup(); }
        });
        try
        {
            var authored = fields.Select(field =>
            {
                var target = field.Presentation?.AcquireCommandTarget(field);
                if (target != null) targets.Add(target);
                return (Field: field, Presenter: target?.Presenter);
            }).ToArray();
            var containers = authored.Where(row => row.Presenter != null).Select(row => NativeRowContainer(row.Presenter!)).ToHashSet();
            foreach (var row in authored.Where(row => row.Presenter != null))
                for (var ancestor = NativeVirtualAncestor(row.Presenter!); ancestor != null; ancestor = NativeVirtualAncestor(ancestor)) containers.Add(NativeRowContainer(ancestor));
            var virtuals = fields.Select(field => field.Presentation).Distinct().Where(value => value != null)
                .SelectMany(value => value!.VirtualRows).Where(row => !NativePresenterCategories.IsCategory(row)
                    && containers.Contains(NativeRowContainer(row)) && NativeVirtualAncestor(row) == null && !componentVirtualRowsDrawn.Contains(row)).Distinct();
            var rows = authored.Where(row => row.Presenter == null || NativeVirtualAncestor(row.Presenter) == null).Select(row => (row.Field, row.Presenter, Virtual: (Stride.Core.Presentation.Quantum.Presenters.VirtualNodePresenter?)null,
                    Path: row.Presenter == null ? new[] { new NativeRowOrder(row.Field.DisplayOrder ?? int.MaxValue, row.Field.Name) } : NativeRowPath(row.Presenter)))
                .Concat(virtuals.Select(row => (Field: (ComponentField)null!, Presenter: (Stride.Core.Presentation.Quantum.Presenters.INodePresenter?)row,
                    Virtual: (Stride.Core.Presentation.Quantum.Presenters.VirtualNodePresenter?)row, Path: NativeRowPath(row))))
                .OrderBy(row => row.Path, NativeRowPathComparer.Instance);
            foreach (var row in rows)
            {
                if (row.Presenter != null && (!PresenterPathVisible(row.Presenter)
                    || !componentPresenterCategories.Before(row.Presenter, includeSelf: false, includeMemberCategories: false))) continue;
                if (row.Virtual != null)
                {
                    componentVirtualRowsDrawn.Add(row.Virtual); DrawComponentVirtualProperty(owner, fields, row.Virtual);
                }
                else DrawComponentField(row.Field);
            }
        }
        finally
        {
            if (!nested) { componentPresenterCategories = null; componentVirtualRowsDrawn = null; }
            List<Exception>? failures = null;
            foreach (var target in targets) try { target.Dispose(); }
                catch (Exception error) { packageCommandCleanup.Add(target.Dispose); (failures ??= []).Add(error); }
            if (failures != null) throw new AggregateException("Native component presenter release requires recovery.", failures);
        }
    }

    private static void ApplyPropertyExpansion(ComponentField field)
    {
        if (field.IsCategoryHeader) { Gui.SetNextItemOpen(true, ImGuiCond.Once); return; }
        if (field.Expansion == ExpandRule.Auto) return;
        Gui.SetNextItemOpen(field.Expansion != ExpandRule.Never, ImGuiCond.Once);
    }

    private void DrawInlineEnabled(IReadOnlyList<ComponentField> fields, System.Numerics.Vector2? position = null)
    {
        var enabled = fields.FirstOrDefault(member => member.Node.Name == "Enabled" && member.Type == typeof(bool) && !member.IsVisible);
        if (enabled == null) return;
        if (position is { } headerPosition) Gui.SetCursorScreenPos(headerPosition); else Gui.SameLine();
        Gui.PushID(enabled.Name); var value = (bool)(enabled.Node.Retrieve() ?? false);
        var inherited = document!.CanOverrideInheritedComponent(enabled);
        Gui.BeginDisabled((!document.CanEditComponentValue(enabled) && !inherited) || !enabled.SupportsEditing);
        if (Gui.Checkbox("##Enabled", ref value)) Execute(() =>
        {
            if (inherited) document.EditInheritedScalar(enabled, value);
            else { document.UpdateComponent(enabled, value); document.EndComponentEdit(); }
        });
        if (inherited) DrawInheritedResetAction(enabled);
        Gui.EndDisabled(); Gui.PopID();
    }
}
