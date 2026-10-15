using Stride.Core.IO;
using Stride.Graphics;
using Stride.Core.Reflection;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    [Flags]
    private enum SpriteCanvasDrag { None = 0, Left = 1, Right = 2, Top = 4, Bottom = 8, Move = 16, Pivot = 32, BorderLeft = 64, BorderRight = 128, BorderTop = 256, BorderBottom = 512 }
    private sealed class SpriteCanvasState
    {
        public TextureAssetPreview? Preview;
        public Task<SpriteSourceImage>? Load;
        public CancellationTokenSource? LoadCancellation;
        public SpriteSourceImage? Pixels;
        public UFile? SourceValue;
        public string AssetPath = "", Source = "", LoadingSource = "";
        public long SourceRevision = -1, LoadingRevision;
        public Exception? Error;
        public SpriteCanvasTool Tool;
        public bool Highlight, MagicWandTransparency;
        public System.Numerics.Vector4 BorderColor = new(1, 0, 0, 1);
        public SpritePixelWork? PixelTool;
        public ItemId? FrameId;
        public int LastVisibleFrame;
        public bool Panning;
        public bool ScrollInitialized;
        public float ScrollScale;
        public Stride.Core.Mathematics.Vector2 ScrollOffsets;
        public System.Numerics.Vector2 ScrollSize;
        public float ViewScale = 1;
        public float ViewDpi = 1;
        public System.Numerics.Vector2 ViewScroll;
        public bool RestoreViewScroll = true;
        public SpriteCanvasDrag Drag;
        public SpriteSettingField? DragField;
        public System.Numerics.Vector2 DragLocalPoint;
        public System.Numerics.Vector2 LastDragScreenPoint;
    }
    private readonly Dictionary<AssetMetadataDocument, SpriteCanvasState> spriteCanvases = [];
    private readonly List<Task<SpriteSourceImage>> retiredSpriteSourceLoads = [];
    private readonly List<AssetMetadataDocument> expiredSpriteCanvases = [];
    private static readonly SemaphoreSlim SpriteSourceDecodeSlots = new(2);
    private bool IsSpriteSourceWorkDraining => retiredSpriteSourceLoads.Count != 0 || retiredSpritePixelTasks.Count != 0
        || spriteCanvases.Values.Any(state => state.Load != null || state.PixelTool != null);
    private void BeginSpriteSourceWorkDrain()
    {
        foreach (var state in spriteCanvases.Values)
        {
            state.LoadCancellation?.Cancel(); state.LoadCancellation?.Dispose(); state.LoadCancellation = null;
            if (state.Load != null) { retiredSpriteSourceLoads.Add(state.Load); state.Load = null; }
            if (state.PixelTool != null) { retiredSpritePixelTasks.Add(state.PixelTool); state.PixelTool = null; }
            state.Pixels?.Dispose(); state.Pixels = null;
            state.SourceValue = null; state.AssetPath = "";
        }
        PumpRetiredSpriteSourceWork();
    }
    private SpriteCanvasState GetSpriteCanvasState(AssetMetadataDocument document)
    {
        if (!spriteCanvases.TryGetValue(document, out var state)) spriteCanvases.Add(document,
            state = new SpriteCanvasState { MagicWandTransparency = !document.SpriteSettings!.ColorKeyEnabled });
        return state;
    }
    private SpriteCanvasState GetSpriteCanvas(AssetMetadataDocument document, SpriteSettingFrame frame)
    {
        var state = GetSpriteCanvasState(document);
        state.LastVisibleFrame = frames;
        if (state.FrameId != frame.Id)
        {
            if (state.Drag != SpriteCanvasDrag.None) document.EndSpriteSettingEdit();
            state.Drag = SpriteCanvasDrag.None; state.FrameId = frame.Id;
        }
        if (closeRequested || closePromptOpen || RecoveryTransitionBusy || IsEditorGpuRetirementFinalizing) return state;
        if (workspace != null) WatchSpriteSources(document);
        var revision = spriteSourceWatches.GetValueOrDefault(document)?.Revision ?? 0;
        if (!Equals(state.SourceValue, frame.Value.Source) || state.AssetPath != document.Path || state.SourceRevision != revision)
        {
            state.Error = null;
            if (state.Preview != null) state.ViewScale = state.Preview.Scale / state.ViewDpi;
            state.RestoreViewScroll = true;
            state.ScrollInitialized = false;
            state.Pixels?.Dispose(); state.Pixels = null;
            state.LoadCancellation?.Cancel(); state.LoadCancellation?.Dispose(); state.LoadCancellation = null;
            if (state.Load != null) { retiredSpriteSourceLoads.Add(state.Load); state.Load = null; }
            if (state.Preview != null) { state.Preview.Visible = false; RetireEditorPreview(state.Preview); state.Preview = null; }
            state.SourceValue = frame.Value.Source; state.AssetPath = document.Path; state.SourceRevision = revision;
            state.Source = "";
            try { state.Source = string.IsNullOrEmpty(frame.Value.Source) ? "" : Path.GetFullPath(frame.Value.Source, Path.GetDirectoryName(document.Path)!); }
            catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
            {
                state.Error = error;
                outputLog.Append("Project", Stride.Core.Diagnostics.LogMessageType.Error, "Sprite source", error.Message, error.ToString(), assetId: document.AuthoredAsset.Id);
            }
            if (state.Source.Length != 0)
            {
                state.LoadingSource = state.Source; state.LoadingRevision = revision;
                var source = state.Source; state.LoadCancellation = new CancellationTokenSource(); var token = state.LoadCancellation.Token;
                state.Load = Task.Run(async () =>
                {
                    await SpriteSourceDecodeSlots.WaitAsync(token);
                    try { token.ThrowIfCancellationRequested(); return SpriteSourceImage.Load(source); }
                    finally { SpriteSourceDecodeSlots.Release(); }
                }, token);
            }
        }
        if (state.Load is { IsCompleted: true } completed)
        {
            state.LoadCancellation?.Dispose(); state.LoadCancellation = null;
            state.Load = null;
            try
            {
                var image = completed.GetAwaiter().GetResult();
                if (state.Source == state.LoadingSource && state.SourceRevision == state.LoadingRevision)
                {
                    state.Pixels = image;
                    state.Preview = new TextureAssetPreview(this, renderer!, image.Upload, state.Source);
                    var dpi = Hexa.NET.ImGui.ImGui.GetStyle().FontScaleDpi;
                    state.ViewScroll *= dpi / state.ViewDpi; state.ViewDpi = dpi;
                    state.Preview.SetView(state.ViewScale * dpi, Stride.Core.Mathematics.Vector2.Zero);
                    image.ReleaseUpload();
                }
                else retiredSpriteSourceLoads.Add(completed);
            }
            catch (Exception error)
            {
                EditorGraphicsFailures.ThrowIfDeviceLost(GraphicsDevice, error);
                state.Error = error;
                outputLog.Append("Project", Stride.Core.Diagnostics.LogMessageType.Error, "Sprite source", error.Message, assetId: document.AuthoredAsset.Id);
            }
        }
        return state;
    }
    private void BeginSpriteCanvasFrame()
    {
        if (frames - lastSpriteFrameDrag > 2 && !Hexa.NET.ImGui.ImGui.IsMouseDown(Hexa.NET.ImGui.ImGuiMouseButton.Left))
        { activeSpriteFrameDrag = null; deliveredSpriteFrameDrag = null; }
        spriteFileDropTargets.Clear();
        expiredSpriteCanvases.Clear();
        foreach (var pair in spriteCanvases)
        {
            var state = pair.Value;
            if (state.Preview != null) state.Preview.Visible = false;
            if (state.Drag != SpriteCanvasDrag.None && (!ReferenceEquals(documents.Active?.Document, pair.Key)
                || !Hexa.NET.ImGui.ImGui.IsMouseDown(Hexa.NET.ImGui.ImGuiMouseButton.Left)))
            { pair.Key.EndSpriteSettingEdit(); state.Drag = SpriteCanvasDrag.None; }
            if (frames - state.LastVisibleFrame > 300) expiredSpriteCanvases.Add(pair.Key);
        }
        foreach (var document in expiredSpriteCanvases) ReleaseSpriteCanvas(document);
        PumpRetiredSpriteSourceWork();
    }
    private void PumpRetiredSpriteSourceWork()
    {
        for (var index = retiredSpritePixelTasks.Count - 1; index >= 0; index--)
            if (retiredSpritePixelTasks[index].Task.IsCompleted)
            {
                var job = retiredSpritePixelTasks[index];
                job.ReleasePixels(); _ = job.Task.Exception; retiredSpritePixelTasks.RemoveAt(index);
            }
        for (var index = retiredSpriteSourceLoads.Count - 1; index >= 0; index--)
            if (retiredSpriteSourceLoads[index].IsCompleted)
            {
                var task = retiredSpriteSourceLoads[index];
                if (task.IsCompletedSuccessfully) task.GetAwaiter().GetResult().Dispose();
                else _ = task.Exception;
                retiredSpriteSourceLoads.RemoveAt(index);
            }
    }
    private void RenderSpriteCanvases()
    {
        foreach (var pair in spriteCanvases)
        {
            var state = pair.Value; state.Preview?.Render();
            if (state.Preview?.RenderError is { } error && !ReferenceEquals(state.Error, error))
            {
                state.Error = error;
                outputLog.Append("Project", Stride.Core.Diagnostics.LogMessageType.Error, "Sprite source", error.Message, error.ToString(), assetId: pair.Key.AuthoredAsset.Id);
            }
        }
    }
    private void ReleaseSpriteCanvas(AssetMetadataDocument document)
    {
        if (!spriteCanvases.TryGetValue(document, out var state)) return;
        document.EndSpriteSettingEdit();
        state.LoadCancellation?.Cancel(); state.LoadCancellation?.Dispose(); state.LoadCancellation = null;
        if (state.Preview != null) { state.Preview.Visible = false; RetireEditorPreview(state.Preview); state.Preview = null; }
        if (state.Load != null) { retiredSpriteSourceLoads.Add(state.Load); state.Load = null; }
        state.Pixels?.Dispose(); state.Pixels = null;
        if (state.PixelTool != null) { retiredSpritePixelTasks.Add(state.PixelTool); state.PixelTool = null; }
        spriteCanvases.Remove(document);
    }
    private void ClearSpriteCanvases()
    {
        foreach (var document in spriteCanvases.Keys.ToArray()) ReleaseSpriteCanvas(document);
        PumpRetiredSpriteSourceWork();
    }
}
