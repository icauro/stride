using System.Collections.Concurrent;
using Stride.Assets;
using Stride.Assets.SpriteFont;
using Stride.Core;
using Stride.Core.Assets;
using Stride.Core.Diagnostics;
using Stride.Core.Serialization;
using Stride.Core.Storage;
using Stride.Graphics;
using Stride.Graphics.Font;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private readonly record struct FontRevision(long Settings, long Source, GraphicsProfile Profile, ColorSpace ColorSpace);
    private sealed class FontSourceWatch : IDisposable
    {
        public string Path = "", Url = ""; public string[] Sources = []; public long Settings = -1, Source;
        public FontRevision Pending; public DateTime Debounce;
        public List<FileSystemWatcher> Watchers = [];
        public void Dispose() { foreach (var watcher in Watchers) watcher.Dispose(); Watchers.Clear(); }
    }
    private sealed record InstalledFont(CompiledFontContent Content, Asset Snapshot, (string Url, ObjectId Id)[] Identity)
    {
        public long CheckedEpoch = -1; public bool Current; public int Leases;
        private SpriteFont lastFont = Content.Font;
        internal SpriteFont RuntimeFont
        {
            get { if (Content.ActiveFont is { } active) lastFont = active; return lastFont; }
        }
    }
    private sealed class CompiledFontLease(InstalledFont font) : IDisposable
    {
        private InstalledFont? owner = Pin(font);
        private static InstalledFont Pin(InstalledFont value) { Interlocked.Increment(ref value.Leases); return value; }
        public CompiledProjectContent Bundle => owner?.Content.Bundle ?? throw new ObjectDisposedException(nameof(CompiledFontLease));
        public SpriteFont Font => owner?.Content.Font ?? throw new ObjectDisposedException(nameof(CompiledFontLease));
        public void Dispose() { var value = Interlocked.Exchange(ref owner, null); if (value != null) Interlocked.Decrement(ref value.Leases); }
    }
    private CompiledFontLease? AcquireCompiledFont(string url) => refreshedFonts.TryGetValue(url, out var font) && HasCurrentFontContent(font) ? new(font) : null;
    private bool FontContentHasConsumers(CompiledFontContent content, SpriteFont? font = null) =>
        fontPreview is { IsDisposed: false } preview && ReferenceEquals(preview.SourceContent, content.Bundle)
        || retiredEditorPreviews.Any(retired => retired.Preview is SpriteFontAssetPreview { IsDisposed: false } old && ReferenceEquals(old.SourceContent, content.Bundle))
        || SnapshotSceneResourceViews(includeRetiring: true).Any(view => view.Viewport.UsesRuntimeFont(font ?? content.Font, view.Document));
    private readonly Dictionary<AssetMetadataDocument, FontSourceWatch> fontSourceWatches = [];
    private readonly ConcurrentQueue<AssetId> changedFontSources = new();
    private readonly Dictionary<AssetMetadataDocument, FontRevision> compiledFontRevisions = [];
    private readonly Dictionary<string, InstalledFont> refreshedFonts = new(StringComparer.Ordinal);
    private readonly List<(InstalledFont Owner, int Frame)> retiredFonts = [];
    private readonly Dictionary<InstalledFont, RetiredFontContent> fontCleanupOwners = new(ReferenceEqualityComparer.Instance);
    private RetiredFontContent GetFontCleanupOwner(InstalledFont owner)
    {
        if (!fontCleanupOwners.TryGetValue(owner, out var cleanup))
            fontCleanupOwners.Add(owner, cleanup = new(this, owner));
        return cleanup;
    }
    private sealed class RetiredFontContent(EditorGame host, InstalledFont owner) : IDisposable, IRuntimeTextureConsumer
    {
        private readonly SpriteFont font = owner.RuntimeFont;
        public bool UsesRuntimeTexture(Texture texture) => !disposed
            && (CompiledTextureConsumers.UsesTexture(font, texture) || owner.Content.UsesRuntimeTexture(texture));
        private bool disposed;
        public void Dispose()
        {
            if (disposed) return;
            if (Volatile.Read(ref owner.Leases) != 0 || host.FontContentHasConsumers(owner.Content, font))
                throw new PreviewResourceDrainPendingException();
            owner.Content.Dispose(); disposed = true;
            host.fontCleanupOwners.Remove(owner);
        }
    }
    private Task<string>? fontBuild;
    private CancellationTokenSource? fontCancellation;
    private AssetMetadataDocument? compilingFontDocument;
    private Asset? compilingFontSnapshot;
    private FontCompilationSources? compilingFontSources;
    private FontRevision compilingFontRevision;
    private string? compilingFontPath, compilingFontUrl;
    private ProjectWorkspace? fontWorkspace;
    private int fontBuildLevel;
    private long fontContentEpoch;
    private long GetCompiledFontGeneration() => fontContentEpoch;
    private int detachedFontBuilds;
    private bool IsFontSettingsUpdating => fontBuild != null || Volatile.Read(ref detachedFontBuilds) != 0;
    private void NotifyFontCompiledContentInstalled() => fontContentEpoch++;
    private FontSourceWatch WatchFontSources(AssetMetadataDocument owner)
    {
        if (!fontSourceWatches.TryGetValue(owner, out var watch)) fontSourceWatches.Add(owner, watch = new());
        if (watch.Settings == owner.FontSettingsEditVersion && watch.Path == owner.Path) return watch;
        var files = FontSourcePaths.Files(owner.AuthoredAsset, owner.Path).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        watch.Settings = owner.FontSettingsEditVersion;
        if (watch.Path == owner.Path && files.SequenceEqual(watch.Sources, StringComparer.OrdinalIgnoreCase)) return watch;
        if (watch.Path.Length != 0) watch.Source++;
        watch.Dispose(); watch.Path = owner.Path; watch.Sources = files; watch.Url = workspace!.FindAsset(owner.AuthoredAsset.Id).Location.ToString();
        var directories = files.GroupBy(Path.GetDirectoryName, StringComparer.OrdinalIgnoreCase).Where(group => group.Key != null)
            .Select(group => (Directory: group.Key!, Files: group.ToHashSet(StringComparer.OrdinalIgnoreCase))).ToList();
        if (owner.AuthoredAsset is SpriteFontAsset { FontSource: SystemFontProvider } && directories.Count == 0)
            directories.Add((Environment.GetFolderPath(Environment.SpecialFolder.Fonts), new(StringComparer.OrdinalIgnoreCase)));
        foreach (var entry in directories)
        {
            var directory = entry.Directory;
            while (!Directory.Exists(directory) && Path.GetDirectoryName(directory) is { } ancestor) directory = ancestor;
            if (!Directory.Exists(directory)) continue;
            var watcher = new FileSystemWatcher(directory) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size };
            void Changed(object sender, FileSystemEventArgs args)
            {
                if (entry.Files.Count == 0 || entry.Files.Contains(args.FullPath) || args is RenamedEventArgs renamed && entry.Files.Contains(renamed.OldFullPath)) changedFontSources.Enqueue(owner.AuthoredAsset.Id);
            }
            watcher.Changed += Changed; watcher.Created += Changed; watcher.Deleted += Changed; watcher.Renamed += (sender, args) => Changed(sender, args);
            watcher.Error += (_, _) => changedFontSources.Enqueue(owner.AuthoredAsset.Id);
            try { watcher.EnableRaisingEvents = true; watch.Watchers.Add(watcher); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { watcher.Dispose(); outputLog.Append("Project", LogMessageType.Warning, "Font source", error.Message); }
        }
        return watch;
    }
    private FontRevision CurrentFontRevision(AssetMetadataDocument owner)
    {
        var settings = workspace!.Package.GetGameSettingsAssetOrDefault();
        var settingsItem = workspace.AssetItems.FirstOrDefault(item => item.Asset is GameSettingsAsset);
        if (settingsItem != null && authoring?.Find(settingsItem.Id)?.Document is IAssetTagDocument retained) settings = (GameSettingsAsset)retained.AuthoredAsset;
        var rendering = settings.GetOrDefault<RenderingSettings>();
        return new(owner.FontSettingsEditVersion, WatchFontSources(owner).Source, rendering.DefaultGraphicsProfile, rendering.ColorSpace);
    }
    private bool HasCurrentFontContent(InstalledFont installed)
    {
        if (installed.CheckedEpoch == fontContentEpoch) return installed.Current;
        installed.CheckedEpoch = fontContentEpoch;
        var map = Content.FileProvider.ObjectDatabase.ContentIndexMap;
        foreach (var entry in installed.Identity) if (!map.TryGetValue(entry.Url, out var id) || id != entry.Id) return installed.Current = false;
        return installed.Current = true;
    }
    private void PumpFontSettingsUpdate()
    {
        if (!ReferenceEquals(fontWorkspace, workspace)) { ClearCompiledFonts(); fontWorkspace = workspace; }
        RestoreRetiredFontPresentation();
        PumpFontPreviewText();
        while (changedFontSources.TryDequeue(out var id)) foreach (var entry in fontSourceWatches) if (entry.Key.AuthoredAsset.Id == id) entry.Value.Source++;
        foreach (var installed in refreshedFonts.Values) installed.Content.Draw();
        for (var index = retiredFonts.Count - 1; index >= 0; index--)
        {
            var retired = retiredFonts[index];
            if (frames < retired.Frame || Volatile.Read(ref retired.Owner.Leases) != 0 || FontContentHasConsumers(retired.Owner.Content, retired.Owner.RuntimeFont))
            { retired.Owner.Content.Draw(); continue; }
            RetireEditorPreview(GetFontCleanupOwner(retired.Owner)); retiredFonts.RemoveAt(index);
        }
        if (fontBuild is { IsCompleted: false } && compilingFontDocument is { } current
            && (current.Path != compilingFontPath || CurrentFontRevision(current) != compilingFontRevision)) fontCancellation!.Cancel();
        if (fontBuild is { IsCompleted: true } finished)
        {
            fontBuild = null; var owner = compilingFontDocument!; compilingFontDocument = null;
            var canceled = fontCancellation!.IsCancellationRequested; fontCancellation.Dispose(); fontCancellation = null;
            Execute(() =>
            {
                string? output = null;
                try
                {
                    output = finished.GetAwaiter().GetResult();
                    if (canceled || owner.Path != compilingFontPath || CurrentFontRevision(owner) != compilingFontRevision
                        || !documents.AuthoredDocuments.Any(document => ReferenceEquals(document, owner))) return;
                    if (FontCompilationSources.Capture(owner.AuthoredAsset, owner.Path).Revision != compilingFontSources!.Revision)
                    { if (fontSourceWatches.TryGetValue(owner, out var changed)) changed.Source++; return; }
                    InstallCompiledFont(ref output, compilingFontUrl!, owner, compilingFontSnapshot!);
                    SetAssetThumbnailBuildStatus(owner.AuthoredAsset.Id, new((LogMessageType)Volatile.Read(ref fontBuildLevel), AssetThumbnailFailure.None));
                }
                catch (OperationCanceledException) when (canceled) { }
                catch (Exception error)
                { if (!canceled) { RecordAssetPreviewBuildFailure(owner.AuthoredAsset.Id, error, AssetThumbnailFailure.Broken); throw; } }
                finally { if (output != null) CompiledFontBuild.ReleaseOutput(output); }
            });
            if (documents.AuthoredDocuments.Any(document => ReferenceEquals(document, owner))) compiledFontRevisions[owner] = compilingFontRevision;
            compilingFontPath = compilingFontUrl = null; compilingFontSnapshot = null;
            compilingFontSources = null;
        }
        if (!CanStartAuthoringPreviewUpdate) return;
        foreach (var candidate in documents.AuthoredDocuments)
        {
            if (candidate is not AssetMetadataDocument { IsFontAsset: true } owner || !owner.CanEditFontSettings && !ReferenceEquals(requestedFontDocument, owner)) continue;
            var revision = CurrentFontRevision(owner); var watch = fontSourceWatches[owner];
            if (compiledFontRevisions.TryGetValue(owner, out var attempted) && attempted == revision
                && (!refreshedFonts.TryGetValue(watch.Url, out var existing) || HasCurrentFontContent(existing))) continue;
            if (!compiledFontRevisions.ContainsKey(owner) && owner.FontSettingsEditVersion == 0 && watch.Source == 0
                && !refreshedFonts.ContainsKey(watch.Url) && !ReferenceEquals(requestedFontDocument, owner)
                && Content.FileProvider.ObjectDatabase.ContentIndexMap.Contains(watch.Url)) { compiledFontRevisions[owner] = revision; continue; }
            if (watch.Pending != revision || watch.Debounce == default) { watch.Pending = revision; watch.Debounce = DateTime.UtcNow.AddMilliseconds(220); continue; }
            if (DateTime.UtcNow < watch.Debounce) continue;
            try
            {
            compilingFontDocument = owner; compilingFontRevision = revision; compilingFontPath = owner.Path; compilingFontUrl = watch.Url;
            compilingFontSnapshot = AssetCloner.Clone(owner.AuthoredAsset); FontSourcePaths.Resolve(compilingFontSnapshot, owner.Path);
            if (compilingFontSnapshot is SpriteFontAsset sourceFont) sourceFont.FontSource?.GetFontPath();
            compilingFontSources = FontCompilationSources.Capture(owner.AuthoredAsset, owner.Path);
            var sourceInputs = compilingFontSources;
            var snapshot = compilingFontSnapshot; var parameters = new FontPreviewBuildParameters
            { AssetPath = owner.Path, Url = watch.Url, Platform = PlatformType.Windows, GraphicsPlatform = Stride.Graphics.GraphicsDevice.Platform, GraphicsProfile = revision.Profile, ColorSpace = revision.ColorSpace };
            fontBuildLevel = (int)LogMessageType.Info; fontCancellation = new(); var token = fontCancellation.Token;
            fontBuild = Task.Run(() => CompiledFontBuild.Build(snapshot, parameters, token, state => { AccumulatePreviewBuildLevel(ref fontBuildLevel, state); RecordBuildOutput(state); }, sourceInputs), token);
            }
            catch (Exception error)
            {
                compilingFontDocument = null; compilingFontSnapshot = null; compilingFontSources = null;
                compilingFontPath = compilingFontUrl = null; compiledFontRevisions[owner] = revision;
                RecordAssetPreviewBuildFailure(owner.AuthoredAsset.Id, error, AssetThumbnailFailure.Broken);
            }
            break;
        }
    }
    private void InstallCompiledFont(ref string? output, string url, AssetMetadataDocument owner, Asset snapshot)
    {
        NotifyFontCompiledContentInstalled();
        var next = CompiledFontContent.AdoptCompiledOutput(this, ref output, url);
        (string Url, ObjectId Id)[] identity = [];
        try
        {
        var map = Content.FileProvider.ObjectDatabase.ContentIndexMap;
        var urls = new HashSet<string>(StringComparer.Ordinal) { url };
        foreach (var texture in next.Font.Textures ?? []) if (AttachedReferenceManager.GetUrl(texture) is { } location) { urls.Add(location); if (map.Contains(location + "_Data")) urls.Add(location + "_Data"); }
        if (snapshot is SpriteFontAsset { FontType: RuntimeRasterizedSpriteFontType } font) urls.Add(FontHelper.GetFontPath(font.FontSource.GetFontName(), font.FontSource.Style));
        identity = urls.Select(location => { map.TryGetValue(location, out var id); return (location, id); }).ToArray();
        foreach (var view in SnapshotSceneResourceViews())
            view.Viewport.ReplaceRuntimeFont(url, next.Font, view.Document);
        }
        catch
        {
            // Retain failed adoption for guarded retirement, including retryable native cleanup failures.
            retiredFonts.Add((new(next, snapshot, identity), frames + 2));
            throw;
        }
        var previous = refreshedFonts.GetValueOrDefault(url); refreshedFonts[url] = new(next, snapshot, identity);
        if (previous != null) retiredFonts.Add((previous, frames + 2));
        ReloadFontPreview(owner); InvalidateAssetThumbnails(owner.AuthoredAsset.Id);
    }
    private void ReleaseFontSettingsDocument(IEditorDocument document)
    {
        if (document is not AssetMetadataDocument owner) return;
        if (ReferenceEquals(requestedFontDocument, owner) || ReferenceEquals(fontPreviewDocument, owner)) CloseFontPreview();
        if (fontSourceWatches.Remove(owner, out var watch)) watch.Dispose(); compiledFontRevisions.Remove(owner);
        if (ReferenceEquals(compilingFontDocument, owner)) fontCancellation?.Cancel();
    }
    private void StopFontSettingsUpdate()
    {
        var build = fontBuild; var cancellation = fontCancellation; cancellation?.Cancel(); fontBuild = null; fontCancellation = null;
        if (build == null) cancellation?.Dispose();
        else
        {
            Interlocked.Increment(ref detachedFontBuilds);
            _ = build.ContinueWith(completed =>
            {
                try { if (completed.Status == TaskStatus.RanToCompletion) CompiledFontBuild.ReleaseOutput(completed.Result);
                    else if (completed.IsFaulted) _ = completed.Exception; }
                catch (Exception error) { outputLog.Append("Project", LogMessageType.Error, "Font compilation", error.Message, error.ToString()); }
                finally { cancellation?.Dispose(); Interlocked.Decrement(ref detachedFontBuilds); }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }
        compilingFontDocument = null; compilingFontSnapshot = null; compilingFontSources = null; compilingFontPath = compilingFontUrl = null;
    }
    private void ClearCompiledFonts()
    {
        ForgetRetiredFontPresentation();
        CloseFontPreview(); StopFontSettingsUpdate();
        foreach (var watch in fontSourceWatches.Values) watch.Dispose(); fontSourceWatches.Clear(); compiledFontRevisions.Clear();
        while (changedFontSources.TryDequeue(out _)) { }
        foreach (var installed in refreshedFonts.Values.Concat(retiredFonts.Select(retired => retired.Owner)).Distinct().ToArray())
        {
            GetFontCleanupOwner(installed).Dispose();
            foreach (var pair in refreshedFonts.Where(pair => ReferenceEquals(pair.Value, installed)).ToArray()) refreshedFonts.Remove(pair.Key);
            retiredFonts.RemoveAll(retired => ReferenceEquals(retired.Owner, installed));
        }
    }
}
