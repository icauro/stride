using Hexa.NET.ImGui;
using Stride.Core.Assets;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private sealed record DeletionReplacementItem(AssetItem Asset, string Label);
    private int deletionReferenceIndex;
    private bool deletionReplaceAll = true;
    private AssetItem? deletionSingleReplacement;
    private AssetDeletionHistory.ReferenceRow[] deletionReferenceRows = [];
    private readonly Dictionary<string, DeletionReplacementItem[]> deletionReplacementChoices = [];
    private string deletionReplacementSearch = "", deletionReplacementCachedSearch = "", deletionReplacementPopup = "";
    private DeletionReplacementItem[] deletionReplacementFiltered = [];
    private void ResetDeletionReferenceChoices()
    {
        deletionReferenceIndex = 0; deletionReplaceAll = true; deletionSingleReplacement = null;
        deletionReferenceRows = []; deletionReplacementChoices.Clear(); deletionReplacementFiltered = [];
        deletionReplacementSearch = deletionReplacementCachedSearch = deletionReplacementPopup = "";
    }
    private void DrawAssetDeletionReferences(AssetDeletionHistory.Plan plan)
    {
        if (deletionReferenceIndex >= plan.ReferencedTargets.Count) return;
        var target = plan.ReferencedTargets[deletionReferenceIndex];
        if (deletionReferenceRows.Length == 0)
            deletionReferenceRows = plan.ReferenceRows.Where(row => row.Choice.DeletedAssetId == target.Id).ToArray();
        DrawWrappedText(string.Format(EditorLocalization.Text("The asset {0} is referenced by other assets. Choose how to handle these references."), target.Name));
        if (Gui.RadioButton(EditorLocalization.Widget("Replace all the references with a reference to a different asset:", "Button"), deletionReplaceAll))
        {
            deletionReplaceAll = true;
            if (deletionSingleReplacement != null) TrySelectDeletionReplacement(deletionReferenceRows, deletionSingleReplacement);
        }
        Gui.BeginDisabled(!deletionReplaceAll);
        DrawDeletionReplacementPicker("all:" + target.UiId, deletionSingleReplacement, deletionReferenceRows, asset =>
        { if (TrySelectDeletionReplacement(deletionReferenceRows, asset)) deletionSingleReplacement = asset; });
        Gui.EndDisabled();
        if (Gui.RadioButton(EditorLocalization.Widget("Replace references individually:", "Button"), !deletionReplaceAll)) deletionReplaceAll = false;
        Gui.BeginDisabled(deletionReplaceAll);
        if (Gui.BeginTable("AssetDeleteReferencers", 2, ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.ScrollY,
            new System.Numerics.Vector2(UiScale(650), Math.Min(UiScale(360), Math.Max(Gui.GetFrameHeight() * 3, Gui.GetWindowViewport().WorkSize.Y * 0.4f)))))
        {
            Gui.TableSetupColumn(EditorLocalization.Widget("Referencer")); Gui.TableSetupColumn(EditorLocalization.Widget("Replacement asset")); Gui.TableHeadersRow();
            var clipper = Gui.ImGuiListClipper();
            try
            {
                Gui.Begin(clipper, deletionReferenceRows.Length);
                while (Gui.Step(clipper))
                for (var index = clipper.DisplayStart; index < clipper.DisplayEnd; index++)
                {
                    var row = deletionReferenceRows[index];
                    Gui.PushID(row.Choice.Id.ToString()); Gui.TableNextRow(); Gui.TableNextColumn(); Gui.TextUnformatted(row.Owner.Name);
                    Gui.TableNextColumn(); var clear = row.Choice.IsClear;
                    if (Gui.Checkbox(EditorLocalization.Widget("Clear this reference", "Button"), ref clear))
                    {
                        try { if (clear) row.Choice.Clear(); else row.Choice.UseReplacement(); assetDeletionError = null; }
                        catch (Exception error) { assetDeletionError = error.Message; }
                    }
                    Gui.BeginDisabled(clear);
                    DrawDeletionReplacementPicker("row:" + row.Choice.Id, row.Choice.Replacement, [row], asset => TrySelectDeletionReplacement([row], asset));
                    Gui.EndDisabled(); Gui.PopID();
                }
            }
            finally { Gui.Destroy(clipper); }
            Gui.EndTable();
        }
        Gui.EndDisabled();
        if (assetDeletionError != null) DrawWrappedText(assetDeletionError);
        var canDone = deletionReferenceRows.Length > 0 && deletionReferenceRows.All(row => row.Choice.IsResolved)
            && (!deletionReplaceAll || deletionSingleReplacement != null);
        Gui.BeginDisabled(!canDone);
        if (Gui.Button(EditorLocalization.Widget("Done", "Button"))) AdvanceDeletionReferenceTarget(plan);
        Gui.EndDisabled(); Gui.SameLine();
        if (Gui.Button(EditorLocalization.Widget("Clear references", "Button")))
        {
            try { foreach (var row in deletionReferenceRows) row.Choice.Clear(); AdvanceDeletionReferenceTarget(plan); }
            catch (Exception error) { assetDeletionError = error.Message; }
        }
        if (Gui.IsItemHovered()) Gui.SetTooltip(EditorLocalization.Text("Clear the references of the current asset", "ToolTip"));
        Gui.SameLine();
        if (Gui.Button(EditorLocalization.Widget("Clear all references", "Button")))
        {
            try
            {
                var remaining = plan.ReferencedTargets.Skip(deletionReferenceIndex).Select(asset => asset.Id).ToHashSet();
                foreach (var row in plan.ReferenceRows.Where(row => remaining.Contains(row.Choice.DeletedAssetId))) row.Choice.Clear();
                TryCommitAssetDeletion(false);
            }
            catch (Exception error) { assetDeletionError = error.Message; }
        }
        if (Gui.IsItemHovered()) Gui.SetTooltip(EditorLocalization.Text("Clear references for all assets being deleted", "ToolTip"));
        Gui.SameLine(); if (Gui.Button(EditorLocalization.Widget("Cancel", "Button"))) { CancelAssetDeletion(); Gui.CloseCurrentPopup(); }
        if (Gui.IsItemHovered()) Gui.SetTooltip(EditorLocalization.Text("Cancel operation", "ToolTip"));
    }
    private bool TrySelectDeletionReplacement(IReadOnlyList<AssetDeletionHistory.ReferenceRow> rows, AssetItem asset)
    {
        try
        {
            if (pendingAssetDeletion!.Targets.Any(target => target.Id == asset.Id) || rows.Any(row => !row.Choice.Accepts(asset)))
                throw new ArgumentException("The asset is not compatible with this property.");
            // Validate every proxy construction before changing any of the UI drafts.
            foreach (var row in rows) ContentAssetReference.Create(row.Choice.StorageType, asset);
            foreach (var row in rows) row.Choice.SetReplacement(asset);
            assetDeletionError = null; return true;
        }
        catch (Exception error) { assetDeletionError = error.Message; return false; }
    }
    private void AdvanceDeletionReferenceTarget(AssetDeletionHistory.Plan plan)
    {
        deletionReferenceIndex++; deletionReferenceRows = []; deletionSingleReplacement = null; deletionReplaceAll = true;
        if (deletionReferenceIndex >= plan.ReferencedTargets.Count) TryCommitAssetDeletion(false);
    }
    private void DrawDeletionReplacementPicker(string identity, AssetItem? selected,
        IReadOnlyList<AssetDeletionHistory.ReferenceRow> rows, Action<AssetItem> choose)
    {
        Gui.PushID(identity); Gui.SetNextItemWidth(UiScale(330));
        if (Gui.BeginCombo("##Replacement", selected?.Location.ToString() ?? EditorLocalization.Text("Select an asset")))
        {
            if (!deletionReplacementChoices.TryGetValue(identity, out var choices))
            {
                var excluded = pendingAssetDeletion!.Targets.Select(target => target.Id).ToHashSet();
                choices = workspace!.AssetItems.Where(asset => !excluded.Contains(asset.Id) && rows.All(row => row.Choice.Accepts(asset)))
                    .Select(asset => new DeletionReplacementItem(asset, asset.Location.ToString()))
                    .OrderBy(item => item.Label, StringComparer.OrdinalIgnoreCase).ToArray();
                deletionReplacementChoices.Add(identity, choices);
            }
            if (Gui.IsWindowAppearing() || deletionReplacementPopup != identity)
            {
                deletionReplacementSearch = deletionReplacementCachedSearch = ""; deletionReplacementPopup = identity;
                deletionReplacementFiltered = choices;
            }
            Gui.SetNextItemWidth(-1); Gui.InputTextWithHint("##Search", "Search", ref deletionReplacementSearch, 512);
            if (deletionReplacementSearch != deletionReplacementCachedSearch)
            {
                deletionReplacementCachedSearch = deletionReplacementSearch;
                deletionReplacementFiltered = choices.Where(item => item.Label.Contains(deletionReplacementSearch, StringComparison.OrdinalIgnoreCase)).ToArray();
            }
            var clipper = Gui.ImGuiListClipper();
            try
            {
                Gui.Begin(clipper, deletionReplacementFiltered.Length);
                while (Gui.Step(clipper))
                    for (var index = clipper.DisplayStart; index < clipper.DisplayEnd; index++)
                    {
                        var item = deletionReplacementFiltered[index]; Gui.PushID(item.Asset.Id.ToString());
                        var origin = Gui.GetCursorScreenPos(); var clicked = Gui.Selectable("##Asset", selected?.Id == item.Asset.Id);
                        Gui.GetWindowDrawList().AddText(origin, Gui.GetColorU32(ImGuiCol.Text), item.Label);
                        if (clicked) choose(item.Asset); Gui.PopID();
                    }
            }
            finally { Gui.Destroy(clipper); }
            Gui.EndCombo();
        }
        Gui.PopID();
    }
}
