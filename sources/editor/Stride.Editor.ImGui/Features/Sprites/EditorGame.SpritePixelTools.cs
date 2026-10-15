using Stride.Assets.Sprite;
using Stride.Core.Mathematics;
using Stride.Core.Reflection;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private enum SpriteCanvasTool { None, ColorPick, MagicWand, SpriteCenter, Borders }
    private sealed record SpritePixelResult(object Value, bool ColorPick, ItemId Frame, long SourceRevision, long SettingsVersion, bool Union, Rectangle PreviousRegion);
    private sealed class SpritePixelWork(Task<SpritePixelResult> task, SpriteSourceImage pixels)
    {
        internal Task<SpritePixelResult> Task { get; } = task;
        private SpriteSourceImage? retainedPixels = pixels;
        internal void ReleasePixels()
        {
            if (!Task.IsCompleted) throw new InvalidOperationException("The sprite pixel operation is still using its source.");
            retainedPixels?.Dispose(); retainedPixels = null;
        }
    }
    private readonly List<SpritePixelWork> retiredSpritePixelTasks = [];
    private void QueueSpritePixelTool(AssetMetadataDocument document, SpriteSettingFrame frame, SpriteCanvasState state,
        Int2 point, bool union)
    {
        if (!CanMutateSpriteCanvasPresentation(document, frame, state) || !document.CanEditSpriteSettings
            || state.PixelTool != null || state.Pixels == null || state.Tool is not (SpriteCanvasTool.ColorPick or SpriteCanvasTool.MagicWand)) return;
        if (!Services.Commands.TryExecute(ValidatePendingPropertyStrings) || !CanMutateSpriteCanvasPresentation(document, frame, state)
            || state.Pixels == null || state.PixelTool != null || !document.CanEditSpriteSettings
            || state.Tool is not (SpriteCanvasTool.ColorPick or SpriteCanvasTool.MagicWand)) return;
        var pixels = state.Pixels.Retain(); var tool = state.Tool; var sourceRevision = state.SourceRevision;
        var settingsVersion = document.SpriteSettingsEditVersion; var frameId = frame.Id; var region = frame.Value.TextureRegion;
        var transparency = state.MagicWandTransparency; var key = document.SpriteSettings!.ColorKeyColor;
        try { state.PixelTool = new SpritePixelWork(Task.Run(() =>
        {
            object value = tool == SpriteCanvasTool.ColorPick ? pixels.PickColor(point) : pixels.FindRegion(point, transparency, key);
            return new SpritePixelResult(value, tool == SpriteCanvasTool.ColorPick, frameId, sourceRevision, settingsVersion, union, region);
        }), pixels); }
        catch { pixels.Dispose(); throw; }
        if (tool == SpriteCanvasTool.ColorPick) state.Tool = SpriteCanvasTool.None;
    }
    private void PumpSpritePixelTool(AssetMetadataDocument document, SpriteSettingFrame frame, SpriteCanvasState state)
    {
        if (!CanMutateSpriteCanvasPresentation(document, frame, state) || state.PixelTool is not { Task.IsCompleted: true } job) return;
        if (!Services.Commands.TryExecute(ValidatePendingPropertyStrings) || !CanMutateSpriteCanvasPresentation(document, frame, state)) return;
        job.ReleasePixels();
        state.PixelTool = null;
        Execute(() =>
        {
            var result = job.Task.GetAwaiter().GetResult();
            if (!CanMutateSpriteCanvasPresentation(document, frame, state) || !document.CanEditSpriteSettings) return;
            if (frame.Id != result.Frame || state.SourceRevision != result.SourceRevision || document.SpriteSettingsEditVersion != result.SettingsVersion) return;
            if (result.ColorPick)
            {
                var field = document.SpriteSettingsFields.First(candidate => candidate.Node.Name == nameof(SpriteSheetAsset.ColorKeyColor));
                document.UpdateSpriteSetting(field, result.Value); document.EndSpriteSettingEdit();
            }
            else if (result.Value is Rectangle { Width: > 0, Height: > 0 } region && state.Preview is { } preview)
            {
                if (result.Union) region = Rectangle.Union(result.PreviousRegion, region);
                var left = Math.Clamp(region.X, 0, preview.Width); var top = Math.Clamp(region.Y, 0, preview.Height);
                var right = Math.Clamp(region.X + region.Width, left, preview.Width); var bottom = Math.Clamp(region.Y + region.Height, top, preview.Height);
                document.SetSpriteFrameRegion(frame, new Rectangle(left, top, right - left, bottom - top), !result.Union);
            }
        });
    }
}
