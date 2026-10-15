using System.Numerics;
using Hexa.NET.ImGui;
using Stride.Core.Assets;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private Action? DrawBuildRootAssetActions(AssetEntry entry, EditorSessionHistory? history, AssetId[]? commandSelection = null)
    {
        if (workspace == null) return null;
        var owner = workspace;
        var ids = commandSelection ?? (assetBrowser.IsSelected(entry.Id) ? assetBrowser.SelectedEntries.Select(asset => asset.Id).ToArray() : [entry.Id]);
        var assets = ids.Select(workspace.FindAsset).ToArray();
        var targets = PackageRootAssets.GetTargets(workspace, assets);
        var target = PackageRootAssets.GetDefaultTarget(workspace, assets, targets);
        var included = target != null && assets.All(asset => target.RootAssets.ContainsKey(asset.Id));
        var header = target == null ? EditorLocalization.Text("Include in build as root asset", "Menu")
            : string.Format(EditorLocalization.Text(included ? "Don't include in build as root asset (in {0})" : "Include in build as root asset (in {0})", "Menu"), System.IO.Path.GetFileNameWithoutExtension(target.FullPath.ToOSPath()));
        var canEdit = history != null && projectLoading == null && assetPreviewBuild == null && !IsAuthoringPipelineUpdating && !IsUserProjectBuilding && !IsAssetImportUpdating
            && !closeRequested && !closePromptOpen && closingDocument == null;
        Action? action = null;
        var defaultEnabled = canEdit && target != null && assets.All(asset => !PackageRootAssets.IsForced(asset));
        if (DrawNativeMenuItem("      " + header + "###ToggleBuildRoot", "", false, defaultEnabled))
            action = () => { if (ReferenceEquals(workspace, owner)) new PackageRootAssets(owner, history!).Toggle(ids, target!); };
        DrawBuildRootMenuArtwork("asset-build-root", defaultEnabled);
        var submenuPosition = Gui.GetCursorScreenPos();
        var submenuDrawList = Gui.GetWindowDrawList();
        var submenuOpen = targets.Length > 0 && BeginNativeMenu("      " + EditorLocalization.Widget("Include in build as root asset in", "Menu"));
        if (targets.Length > 0) DrawBuildRootMenuArtwork("asset-build-root", true, submenuPosition, submenuDrawList);
        if (submenuOpen)
        {
            foreach (var package in targets)
            {
                Gui.PushID(package.FullPath.ToString());
                var bold = PushPackagePropertiesBoldFont(ReferenceEquals(package, target));
                var rooted = assets.Count(asset => package.RootAssets.ContainsKey(asset.Id));
                var partial = rooted > 0 && rooted < assets.Length;
                var enabled = canEdit && assets.Any(asset => !PackageRootAssets.IsForced(asset));
                if (DrawNativeMenuItem("      " + System.IO.Path.GetFileNameWithoutExtension(package.FullPath.ToOSPath()), "", rooted == assets.Length, enabled))
                    action = () => { if (ReferenceEquals(workspace, owner)) new PackageRootAssets(owner, history!).Toggle(ids, package); };
                if (partial) DrawBuildRootMenuArtwork("asset-build-root-partial", enabled);
                if (bold) Gui.PopFont();
                Gui.PopID();
            }
            Gui.EndMenu();
        }
        return action;
    }
    private void DrawBuildRootMenuArtwork(string name, bool enabled, Vector2? position = null, ImDrawListPtr? drawing = null)
    {
        if (icons == null) return;
        var size = 16 * Gui.GetStyle().FontScaleDpi;
        var minimum = position ?? Gui.GetItemRectMin();
        var height = position == null ? Gui.GetItemRectMax().Y - minimum.Y : Gui.GetTextLineHeight() + Gui.GetStyle().FramePadding.Y * 2;
        var start = minimum + new Vector2(Gui.GetStyle().FramePadding.X, Math.Max(0, (height - size) / 2));
        (drawing ?? Gui.GetWindowDrawList()).AddImage(icons.Get(name), start, start + new Vector2(size), Vector2.Zero, Vector2.One,
            Gui.GetColorU32(new Vector4(1, 1, 1, enabled ? 1 : Gui.GetStyle().DisabledAlpha)));
    }
}
