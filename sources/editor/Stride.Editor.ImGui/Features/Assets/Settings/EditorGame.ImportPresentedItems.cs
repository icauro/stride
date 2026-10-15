using Stride.Core;
using Stride.Core.Assets.Editor.Quantum.NodePresenters.Keys;
using Stride.Core.Presentation.Quantum.Presenters;
using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private sealed record PresentedImportItem(AssetImportSettingItem Item, EditorPresenterTarget? Target);
    private IEnumerable<PresentedImportItem> PresentedImportItems(AssetMetadataDocument owner, AssetImportSettingField field)
    {
        var targets = new List<EditorPresenterTarget>();
        try
        {
            var rows = field.Items.Select(item =>
            {
                var target = owner.AcquireImportItemCommandTarget(item);
                if (target != null) targets.Add(target);
                return new PresentedImportItem(item, target);
            }).OrderBy(row => row.Target == null ? [] : NativeRowPath(row.Target.Presenter), NativeRowPathComparer.Instance);
            var categories = GetAssetPresenterRenderScope(owner).Categories;
            foreach (var row in rows)
            {
                if (!owner.IsExtensionItemVisible(row.Item.Collection, row.Item.Index)) continue;
                if (row.Target != null && (!PresenterPathVisible(row.Target.Presenter)
                    || !categories.Before(row.Target.Presenter, includeSelf: false, includeMemberCategories: false))) continue;
                yield return row;
            }
        }
        finally
        {
            List<Exception>? failures = null;
            foreach (var target in targets)
                try { target.Dispose(); }
                catch (Exception error) { packageCommandCleanup.Add(target.Dispose); (failures ??= []).Add(error); }
            if (failures != null) throw new AggregateException("Native import item presenter release requires recovery.", failures);
        }
    }
    private static void ApplyNativeItemExpansion(INodePresenter? presenter)
    {
        if (presenter != null && presenter.AttachedProperties.TryGetValue(DisplayData.AutoExpandRuleKey, out var rule)
            && rule != ExpandRule.Auto) Gui.SetNextItemOpen(rule != ExpandRule.Never, ImGuiCond.Once);
    }
    private void DrawPresentedImportItemFields(AssetMetadataDocument owner, AssetImportSettingItem item,
        IReadOnlyList<AssetImportSettingField> fields)
    {
        var target = owner.AcquireImportItemCommandTarget(item);
        try
        {
            DrawPresentedAssetRows(owner, fields.Select(field => new PresentedAssetRow(field.Node, field.Category, field.Order,
                field.CategoryOrder?.Expand ?? ExpandRule.Always, () => DrawImportAssetSetting(owner, field), field.CategoryOrder?.Order)),
                target == null ? null : [target.Presenter]);
        }
        finally
        {
            if (target != null)
                try { target.Dispose(); } catch { packageCommandCleanup.Add(target.Dispose); throw; }
        }
    }
}
