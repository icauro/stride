using System.Diagnostics;
using Hexa.NET.ImGui;
using Stride.Core.Assets;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private sealed record AssetExploreMessage(string Text, bool Error, Action? Acknowledged = null);
    private readonly Queue<AssetExploreMessage> assetExploreMessages = new();
    private AssetExploreMessage? assetExploreMessage;
    private bool closeAssetExploreMessages;
    private bool IsAssetExploreMessagePending => assetExploreMessage != null || assetExploreMessages.Count != 0;

    private void RequestAssetExploreMessage(string message, bool error = false)
        => assetExploreMessages.Enqueue(new(EditorLocalization.Text(message, "Message"), error));

    private void RequestAssetActionMessage(string localizedMessage, bool error = false, Action? acknowledged = null)
        => assetExploreMessages.Enqueue(new(localizedMessage, error, acknowledged));

    private void StartAssetExplorer(string path, bool fileSelection, bool assetFile = false)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            if (assetFile && !File.Exists(fullPath))
            { RequestAssetExploreMessage("You need to save the asset before you can explore it."); return; }
            var start = new ProcessStartInfo { FileName = fileSelection ? "explorer.exe" : fullPath, UseShellExecute = true };
            if (fileSelection) { start.ArgumentList.Add("/select,"); start.ArgumentList.Add(fullPath); }
            using var process = Process.Start(start);
        }
        catch (Exception)
        { RequestAssetExploreMessage("There was a problem starting the file explorer.", true); }
    }

    private void ExploreAssetSource(AssetItem item)
    {
        // Target invalidation is a command-ownership failure, not an Explorer launch failure.
        if (workspace == null || !ReferenceEquals(workspace.Session.FindAsset(item.Id), item) || item.IsDeleted)
            throw new InvalidOperationException("The selected asset is no longer available.");
        try
        {
            var source = AssetMainSource(item);
            if (source == null) { RequestAssetExploreMessage("This asset doesn't have a source file."); return; }
            var fileSelection = File.Exists(source);
            if (!fileSelection)
            {
                source = Path.GetDirectoryName(source);
                if (source == null || !Directory.Exists(source))
                { RequestAssetExploreMessage("Source file and path no longer exists."); return; }
            }
            StartAssetExplorer(source, fileSelection);
        }
        catch (Exception)
        { RequestAssetExploreMessage("There was a problem starting the file explorer.", true); }
    }

    private void DrawAssetExploreMessage()
    {
        if (closeAssetExploreMessages)
        {
            closeAssetExploreMessages = false;
            foreach (var id in new[] { "AssetExploreError", "AssetExploreInformation" })
            {
                using var retired = ImGuiScopes.Modal(WpfMessageDialogTitle(id), ImGuiWindowFlags.AlwaysAutoResize);
                if (retired.DrawContents) Gui.CloseCurrentPopup();
            }
        }
        if (assetExploreMessage == null && assetExploreMessages.TryDequeue(out var message))
        {
            assetExploreMessage = message;
            Gui.OpenPopup(WpfMessageDialogTitle(message.Error ? "AssetExploreError" : "AssetExploreInformation"));
        }
        var current = assetExploreMessage;
        if (current == null) return;
        var title = WpfMessageDialogTitle(current.Error ? "AssetExploreError" : "AssetExploreInformation");
        using var dialog = ImGuiScopes.Modal(title, ImGuiWindowFlags.AlwaysAutoResize);
        if (!dialog.DrawContents) return;
        DrawMessageDialogBody("##AssetExploreMessage", current.Text, current.Error ? EditorMessageDialogImage.Error : EditorMessageDialogImage.Information);
        var ownsKeys = !Gui.GetIO().AppFocusLost && Gui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows)
            && !Gui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId);
        if (DrawNativeButton(EditorLocalization.Widget("OK", "Button"))
            || ownsKeys && (Gui.IsKeyPressed(ImGuiKey.Enter, false) || Gui.IsKeyPressed(ImGuiKey.Escape, false)))
        {
            assetExploreMessage = null; Gui.CloseCurrentPopup();
            current.Acknowledged?.Invoke();
        }
    }

    private void ClearAssetExploreMessages()
    { assetExploreMessages.Clear(); assetExploreMessage = null; closeAssetExploreMessages = true; }
}
