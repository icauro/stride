using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class AssetBrowserPanel
{
    private WeakReference<ProjectWorkspace>? backgroundWorkspace;
    private long backgroundSelectionVersion;
    private bool IsBackgroundContextCurrent()
    {
        if (backgroundWorkspace == null || !backgroundWorkspace.TryGetTarget(out var owner)) return false;
        return ReferenceEquals(host.Workspace, owner) && assetBrowser.SelectionVersion == backgroundSelectionVersion;
    }
    private void DrawAssetBackgroundContext(ref Action? assetAction)
    {
        var disabled = (ImGuiP.GetItemFlags() & (ImGuiItemFlags)ImGuiItemFlagsPrivate.Disabled) != 0;
        if (!Gui.BeginPopupContextWindow("Asset view background actions", ImGuiPopupFlags.MouseButtonRight | ImGuiPopupFlags.NoOpenOverItems)) return;
        try
        {
            if (Gui.IsWindowAppearing() && host.Workspace is { } workspace)
            { backgroundWorkspace = new(workspace); backgroundSelectionVersion = assetBrowser.SelectionVersion; }
            if (!IsBackgroundContextCurrent() || Gui.GetIO().AppFocusLost || disabled)
            { Gui.CloseCurrentPopup(); return; }
            if (host.DrawBackgroundActions() is { } action)
                assetAction = () => { if (IsBackgroundContextCurrent()) action(); };
        }
        finally { Gui.EndPopup(); }
    }
}
