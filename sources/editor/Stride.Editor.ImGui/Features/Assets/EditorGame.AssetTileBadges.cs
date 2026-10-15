using System.Numerics;
using Hexa.NET.ImGui;
using Stride.Core.Assets;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private readonly AssetBuildInclusionState assetBuildInclusion = new();
    private void UpdateAssetBuildInclusion()
    {
        if (workspace == null) { assetBuildInclusion.Clear(); return; }
        assetReferences.Update(workspace, documents, assetBrowser);
        assetBuildInclusion.Update(workspace, assetReferences);
    }
    private Action? DrawAssetTileBadges(AssetEntry asset, Vector2 origin, float thumbnailSize)
    {
        if (workspace == null || icons == null) return null;
        var sourceAction = DrawAssetSourceUpdateBadge(asset, origin, thumbnailSize);
        if (assetBuildInclusion.Get(asset.Id) is not { } status) return sourceAction;
        var dpi = Gui.GetStyle().FontScaleDpi;
        var savedCursor = Gui.GetCursorScreenPos();
        var owner = workspace;
        var item = owner.FindAsset(asset.Id);
        var targets = PackageRootAssets.GetTargets(owner, [item]);
        var target = PackageRootAssets.GetDefaultTarget(owner, [item], targets);
        var enabled = target != null && !PackageRootAssets.IsForced(item) && sessionHistory != null
            && projectLoading == null && assetPreviewBuild == null && !IsAuthoringPipelineUpdating && !IsUserProjectBuilding && !IsAssetImportUpdating
            && !closeRequested && !closePromptOpen && closingDocument == null;
        var dotOrigin = origin + new Vector2(0, 3 * dpi);
        Gui.SetCursorScreenPos(dotOrigin);
        Gui.BeginDisabled(!enabled);
        var clicked = Gui.InvisibleButton("##AssetRootInclusion", new Vector2(22 * dpi));
        Gui.EndDisabled();
        Gui.GetWindowDrawList().AddImage(icons.Get(status.Icon), dotOrigin + new Vector2(5 * dpi), dotOrigin + new Vector2(17 * dpi));
        if (Gui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) Gui.SetTooltip(status.Description);
        if (status.ReplacesUrl != null) DrawAssetReplacementBadge(origin + new Vector2(thumbnailSize - 26 * dpi, thumbnailSize - 26 * dpi),
            string.Format(EditorLocalization.Text("Replaces {0}"), status.ReplacesUrl), dpi);
        if (status.ReplacedByUrl != null) DrawAssetReplacementBadge(origin + new Vector2(0, thumbnailSize - 26 * dpi),
            string.Format(EditorLocalization.Text("Replaced by {0}"), status.ReplacedByUrl), dpi);
        Gui.SetCursorScreenPos(savedCursor);
        return sourceAction ?? (clicked ? () => { if (ReferenceEquals(workspace, owner)) new PackageRootAssets(owner, sessionHistory!).Toggle([asset.Id], target!); } : null);
    }
    private bool IsAssetTileBadgeHovered(AssetEntry asset, Vector2 origin, float thumbnailSize)
    {
        if (icons == null || workspace == null) return false;
        var dpi = Gui.GetStyle().FontScaleDpi;
        if (modifiedSourceStatus?.Modified.Contains(asset.Id) == true
            && Gui.IsMouseHoveringRect(origin + new Vector2(thumbnailSize - 26 * dpi, 3 * dpi), origin + new Vector2(thumbnailSize, 29 * dpi))) return true;
        if (assetBuildInclusion.Get(asset.Id) is not { } status) return false;
        if (Gui.IsMouseHoveringRect(origin + new Vector2(0, 3 * dpi), origin + new Vector2(22 * dpi, 25 * dpi))) return true;
        if (status.ReplacesUrl != null && Gui.IsMouseHoveringRect(origin + new Vector2(thumbnailSize - 26 * dpi), origin + new Vector2(thumbnailSize))) return true;
        return status.ReplacedByUrl != null && Gui.IsMouseHoveringRect(origin + new Vector2(0, thumbnailSize - 26 * dpi), origin + new Vector2(26 * dpi, thumbnailSize));
    }
    private Action? DrawAssetSourceUpdateBadge(AssetEntry asset, Vector2 origin, float thumbnailSize)
    {
        if (modifiedSourceStatus?.Modified.Contains(asset.Id) != true || icons == null || workspace == null) return null;
        var owner = workspace; var dpi = Gui.GetStyle().FontScaleDpi;
        var position = origin + new Vector2(thumbnailSize - 26 * dpi, 3 * dpi);
        var saved = Gui.GetCursorScreenPos(); Gui.SetCursorScreenPos(position); Gui.PushID("source-update/" + asset.Id);
        var enabled = CanReimportAsset(asset); Gui.BeginDisabled(!enabled);
        var clicked = Gui.InvisibleButton("##UpdateSource", new Vector2(26 * dpi));
        var draw = Gui.GetWindowDrawList(); var end = position + new Vector2(26 * dpi);
        draw.AddRectFilled(position, end, Gui.GetColorU32(Gui.IsItemHovered() ? ImGuiCol.ButtonHovered : ImGuiCol.FrameBg), 3 * dpi);
        draw.AddRect(position, end, Gui.GetColorU32(ImGuiCol.Border), 3 * dpi, ImDrawFlags.None, 2 * dpi);
        draw.AddImage(icons.Get("asset-update-source"), position + new Vector2(7 * dpi), position + new Vector2(19 * dpi));
        Gui.EndDisabled(); Gui.PopID(); Gui.SetCursorScreenPos(saved);
        return !clicked ? null : () =>
        {
            if (ReferenceEquals(workspace, owner) && owner.AssetItems.Any(item => item.Id == asset.Id) && CanReimportAsset(asset)) RequestAssetReimport(asset);
        };
    }
    private void DrawAssetReplacementBadge(Vector2 origin, string tooltip, float dpi)
    {
        var list = Gui.GetWindowDrawList(); var end = origin + new Vector2(26 * dpi);
        list.AddRectFilled(origin, end, Gui.GetColorU32(ImGuiCol.FrameBg), 3 * dpi);
        list.AddRect(origin, end, Gui.GetColorU32(ImGuiCol.Border), 3 * dpi, ImDrawFlags.None, 2 * dpi);
        list.AddImage(icons!.Get("asset-replacement"), origin + new Vector2(7 * dpi), origin + new Vector2(19 * dpi));
        if (Gui.IsMouseHoveringRect(origin, end)) Gui.SetTooltip(tooltip);
    }
}
