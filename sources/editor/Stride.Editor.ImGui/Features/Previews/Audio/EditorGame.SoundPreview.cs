using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private SoundAssetPreview? soundPreview;
    private Exception? reportedSoundPreviewError;
    private void OpenSoundPreview(AssetEntry entry)
    {
        CloseCatalogAssetPreviews(); CloseLegacyAssetPreviews();
        CloseAnimatedMediaPreviews(); requestedMediaPreview = entry; mediaPreviewWorkspace = workspace;
        selectedAssetPath = assetPreviewPath = entry.Path; focusAssetPreview = true;
        var compiled = projectContent;
        if (workspace == null || compiled == null) return;
        var snapshot = CaptureMediaPreview(entry);
        if (Content.FileProvider.ObjectDatabase.ContentIndexMap.Contains(snapshot.ClipUrl))
        {
            soundPreview = CreateAssetPreview(entry, () => new SoundAssetPreview(this, compiled, snapshot.ClipUrl));
            if (soundPreview != null) installedMediaRevision = snapshot.Revision;
        }
    }
    private void CloseSoundPreview()
    {
        if (soundPreview != null) { soundPreview.Stop(); soundPreview.Dispose(); soundPreview = null; }
        reportedSoundPreviewError = null;
    }
    private void DrawSoundPreview()
    {
        if (focusAssetPreview) { Gui.SetWindowFocus(PreviewWindowCaption.Text); focusAssetPreview = false; }
        using var previewWindow = ImGuiScopes.Window(PreviewWindowCaption.Text, ref showAssetPreview);
        if (!previewWindow.DrawContents) return;
        var preview = soundPreview;
        if (preview == null)
        {
            if (IsMediaPreviewUpdating) DrawSoundPreviewBuilding();
            return;
        }
        if (preview.PlaybackError is { } error && !ReferenceEquals(reportedSoundPreviewError, error))
        { reportedSoundPreviewError = error; RecordAssetPreviewRenderFailure("Sound preview", error); statusMessage = error.Message; }
        if (!preview.IsValid) { DrawSoundPreviewBuilding(); return; }
        var toolbarHeight = Math.Max(UiScale(16) + Gui.GetStyle().FramePadding.Y * 2, UiScale(18));
        using (ImGuiScopes.Style(ImGuiStyleVar.ItemSpacing, System.Numerics.Vector2.Zero))
        using (ImGuiScopes.Child("##SoundPreviewToolbar", new System.Numerics.Vector2(0, toolbarHeight), ImGuiChildFlags.None,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
        var toolbarY = Gui.GetCursorScreenPos().Y;
        using (ImGuiScopes.Disabled(!preview.IsValid || preview.IsPlaying))
        {
        if (DrawPreviewToolbarIcon("preview-play", toolbarY, toolbarHeight, 0, EditorLocalization.Text("Play the sound", "ToolTip"))) preview.Play();
        if (Controls.Enabled) Controls.Record("sound-preview/play");
        }
        using (ImGuiScopes.Disabled(!preview.IsValid || !preview.IsPlaying))
        {
        if (DrawPreviewToolbarIcon("preview-pause", toolbarY, toolbarHeight, 0, EditorLocalization.Text("Pause the sound", "ToolTip"))) preview.Pause();
        if (Controls.Enabled) Controls.Record("sound-preview/pause");
        }
        using (ImGuiScopes.Disabled(!preview.IsValid))
        {
        Gui.SetCursorScreenPos(new System.Numerics.Vector2(Gui.GetCursorScreenPos().X + UiScale(5),
            toolbarY + Math.Max(0, (toolbarHeight - UiScale(18)) / 2) + UiScale(5)));
        Gui.SetNextItemWidth(UiScale(60)); var volume = preview.Volume;
        if (PreviewSliderControl.DrawFloat(preview, "##SoundVolume", ref volume, 0, 1, heightDips: 8)) preview.Volume = volume;
        if (Controls.Enabled) Controls.Record("sound-preview/volume");
        }
        }
        var available = Gui.GetContentRegionAvail();
        var groupHeight = UiScale(30) + Gui.GetFontSize();
        var groupOrigin = Gui.GetCursorScreenPos() + new System.Numerics.Vector2(0, Math.Max(0, (available.Y - groupHeight) / 2));
        Gui.SetCursorScreenPos(groupOrigin + new System.Numerics.Vector2(UiScale(15), UiScale(5)));
        var position = (float)preview.Position.TotalSeconds;
        Gui.SetNextItemWidth(Math.Max(1, available.X - UiScale(30)));
        if (PreviewSliderControl.DrawFloat(preview, "##SoundTime", ref position, 0, (float)preview.Duration.TotalSeconds,
            heightDips: 20, thumbWidthDips: 12)) preview.Seek(position);
        if (Controls.Enabled) Controls.Record("sound-preview/time");
        var time = $"{preview.Position:mm\\:ss\\:fff}/{preview.Duration:mm\\:ss\\:fff}";
        Gui.SetCursorScreenPos(groupOrigin + new System.Numerics.Vector2((available.X - Gui.CalcTextSize(time).X) / 2, UiScale(30)));
        Gui.TextUnformatted(time);
    }
    private static void DrawSoundPreviewBuilding()
    {
        var text = EditorLocalization.Text("Building preview sound asset...");
        var available = Gui.GetContentRegionAvail(); var measured = Gui.CalcTextSize(text);
        Gui.SetCursorPos(Gui.GetCursorPos() + new System.Numerics.Vector2(Math.Max(0, (available.X - measured.X) / 2),
            Math.Max(0, (available.Y - measured.Y) / 2)));
        Gui.TextUnformatted(text);
    }
}
