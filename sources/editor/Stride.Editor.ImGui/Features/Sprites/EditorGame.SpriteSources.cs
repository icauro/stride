using System.Collections.Concurrent;
using Stride.Assets;
using Stride.Assets.Textures;
using Stride.Core.Assets;
using Stride.Graphics;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private readonly record struct SpriteCompilationRevision(long Settings, long Source);
    private sealed class SpriteSourceWatch : IDisposable
    {
        public string Path = "", Url = "";
        public string[] Sources = [];
        public long Settings = -1, Revision;
        public TextureImportContext Context;
        public SpriteCompilationRevision Pending;
        public DateTime Debounce;
        public List<FileSystemWatcher> Watchers = [];
        public void Dispose() { foreach (var watcher in Watchers) watcher.Dispose(); Watchers.Clear(); }
    }
    private readonly Dictionary<AssetMetadataDocument, SpriteSourceWatch> spriteSourceWatches = [];
    private readonly ConcurrentQueue<AssetId> changedSpriteSources = new();
    private SpriteSourceWatch WatchSpriteSources(AssetMetadataDocument document)
    {
        var settings = workspace!.Package.GetGameSettingsAssetOrDefault();
        var rendering = settings.GetOrCreate<RenderingSettings>();
        var context = new TextureImportContext(rendering.DefaultGraphicsProfile, settings.GetOrCreate<TextureSettings>().TextureQuality,
            rendering.ColorSpace, Stride.Graphics.GraphicsDevice.Platform);
        if (!spriteSourceWatches.TryGetValue(document, out var watch))
            spriteSourceWatches.Add(document, watch = new SpriteSourceWatch { Context = context, Url = workspace.FindAsset(document.AuthoredAsset.Id).Location.ToString() });
        else if (watch.Context != context) { watch.Context = context; watch.Revision++; }
        if (watch.Settings == document.SpriteSettingsEditVersion && watch.Path == document.Path) return watch;
        var parent = Path.GetDirectoryName(document.Path)!;
        var sources = document.SpriteSettings!.Sprites.Where(sprite => !string.IsNullOrEmpty(sprite.Source))
            .Select(sprite => Path.GetFullPath(sprite.Source, parent)).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        watch.Settings = document.SpriteSettingsEditVersion;
        if (watch.Path == document.Path && sources.SequenceEqual(watch.Sources, StringComparer.OrdinalIgnoreCase)) return watch;
        if (watch.Path.Length > 0) watch.Revision++;
        watch.Dispose(); watch.Path = document.Path; watch.Sources = sources;
        watch.Url = workspace.FindAsset(document.AuthoredAsset.Id).Location.ToString();
        var id = document.AuthoredAsset.Id;
        foreach (var group in sources.GroupBy(source => Path.GetDirectoryName(source)!, StringComparer.OrdinalIgnoreCase))
        {
            var directory = group.Key;
            while (!Directory.Exists(directory) && Path.GetDirectoryName(directory) is { } ancestor) directory = ancestor;
            if (!Directory.Exists(directory)) continue;
            var watched = group.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var watcher = new FileSystemWatcher(directory)
            { IncludeSubdirectories = !string.Equals(directory, group.Key, StringComparison.OrdinalIgnoreCase), NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite };
            void Changed(object sender, FileSystemEventArgs args)
            {
                if (watched.Contains(args.FullPath) || args is RenamedEventArgs renamed && watched.Contains(renamed.OldFullPath)) changedSpriteSources.Enqueue(id);
            }
            watcher.Changed += Changed; watcher.Created += Changed; watcher.Deleted += Changed; watcher.Renamed += (sender, args) => Changed(sender, args);
            watcher.Error += (_, _) => changedSpriteSources.Enqueue(id);
            try { watcher.EnableRaisingEvents = true; watch.Watchers.Add(watcher); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { watcher.Dispose(); outputLog.Append("Project", Stride.Core.Diagnostics.LogMessageType.Warning, "Sprite source", error.Message); }
        }
        return watch;
    }
    private SpriteCompilationRevision SpriteRevision(AssetMetadataDocument document)
        => new(document.SpriteSettingsEditVersion, spriteSourceWatches.GetValueOrDefault(document)?.Revision ?? 0);
    private void ClearSpriteSourceWatches()
    {
        ClearSpriteImageSizes();
        foreach (var watch in spriteSourceWatches.Values) watch.Dispose();
        spriteSourceWatches.Clear(); while (changedSpriteSources.TryDequeue(out _)) { }
    }
}
