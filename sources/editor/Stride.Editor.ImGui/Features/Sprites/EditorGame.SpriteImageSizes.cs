using Stride.Core.Reflection;
using Stride.TextureConverter;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private sealed class SpriteImageSizeState
    {
        public string Path = "";
        public long Settings = -1, SourceRevision = -1;
        public (ItemId Id, string Source)[] Sources = [];
        public Task<(ItemId Id, string Source, int Width, int Height)[]>? Pending;
        public long PendingSettings, PendingSource;
        public string PendingPath = "";
    }
    private readonly Dictionary<AssetMetadataDocument, SpriteImageSizeState> spriteImageSizeStates = [];
    private void PumpSpriteImageSizes()
    {
        if (workspace == null) return;
        PumpSpriteSourceEdits();
        foreach (var owner in documents.AuthoredDocuments)
        {
            if (owner is not AssetMetadataDocument { SpriteSettings: not null } document) continue;
            var watch = WatchSpriteSources(document);
            if (!spriteImageSizeStates.TryGetValue(document, out var state)) spriteImageSizeStates.Add(document, state = new());
            if (state.Pending is { IsCompleted: true } finished)
            {
                state.Pending = null;
                Execute(() =>
                {
                    var sizes = finished.GetAwaiter().GetResult();
                    if (document.Path != state.PendingPath || document.SpriteSettingsEditVersion != state.PendingSettings || watch.Revision != state.PendingSource)
                    { state.SourceRevision = -1; return; }
                    document.RefreshSpriteImageSizes(sizes);
                    state.Settings = document.SpriteSettingsEditVersion;
                });
            }
            if (state.Pending != null || state.Settings == document.SpriteSettingsEditVersion && state.SourceRevision == watch.Revision && state.Path == document.Path) continue;
            var sources = document.SpriteSettingsFrames.Select(frame => (frame.Id, Source: frame.Value.Source?.ToString() ?? "")).ToArray();
            var unchanged = state.Path == document.Path && state.SourceRevision == watch.Revision && sources.SequenceEqual(state.Sources);
            state.Settings = document.SpriteSettingsEditVersion; state.SourceRevision = watch.Revision; state.Path = document.Path;
            if (unchanged) continue;
            state.Sources = sources; state.PendingSettings = state.Settings; state.PendingSource = watch.Revision; state.PendingPath = state.Path;
            var parent = Path.GetDirectoryName(document.Path)!;
            state.Pending = Task.Run(async () =>
            {
                await SpriteSourceDecodeSlots.WaitAsync();
                try
                {
                    using var tool = new TextureTool(); var dimensions = new Dictionary<string, (int Width, int Height)>(StringComparer.OrdinalIgnoreCase);
                    var result = new (ItemId Id, string Source, int Width, int Height)[sources.Length];
                    for (var index = 0; index < sources.Length; index++)
                    {
                        var source = sources[index];
                        if (!dimensions.TryGetValue(source.Source, out var size))
                        {
                            var path = string.IsNullOrEmpty(source.Source) ? "" : Path.GetFullPath(source.Source, parent);
                            if (!File.Exists(path)) size = (0, 0);
                            else { using var image = tool.Load(path, false); size = (image.Width, image.Height); }
                            dimensions.Add(source.Source, size);
                        }
                        result[index] = (source.Id, source.Source, size.Width, size.Height);
                    }
                    return result;
                }
                finally { SpriteSourceDecodeSlots.Release(); }
            });
        }
    }
    private void ReleaseSpriteImageSizes(AssetMetadataDocument document)
    {
        ReleaseSpriteSourceEdit(document);
        if (!spriteImageSizeStates.Remove(document, out var state) || state.Pending == null) return;
        // This task owns only CPU decoder resources. Observe any retired failure;
        // it cannot mutate a released document or upload graphics resources.
        _ = state.Pending.ContinueWith(task => { _ = task.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
    }
    private void ClearSpriteImageSizes()
    {
        ClearSpriteSourceEdits();
        foreach (var document in spriteImageSizeStates.Keys.ToArray()) ReleaseSpriteImageSizes(document);
    }
}
