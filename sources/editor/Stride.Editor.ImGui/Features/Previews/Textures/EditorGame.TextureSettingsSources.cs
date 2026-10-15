using System.Collections.Concurrent;
using Stride.Assets;
using Stride.Assets.Textures;
using Stride.Core.Assets;
using Stride.Core.IO;
using Stride.Graphics;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private readonly record struct TextureSettingsRevision(long Settings, long Source);
    private readonly record struct TextureImportContext(GraphicsProfile Profile, TextureQuality Quality, ColorSpace ColorSpace, GraphicsPlatform GraphicsPlatform);
    private sealed class TextureSettingsSourceWatch : IDisposable
    {
        public string Source = "", AssetPath = "";
        public UFile? SourceValue;
        public long Revision;
        public TextureImportContext Context;
        public TextureSettingsRevision Pending;
        public DateTime Debounce;
        public FileSystemWatcher? Watcher;
        public void Dispose() => Watcher?.Dispose();
    }
    private readonly Dictionary<AssetMetadataDocument, TextureSettingsSourceWatch> textureSourceWatches = [];
    private readonly ConcurrentQueue<AssetId> changedTextureSources = new();
    private TextureSettingsSourceWatch WatchTextureSettingsSource(AssetMetadataDocument document)
    {
        var settings = workspace!.Package.GetGameSettingsAssetOrDefault();
        var rendering = settings.GetOrCreate<RenderingSettings>();
        var context = new TextureImportContext(rendering.DefaultGraphicsProfile, settings.GetOrCreate<TextureSettings>().TextureQuality,
            rendering.ColorSpace, Stride.Graphics.GraphicsDevice.Platform);
        if (!textureSourceWatches.TryGetValue(document, out var watch))
        { textureSourceWatches.Add(document, watch = new TextureSettingsSourceWatch { Context = context }); }
        else if (watch.Context != context) { watch.Context = context; watch.Revision++; }
        if (Equals(watch.SourceValue, document.TextureSettings!.Source) && watch.AssetPath == document.Path) return watch;
        var source = string.IsNullOrEmpty(document.TextureSettings.Source) ? ""
            : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(document.Path)!, document.TextureSettings.Source.ToOSPath()));
        if (watch.Source.Length != 0) watch.Revision++;
        watch.Dispose(); watch.Watcher = null; watch.Source = source; watch.AssetPath = document.Path; watch.SourceValue = document.TextureSettings.Source;
        var parent = source.Length == 0 ? null : Path.GetDirectoryName(source);
        if (parent != null && Directory.Exists(parent))
        {
            var id = document.AuthoredAsset.Id;
            var watcher = new FileSystemWatcher(parent, Path.GetFileName(source))
            { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName };
            watcher.Changed += (_, _) => changedTextureSources.Enqueue(id);
            watcher.Created += (_, _) => changedTextureSources.Enqueue(id);
            watcher.Deleted += (_, _) => changedTextureSources.Enqueue(id);
            watcher.Renamed += (_, _) => changedTextureSources.Enqueue(id);
            try { watcher.EnableRaisingEvents = true; watch.Watcher = watcher; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                watcher.Dispose();
                outputLog.Append("Project", Stride.Core.Diagnostics.LogMessageType.Warning, "Texture source", error.Message);
            }
        }
        return watch;
    }
    private void PumpTextureSourceChanges()
    {
        while (changedTextureSources.TryDequeue(out var id))
            foreach (var pair in textureSourceWatches)
                if (pair.Key.AuthoredAsset.Id == id) pair.Value.Revision++;
    }
    private TextureSettingsRevision TextureRevision(AssetMetadataDocument document)
        => new(document.TextureSettingsEditVersion, textureSourceWatches.GetValueOrDefault(document)?.Revision ?? 0);
    private void ClearTextureSourceWatches()
    {
        foreach (var watch in textureSourceWatches.Values) watch.Dispose();
        textureSourceWatches.Clear(); while (changedTextureSources.TryDequeue(out _)) { }
    }
}
