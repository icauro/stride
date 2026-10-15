using Hexa.NET.ImGui;
using Stride.Assets.Materials;
using Stride.Core.Assets;
using Stride.Core.Serialization;
using Stride.Rendering;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private sealed record MaterialChoice(AssetId Id, string Url, string Label);
    private MaterialChoice[] materialChoices = [];
    private MaterialChoice? materialSelection;
    private string materialSearch = "";

    private void DrawMaterialReference(ProceduralAssetDocument asset, AssetPropertyField field)
    {
        var current = AttachedReferenceManager.GetAttachedReference(field.Node.Retrieve());
        Gui.TextWrapped(current?.Url ?? "None");
        if (Gui.Button("Choose material..."))
        {
            asset.EndEdit(); materialSearch = "";
            materialChoices = workspace!.Session.Packages.SelectMany(package => package.Assets
                    .Where(item => item.Asset is MaterialAsset)
                    .Select(item => new MaterialChoice(item.Id, item.Location.ToString(),
                        (package.Meta.Name ?? "Package") + " / " + item.UnqualifiedUrl)))
                .OrderBy(choice => choice.Label, StringComparer.OrdinalIgnoreCase).ToArray();
            materialSelection = materialChoices.FirstOrDefault(choice => choice.Id == current?.Id)
                ?? (current == null ? null : new MaterialChoice(current.Id, current.Url, "Unavailable / " + current.Url));
            Gui.OpenPopup("Select an asset###MaterialAssetPicker");
        }
        if (Gui.IsItemVisible()) if (Controls.Enabled) Controls.Record("asset-editor/material/choose");
        Gui.SetNextWindowSize(new System.Numerics.Vector2(540, 360), ImGuiCond.Appearing);
        if (!Gui.BeginPopupModal("Select an asset###MaterialAssetPicker", ImGuiWindowFlags.NoResize)) return;
        Gui.TextDisabled("Project and dependency materials");
        Gui.SetNextItemWidth(-1); Gui.InputTextWithHint("##Search", "Search name or package...", ref materialSearch, 512);
        if (Controls.Enabled) Controls.Record("material-picker/search");
        if (Gui.BeginChild("Materials", new System.Numerics.Vector2(0, -45)))
        {
            if (Gui.Selectable(EditorLocalization.Widget("None", "Button"), materialSelection == null)) materialSelection = null;
            if (Controls.Enabled) Controls.Record("material-picker/none");
            if (materialSelection != null && !materialChoices.Contains(materialSelection))
                Gui.TextDisabled(materialSelection.Label);
            foreach (var choice in materialChoices)
            {
                if (!choice.Label.Contains(materialSearch, StringComparison.OrdinalIgnoreCase)) continue;
                Gui.PushID(choice.Id.ToString());
                if (Gui.Selectable(choice.Label, choice == materialSelection)) materialSelection = choice;
                if (Controls.Enabled) Controls.Record("material-picker/asset/" + choice.Url); Gui.PopID();
            }
            if (materialChoices.All(choice => !choice.Label.Contains(materialSearch, StringComparison.OrdinalIgnoreCase)))
                Gui.TextDisabled("No matching materials");
        }
        ImGuiScopes.EndChild();
        if (Gui.Button("Use"))
        {
            Execute(() =>
            {
                if (materialSelection?.Id != current?.Id || materialSelection?.Url != current?.Url)
                {
                    var material = materialSelection == null ? null
                        : AttachedReferenceManager.CreateProxyObject<Material>(materialSelection.Id, materialSelection.Url);
                    asset.Update(field, material); asset.EndEdit();
                }
                Gui.CloseCurrentPopup();
            });
        }
        if (Controls.Enabled) Controls.Record("material-picker/use"); Gui.SameLine();
        if (Gui.Button(EditorLocalization.Widget("Cancel", "Button")) || Gui.IsKeyPressed(ImGuiKey.Escape)) Gui.CloseCurrentPopup();
        if (Controls.Enabled) Controls.Record("material-picker/cancel"); Gui.EndPopup();
    }
}
