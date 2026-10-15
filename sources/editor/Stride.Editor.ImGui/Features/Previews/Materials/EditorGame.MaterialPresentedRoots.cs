namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private sealed record PresentedMaterialLayer(MaterialSettingLayer Layer, int Index, EditorPresenterTarget? Target);
    private IEnumerable<PresentedMaterialLayer> PresentedMaterialLayers(AssetMetadataDocument owner, IReadOnlyList<MaterialSettingLayer> layers)
    {
        var targets = new List<EditorPresenterTarget>();
        try
        {
            var rows = layers.Select((layer, index) =>
            {
                var target = owner.AcquireMaterialLayerCommandTarget(layer);
                if (target != null) targets.Add(target);
                return new PresentedMaterialLayer(layer, index, target);
            }).OrderBy(row => row.Target == null ? [] : NativeRowPath(row.Target.Presenter), NativeRowPathComparer.Instance);
            var categories = GetAssetPresenterRenderScope(owner).Categories;
            foreach (var row in rows)
            {
                if (!owner.IsMaterialLayerVisible(row.Layer)) continue;
                if (row.Target != null && (!PresenterPathVisible(row.Target.Presenter)
                    || !categories.Before(row.Target.Presenter, includeSelf: false, includeMemberCategories: false))) continue;
                yield return row;
            }
        }
        finally { ReleaseMaterialPresentedTargets(targets); }
    }
    private void DrawPresentedMaterialRootFields(AssetMetadataDocument owner)
    {
        var targets = new List<EditorPresenterTarget>();
        try
        {
            if (owner.AcquireMaterialRootPresenter() is { } root) targets.Add(root);
            if (owner.AcquireMaterialAttributesPresenter() is { } attributes) targets.Add(attributes);
            DrawPresentedAssetRows(owner, owner.MaterialSettingsFields.Select(field => new PresentedAssetRow(field.Node, field.Category,
                owner.MaterialSettingOrder(field), owner.MaterialSettingExpansion(field), () => DrawMaterialSetting(owner, field))),
                targets.Select(target => target.Presenter));
        }
        finally { ReleaseMaterialPresentedTargets(targets); }
    }
    private void DrawPresentedMaterialLayerFields(AssetMetadataDocument owner, MaterialSettingLayer layer, IReadOnlyList<MaterialSettingField> fields)
    {
        var targets = new List<EditorPresenterTarget>();
        try
        {
            if (owner.AcquireMaterialLayerCommandTarget(layer) is { } target) targets.Add(target);
            DrawPresentedAssetRows(owner, fields.Select(field => new PresentedAssetRow(field.Node, field.Category,
                owner.MaterialSettingOrder(field), owner.MaterialSettingExpansion(field), () => DrawMaterialSetting(owner, field))),
                targets.Select(target => target.Presenter));
        }
        finally { ReleaseMaterialPresentedTargets(targets); }
    }
    private void ReleaseMaterialPresentedTargets(IEnumerable<EditorPresenterTarget> targets)
    {
        List<Exception>? failures = null;
        foreach (var target in targets)
            try { target.Dispose(); }
            catch (Exception error) { packageCommandCleanup.Add(target.Dispose); (failures ??= []).Add(error); }
        if (failures != null) throw new AggregateException("Native material presenter release requires recovery.", failures);
    }
}
