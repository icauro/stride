using System.Numerics;
using Stride.Core.Assets;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawAssetTooltip(AssetEntry entry)
    {
        if (workspace == null || !workspace.AssetItems.Any(item => item.Id == entry.Id)) return;
        var item = workspace.FindAsset(entry.Id);
        var asset = authoring?.Find(entry.Id)?.Document is IAssetTagDocument retained ? retained.AuthoredAsset : item.Asset;
        var type = Stride.Core.DisplayAttribute.GetDisplayName(asset.GetType()) ?? asset.GetType().Name;
        Gui.BeginTooltip();
        DrawNativeLabel(string.Format(EditorLocalization.Text("URL: {0}"), item.Location.ToString()));
        DrawNativeLabel(string.Format(EditorLocalization.Text("Type: {0}"), type));
        if (asset.Tags.Count != 0) DrawNativeLabel(string.Format(EditorLocalization.Text("Tags: {0}"), string.Join(" ", asset.Tags)));
        if (assetBuildInclusion.Get(entry.Id) is { } status)
        {
            if (status.ReplacesUrl != null) DrawAssetTooltipStatus("asset-replacement", string.Format(EditorLocalization.Text("Replaces {0}"), status.ReplacesUrl));
            if (status.ReplacedByUrl != null) DrawAssetTooltipStatus("asset-replacement", string.Format(EditorLocalization.Text("Replaced by {0}"), status.ReplacedByUrl));
            DrawAssetTooltipStatus(status.Icon, status.Description);
        }
        Gui.EndTooltip();
    }
    private void DrawAssetTooltipStatus(string icon, string text)
    {
        if (icons != null) { Gui.Image(icons.Get(icon), new Vector2(UiScale(16))); Gui.SameLine(); }
        DrawNativeLabel(text);
    }
    private void DrawAssetFolderTooltip(AssetBrowserFolder folder)
    {
        if (workspace == null || !workspace.Session.Packages.Contains(folder.Package)) return;
        Gui.BeginTooltip();
        if (folder.Parent?.Parent == null)
        {
            DrawNativeLabel(string.Format(EditorLocalization.Text("Name: {0}"), folder.Name));
            DrawNativeLabel(string.Format(EditorLocalization.Text("Type: {0}"), folder.Parent == null ? "Package" : "Mount Point"));
        }
        else
        {
            DrawNativeLabel(string.Format(EditorLocalization.Text("URL: {0}"), folder.RelativePath.TrimEnd('/') + "/"));
            DrawNativeLabel(string.Format(EditorLocalization.Text("Type: {0}"), "Folder"));
        }
        Gui.EndTooltip();
    }
}
