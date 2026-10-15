using Hexa.NET.ImGui;
using Stride.Assets.Models;
using Stride.Core.Mathematics;
using Stride.Games;
using Gui = Hexa.NET.ImGui.ImGui;
using NVector2 = System.Numerics.Vector2;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private AnimationAssetPreview? animationPreview;
    private Exception? reportedAnimationPreviewError;
    private AnimationAssetPreview? animationScrubOwner;
    private int animationScrubFrame = -1;
    private readonly ref struct AnimationPreviewScrubFrameScope(EditorGame editor)
    {
        public void Dispose()
        {
            if (editor.animationScrubFrame != Gui.GetFrameCount()) editor.EndAnimationPreviewScrub();
        }
    }
    private void EndAnimationPreviewScrub()
    {
        if (animationScrubOwner == null) return;
        animationScrubOwner.EndScrub();
        animationScrubOwner = null;
    }
    private void PumpAnimationPreviewScrub()
    {
        if (animationScrubOwner != null && (!IsActiveForTick || !showAssetPreview || IsEditorGraphicsRecovering
            || IsEditorGpuRetirementPending || !ReferenceEquals(animationScrubOwner, animationPreview)))
            EndAnimationPreviewScrub();
    }
    private void TrackAnimationPreviewScrub(AnimationAssetPreview preview)
    {
        if (animationScrubOwner != null && !ReferenceEquals(animationScrubOwner, preview)) EndAnimationPreviewScrub();
        if (Gui.IsItemActivated() && Gui.IsMouseDown(ImGuiMouseButton.Left))
        { preview.BeginScrub(); animationScrubOwner = preview; }
        if (!ReferenceEquals(animationScrubOwner, preview)) return;
        if (Gui.IsItemActive() && Gui.IsMouseDown(ImGuiMouseButton.Left)) animationScrubFrame = Gui.GetFrameCount();
        else EndAnimationPreviewScrub();
    }
    private void OpenAnimationPreview(AssetEntry entry)
    {
        CloseCatalogAssetPreviews(); CloseLegacyAssetPreviews();
        CloseAnimatedMediaPreviews(); requestedMediaPreview = entry; mediaPreviewWorkspace = workspace;
        selectedAssetPath = assetPreviewPath = entry.Path; focusAssetPreview = true;
        var compiled = projectContent; var graphics = renderer;
        if (workspace == null || compiled == null || graphics == null) return;
        var snapshot = CaptureMediaPreview(entry);
        var map = Content.FileProvider.ObjectDatabase.ContentIndexMap;
        if (snapshot.ModelUrl != null && map.Contains(snapshot.ModelUrl) && map.Contains(snapshot.ClipUrl))
        {
            animationPreview = CreateAssetPreview(entry, () => new AnimationAssetPreview(this, graphics, compiled,
                snapshot.ModelUrl, snapshot.ClipUrl, snapshot.Hdr, snapshot.Parameters.ColorSpace));
            if (animationPreview != null) installedMediaRevision = snapshot.Revision;
        }
    }
    private void CloseAnimationPreview()
    {
        EndAnimationPreviewScrub();
        if (animationPreview != null)
        { animationPreview.Visible = false; animationPreview.EndScrub(); animationPreview.Pause(); RetireEditorPreview(animationPreview); animationPreview = null; }
        reportedAnimationPreviewError = null;
    }
    private void HideAnimatedMediaPreviews() { if (animationPreview != null) animationPreview.Visible = false; }
    private void CloseAnimatedMediaPreviews()
    {
        mediaPreviewCancellation?.Cancel(); requestedMediaPreview = null;
        installedMediaRevision = attemptedMediaRevision = pendingMediaRevision = null;
        CloseAnimationPreview(); CloseSoundPreview();
    }
    private void RenderAnimationPreview(GameTime time) => animationPreview?.Render(time);
    private void DrawAnimationPreview()
    {
        if (focusAssetPreview) { Gui.SetWindowFocus(PreviewWindowCaption.Text); focusAssetPreview = false; }
        using var previewWindow = ImGuiScopes.Window(PreviewWindowCaption.Text, ref showAssetPreview);
        if (!previewWindow.DrawContents)
        { animationPreview?.EndScrub(); return; }
        var preview = animationPreview;
        var toolbarHeight = Math.Max(Gui.GetFrameHeight(), UiScale(16) + Gui.GetStyle().FramePadding.Y * 2);
        using (ImGuiScopes.Style(ImGuiStyleVar.ItemSpacing, NVector2.Zero))
        using (ImGuiScopes.Child("##AnimationToolbar", new NVector2(0, toolbarHeight + UiScale(8)), ImGuiChildFlags.None,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            Gui.SetCursorScreenPos(Gui.GetCursorScreenPos() + new NVector2(UiScale(4)));
            var toolbarY = Gui.GetCursorScreenPos().Y;
            var buttonOffset = Math.Max(0, (toolbarHeight - UiScale(16) - Gui.GetStyle().FramePadding.Y * 2) / 2);
            DrawAnimationToolbarSeparator(toolbarHeight, toolbarY);
            Gui.SameLine(0, 0);
            Gui.SetCursorScreenPos(new NVector2(Gui.GetCursorScreenPos().X, toolbarY + buttonOffset));
            using (ImGuiScopes.Disabled(preview == null || preview.IsPlaying))
            {
                if (icons!.Button("preview-play", EditorLocalization.Text("Play the animation", "ToolTip"), 16)) Execute(() => preview!.Play());
                if (Controls.Enabled) Controls.Record("animation-preview/play");
            }
            Gui.SameLine(0, 0);
            Gui.SetCursorScreenPos(new NVector2(Gui.GetCursorScreenPos().X, toolbarY + buttonOffset));
            using (ImGuiScopes.Disabled(preview == null || !preview.IsPlaying))
            {
                if (icons.Button("preview-pause", EditorLocalization.Text("Pause the animation", "ToolTip"), 16)) Execute(() => preview!.Pause());
                if (Controls.Enabled) Controls.Record("animation-preview/pause");
            }
            Gui.SameLine(0, 0);
            DrawAnimationToolbarSeparator(toolbarHeight, toolbarY);
            Gui.SameLine(0, UiScale(4));
            var timeOrigin = Gui.GetCursorScreenPos();
            Gui.SetCursorScreenPos(timeOrigin + new NVector2(0, Math.Max(0, (toolbarHeight - Gui.GetFontSize()) / 2)));
            Gui.TextUnformatted($"{preview?.CurrentSeconds ?? 0:0.000} / {preview?.DurationSeconds ?? 0:0.000}");
            Gui.SameLine(0, UiScale(4));
            DrawAnimationToolbarSeparator(toolbarHeight, toolbarY);
            Gui.SameLine(0, UiScale(4));
            var labelOrigin = Gui.GetCursorScreenPos();
            Gui.SetCursorScreenPos(labelOrigin + new NVector2(0, Math.Max(0, (toolbarHeight - Gui.GetFontSize()) / 2)));
            Gui.TextUnformatted(EditorLocalization.Text("Time scale:"));
            Gui.SameLine(0, UiScale(4));
            Gui.SetCursorScreenPos(new NVector2(Gui.GetCursorScreenPos().X, toolbarY + Math.Max(0, (toolbarHeight - Gui.GetFrameHeight()) / 2)));
            Gui.SetNextItemWidth(UiScale(40));
            var rate = preview?.TimeScale ?? 1f;
            using (ImGuiScopes.Disabled(preview == null))
            {
                if (PreviewNumericControl.DrawTimeScale(preview ?? (object)this, "##AnimationTimeScale", ref rate))
                    Execute(() => preview!.TimeScale = Math.Max(0, rate));
                if (Controls.Enabled) Controls.Record("animation-preview/time-scale");
            }
        }
        if (preview == null) return;
        if (preview.RenderError is { } error && !ReferenceEquals(reportedAnimationPreviewError, error))
        { reportedAnimationPreviewError = error; RecordAssetPreviewRenderFailure("Animation preview", error); statusMessage = error.Message; }
        var size = Gui.GetContentRegionAvail();
        var sliderHeight = UiScale(16);
        var stripHeight = UiScale(5 + 25 + 16 + 5);
        size.Y -= stripHeight + Gui.GetStyle().ItemSpacing.Y;
        if (size.X > 0 && size.Y > 0)
        {
            try { preview.Resize((int)size.X, (int)size.Y); }
            catch (Exception resizeError)
            {
                if (!ReferenceEquals(reportedAnimationPreviewError, resizeError))
                { reportedAnimationPreviewError = resizeError; RecordAssetPreviewRenderFailure("Animation preview", resizeError); statusMessage = resizeError.Message; }
                return;
            }
            preview.Visible = true; var origin = Gui.GetCursorScreenPos();
            if (preview.RenderError == null) Gui.Image(preview.TextureRef, size);
            Gui.SetCursorScreenPos(origin);
            DrawAssetPreviewCameraInput("##AnimationImage", size, preview.Camera);
            if (Controls.Enabled) Controls.Record("animation-preview/image");
        }
        var stripOrigin = Gui.GetCursorScreenPos();
        var stripWidth = Gui.GetContentRegionAvail().X;
        var sliderWidth = Math.Max(1, stripWidth - UiScale(20));
        Gui.SetCursorScreenPos(stripOrigin + new NVector2(UiScale(15), UiScale(5)));
        DrawAnimationScale(preview.DurationSeconds, Math.Max(1, sliderWidth - UiScale(10)), sliderWidth);
        Gui.SetCursorScreenPos(stripOrigin + new NVector2(UiScale(10), UiScale(5 + 25)));
        var current = (float)preview.CurrentSeconds; Gui.SetNextItemWidth(sliderWidth);
        bool changed;
        using (ImGuiScopes.Style(ImGuiStyleVar.FramePadding, new NVector2(Gui.GetStyle().FramePadding.X,
            Math.Max(0, (sliderHeight - Gui.GetFontSize()) / 2))))
            changed = PreviewSliderControl.DrawFloat(preview, "##AnimationTime", ref current, 0, (float)preview.DurationSeconds);
        PreviewSliderControl.DrawTicks(0, preview.DurationSeconds, .0333, false);
        TrackAnimationPreviewScrub(preview);
        if (changed) Execute(() => preview.Seek(current));
        if (Controls.Enabled) Controls.Record("animation-preview/time");
        RecordAnimationPreviewControlsSliderItem();
        Gui.SetCursorScreenPos(stripOrigin + new NVector2(0, stripHeight));
        Gui.Dummy(NVector2.Zero);
    }
    private void DrawAnimationToolbarSeparator(float rowHeight, float rowY)
    {
        Gui.SetCursorScreenPos(new NVector2(Gui.GetCursorScreenPos().X, rowY));
        var origin = Gui.GetCursorScreenPos();
        Gui.Dummy(new NVector2(UiScale(13), rowHeight));
        Gui.GetWindowDrawList().AddRectFilled(origin + new NVector2(UiScale(6), UiScale(2)),
            origin + new NVector2(UiScale(7), Math.Max(UiScale(2), rowHeight - UiScale(2))), Gui.GetColorU32(ImGuiCol.Border));
    }
    private unsafe void DrawAnimationScale(double duration, float width, float sliderWidth)
    {
        var origin = Gui.GetCursorScreenPos(); var height = UiScale(25); var draw = Gui.GetWindowDrawList();
        var color = Gui.GetColorU32(ImGuiCol.TextDisabled);
        if (double.IsFinite(duration) && duration > 0 && float.IsFinite(width) && width > 0)
        {
            // Original ScaleFromSliderBehavior: UnitsPerTick=1 and width/range.
            // ScaleBar coerces pixels/tick to 10; its default seconds UnitSystem
            // chooses 2 units per major tick and ten minor subdivisions.
            var pixelsPerSecond = Math.Max(UiScale(10), sliderWidth / duration);
            var majorPixels = pixelsPerSecond * 2;
            var fontSize = UiScale(9);
            for (var tick = 0; tick * majorPixels < width; ++tick)
            {
                var x = origin.X + (float)(tick * majorPixels) + UiScale(1);
                draw.AddLine(new NVector2(x, origin.Y + height * .625f), new NVector2(x, origin.Y + height), color);
                var label = (tick * 2).ToString(System.Globalization.CultureInfo.InvariantCulture) + "s";
                var textWidth = Gui.CalcTextSize(label).X * fontSize / Gui.GetFontSize();
                draw.AddText(Gui.GetFont().Handle, fontSize, new NVector2(x - textWidth * .5f, origin.Y), color, label);
                for (var minor = 1; minor < 10; ++minor)
                {
                    var offset = (tick + minor / 10d) * majorPixels;
                    if (offset > width) break;
                    var minorX = origin.X + (float)offset + UiScale(1);
                    draw.AddLine(new NVector2(minorX, origin.Y + height * .75f), new NVector2(minorX, origin.Y + height), color);
                }
            }
        }
        Gui.Dummy(new NVector2(width, height));
    }
}
