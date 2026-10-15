using System.Collections.Concurrent;
using Hexa.NET.ImGui;
using Stride.Assets;
using Stride.Assets.Models;
using Stride.Assets.Materials;
using Stride.Assets.Effect;
using Stride.Assets.Textures;
using Stride.Assets.Skyboxes;
using Stride.Assets.Sprite;
using Stride.Assets.SpriteFont;
using Stride.Physics.Assets;
using Stride.Assets.Entities;
using Stride.UI.Assets;
using Stride.SpriteStudio.Assets;
using Stride.Core.Assets;
using Stride.Engine;
using Stride.Core.Diagnostics;
using Stride.Graphics;
using Stride.Core.Assets.Compiler;
using Stride.Core.Reflection;
using Stride.Editor.Thumbnails;
using Stride.Core.Assets.Tracking;

namespace Stride.Editor.ImGui;

internal sealed record AssetThumbnailSource(CompiledProjectContent? Content, Asset Asset, string Url,
    ColorSpace ColorSpace, string CompiledIdentity, string SettingsIdentity, RenderingMode RenderingMode,
    IReadOnlyList<AssetId> DependencyIds, string CacheKey, string? PreviewModelUrl = null, string? StaticIcon = null,
    NativeThumbnailCompilationSnapshot? NativeCompilation = null)
{
    public PackageTypeScope? TypeScope { get; init; }
    public CompiledProjectContent RequireCompiledContent() => Content ?? throw new AssetThumbnailNotCompiledException(Url);
}

/// <summary>Visible-first, bounded GPU thumbnail cache over actual compiled engine resources.</summary>
internal sealed class AssetThumbnailService : IDisposable
{
    private sealed record Provider(Type AssetType, int Priority, Func<AssetThumbnailSource, (AssetThumbnailImage Thumbnail, byte[] Png)> Generate,
        bool RequiresCompilation = true, Type? NativeCompiler = null);
    private readonly Dictionary<Type, Provider> providers = [];
    private sealed class NativeReadState
    {
        public int Level = (int)LogMessageType.Info;
        public bool FromDisk;
    }
    private sealed class ThumbnailBatch(GraphicsDevice device) : SpriteBatch(device)
    {
        protected override void Destroy()
        {
            DefaultEffect.Effect.Dispose(); DefaultEffect.Dispose();
            DefaultEffectSRgb.Effect.Dispose(); DefaultEffectSRgb.Dispose();
            TextureSpriteFontEffect.Effect.Dispose(); TextureSpriteFontEffect.Dispose();
            base.Destroy();
        }
    }
    private sealed class Request(AssetEntry asset, Provider provider)
    {
        public AssetEntry Asset = asset;
        public Provider Provider = provider;
        public AssetThumbnailSource? Source;
        public IDisposable? SourceArtifacts;
        public string? Key, BlockedIdentity;
        public long VisibleFrame, ValidatedFrame = -1000, Sequence;
        public long RetryFrame;
        public AssetThumbnailImage? Thumbnail;
        public Task<byte[]?>? CacheRead;
        public string? ReadKey, ReadNativeIdentity;
        public CancellationTokenSource? NativeCancellation;
        public bool SkipPersistentCache;
        public NativeReadState? NativeRead;
        public long ReadGeneration;
        public bool Failed, Invalid = true;
        public AssetThumbnailFailure Failure;
        public long StatusEpoch = -1;
        public AssetThumbnailStatus Status;
        public LogMessageType StatusNativeLevel;
    }
    private readonly Game game;
    private readonly GraphicsDevice device;
    private readonly ImGuiRenderer renderer;
    private readonly Func<AssetEntry, AssetThumbnailSource> source;
    private readonly Action<AssetEntry, Exception> reportError;
    private readonly Action<AssetId?> invalidateSource;
    private readonly Action<ProjectLoadState> nativeProgress;
    private readonly NativeThumbnailResidentQueue nativeQueue;
    private readonly AssetCompilerRegistry nativeRegistry = new();
    private readonly Dictionary<Type, (IThumbnailCompiler Compiler, Provider Provider)> nativeProviders = [];
    private volatile bool disposed;
    private bool shutdownComplete, eventsReleased, customReleased, batchReleased, cancellationReleased;
    private Task? nativeShutdown;
    public bool IsShutdownComplete => shutdownComplete;
    internal bool UsesContent(CompiledProjectContent content)
    {
        foreach (var request in requests.Values)
            if (ReferenceEquals(request.Source?.Content, content)) return true;
        return false;
    }
    public Exception? ShutdownDiagnostic { get; private set; }
    private readonly SpriteBatch batch;
    private readonly CustomAssetThumbnail customThumbnail = new();
    private readonly Dictionary<AssetId, Request> requests = [];
    private readonly Dictionary<AssetId, AssetThumbnailStatus> buildStatuses = [];
    private readonly List<Task> retiredReads = [];
    private readonly ConcurrentQueue<AssetId?> sourceChanges = new();
    private sealed record WatchedSources(ThumbnailSourceWatchPlan Plan, IReadOnlySet<AssetId> Globals);
    private WatchedSources watchedSources = new(new ThumbnailSourceWatchPlan(), new HashSet<AssetId>());
    private readonly ProjectWorkspace sourceWorkspace;
    private IReadOnlyDictionary<AssetId, (Asset Asset, string Path)>? sourceAuthored;
    private int recoverWatchers;
    private readonly List<FileSystemWatcher> watchers = [];
    private readonly CancellationTokenSource cancellation = new();
    private readonly string directory;
    private readonly PackageSession session;
    private long sequence, frame;
    private int gpuCount;
    private long statusEpoch;
    public const int MaximumGpuThumbnails = 128;

    public AssetThumbnailService(Game game, ImGuiRenderer renderer, ProjectWorkspace workspace,
        Func<AssetEntry, AssetThumbnailSource> source, Action<AssetEntry, Exception> reportError, Action<AssetId?> invalidateSource,
        Action<ProjectLoadState> nativeProgress)
    {
        this.game = game; device = game.GraphicsDevice; this.renderer = renderer; this.source = source; this.reportError = reportError; this.invalidateSource = invalidateSource;
        this.nativeProgress = nativeProgress;
        nativeQueue = new NativeThumbnailResidentQueue(state => { if (!disposed) this.nativeProgress(state); });
        Stride.Assets.Thumbnails.AssetThumbnailRules.EnsureRegistered();
        sourceWorkspace = workspace;
        session = workspace.Session; batch = new ThumbnailBatch(game.GraphicsDevice);
        providers.Add(typeof(TextureAsset), new Provider(typeof(TextureAsset), -10000, data =>
            TextureAssetThumbnail.Generate(game, renderer, batch, data.RequireCompiledContent(), data.Url, (TextureAsset)data.Asset, data.ColorSpace)));
        providers.Add(typeof(ModelAsset), new Provider(typeof(ModelAsset), 10000, data => ModelAssetThumbnail.Generate(game, renderer, data)));
        providers.Add(typeof(ProceduralModelAsset), new Provider(typeof(ProceduralModelAsset), 0, data => ModelAssetThumbnail.Generate(game, renderer, data)));
        providers.Add(typeof(MaterialAsset), new Provider(typeof(MaterialAsset), -5000, data => ModelAssetThumbnail.Generate(game, renderer, data)));
        providers.Add(typeof(SpriteSheetAsset), new Provider(typeof(SpriteSheetAsset), 0, data => SpriteSheetAssetThumbnail.Generate(game, renderer, batch, data)));
        providers.Add(typeof(SpriteFontAsset), new Provider(typeof(SpriteFontAsset), 0, data => SpriteFontAssetThumbnail.Generate(game, renderer, batch, data)));
        providers.Add(typeof(PrecompiledSpriteFontAsset), new Provider(typeof(PrecompiledSpriteFontAsset), 0, data => SpriteFontAssetThumbnail.Generate(game, renderer, batch, data)));
        providers.Add(typeof(PrefabModelAsset), new Provider(typeof(PrefabModelAsset), 0, data => ModelAssetThumbnail.Generate(game, renderer, data)));
        providers.Add(typeof(HeightmapAsset), new Provider(typeof(HeightmapAsset), 10050, data => HeightmapAssetThumbnail.Generate(game, renderer, batch, data)));
        providers.Add(typeof(PrefabAsset), new Provider(typeof(PrefabAsset), 10000, data => EntityAssetThumbnail.Generate(game, renderer, batch, data)));
        providers.Add(typeof(UIPageAsset), new Provider(typeof(UIPageAsset), 11000, data => EntityAssetThumbnail.Generate(game, renderer, batch, data)));
        providers.Add(typeof(SpriteStudioModelAsset), new Provider(typeof(SpriteStudioModelAsset), 0, data => EntityAssetThumbnail.Generate(game, renderer, batch, data)));
        providers.Add(typeof(AnimationAsset), new Provider(typeof(AnimationAsset), 12000, data => data.StaticIcon != null
            ? StaticAssetThumbnail.Generate(game, renderer, data) : EntityAssetThumbnail.Generate(game, renderer, batch, data)));
        foreach (var type in StaticAssetThumbnailIcons.AssetTypes)
            providers.Add(type, new Provider(type, 0, data => StaticAssetThumbnail.Generate(game, renderer, data), RequiresCompilation: false));
        directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Stride", "Editor.ImGui", "thumbnails", "v1");
        session.AssetDirtyChanged += OnAssetDirtyChanged;
        AssemblyRegistry.AssemblyRegistered += OnNativeAssemblyChanged;
        AssemblyRegistry.AssemblyUnregistered += OnNativeAssemblyChanged;
        RefreshWatchedSources(workspace);
    }
    public void RefreshWatchedSources(ProjectWorkspace workspace, IReadOnlyDictionary<AssetId, (Asset Asset, string Path)>? authored = null)
    {
        sourceAuthored = authored;
        foreach (var watcher in watchers) watcher.Dispose();
        watchers.Clear();
        var plan = new ThumbnailSourceWatchPlan();
        var globals = new HashSet<AssetId>();
        foreach (var item in workspace.AssetItems)
        {
            if (item.Asset is GameSettingsAsset or EffectShaderAsset) globals.Add(item.Id);
            var assetPath = item.FullPath.ToOSPath(); var asset = item.Asset;
            if (authored != null && authored.TryGetValue(item.Id, out var retained)) { asset = retained.Asset; assetPath = retained.Path; }
            Watch(assetPath, item.Id);
            try
            {
                foreach (var path in new SourceFilesCollector().GetSourceFiles(asset).Keys)
                    if (!string.IsNullOrEmpty(path)) Watch(Path.GetFullPath(path.ToOSPath(), Path.GetDirectoryName(assetPath)!), item.Id);
            }
            catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException) { }
            if (asset is SpriteFontAsset or PrecompiledSpriteFontAsset)
                try { foreach (var source in FontSourcePaths.Files(asset, assetPath)) Watch(source, item.Id); }
                catch (Exception error) when (error is IOException or ArgumentException) { }
            if (asset is SpriteSheetAsset sheet)
                foreach (var sprite in sheet.Sprites)
                    if (!string.IsNullOrEmpty(sprite.Source))
                        try { Watch(Path.GetFullPath(sprite.Source, Path.GetDirectoryName(assetPath)!), item.Id); }
                        catch (ArgumentException) { }
            if (asset is SpriteStudioModelAsset spriteStudio)
                try { foreach (var source in SpriteStudioThumbnailSources.Files(spriteStudio, assetPath)) Watch(source, item.Id); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or System.Xml.XmlException) { }
            if (asset is IAssetWithSource assetWithSource && !string.IsNullOrEmpty(assetWithSource.Source))
            {
                try
                {
                    var sourcePath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(assetPath)!, assetWithSource.Source.ToOSPath()));
                    Watch(sourcePath, item.Id);
                }
                catch (ArgumentException) { }
            }
        }
        Volatile.Write(ref watchedSources, new(plan, globals));
        foreach (var directory in plan.Directories)
        {
            try
            {
                var watcher = new FileSystemWatcher(directory.Key) { IncludeSubdirectories = directory.Value,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size };
                watcher.Changed += OnFileChanged; watcher.Created += OnFileChanged; watcher.Deleted += OnFileChanged;
                watcher.Renamed += OnFileRenamed; watcher.Error += OnWatcherError; watcher.EnableRaisingEvents = true; watchers.Add(watcher);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { } // Missing/unavailable directories remain detectable by compiler errors.
        }
        void Watch(string path, AssetId id)
        {
            plan.Add(path, id);
        }
    }
    public void BeginFrame(long currentFrame)
    {
        if (disposed) return;
        frame = currentFrame;
        if (Interlocked.Exchange(ref recoverWatchers, 0) != 0 && !disposed) RefreshWatchedSources(sourceWorkspace, sourceAuthored);
        for (var index = retiredReads.Count - 1; index >= 0; --index)
            if (retiredReads[index].IsCompleted) { if (retiredReads[index].IsFaulted) _ = retiredReads[index].Exception; retiredReads.RemoveAt(index); }
        while (sourceChanges.TryDequeue(out var id)) Invalidate(id, true);
    }
    internal object InspectCaptureState() => new
    {
        frame, disposed, gpuCount,
        requestCount = requests.Count,
        requests = requests.Values.OrderByDescending(request => request.VisibleFrame).ThenBy(request => request.Asset.Name).Take(32)
            .Select(request => new
            {
                assetId = request.Asset.Id.ToString(), request.Asset.Name, request.Asset.Path,
                url = request.Source?.Url, sourceIdentity = request.Source?.NativeCompilation?.Identity,
                request.VisibleFrame, request.ValidatedFrame, visibleThisFrame = request.VisibleFrame == frame,
                request.Invalid, request.Failed, request.Failure, request.RetryFrame, request.BlockedIdentity,
                request.Key, request.ReadKey, request.ReadNativeIdentity,
                readStatus = request.CacheRead?.Status.ToString(),
                completedPngBytes = request.CacheRead?.IsCompletedSuccessfully == true ? request.CacheRead.Result?.Length : null,
                fromDisk = request.NativeRead?.FromDisk,
                adopted = request.Thumbnail != null,
                nativeCompiler = request.Provider.NativeCompiler?.FullName,
            }).ToArray(),
    };
    public bool RequestVisible(AssetEntry asset, out ImTextureRef reference)
    {
        reference = default;
        if (disposed) return false;
        var providerType = RenderedProviderType(asset.AssetType) ?? asset.AssetType;
        var native = GetNativeCompiler(asset.AssetType);
        Provider? provider;
        if (native != null)
        {
            if (!nativeProviders.TryGetValue(asset.AssetType, out var registered) || !ReferenceEquals(registered.Compiler, native))
            {
                provider = new Provider(asset.AssetType, native.Priority,
                    _ => throw new InvalidOperationException("A registered native thumbnail executes in the isolated compiler worker."),
                    !native.IsStatic, native.GetType());
                nativeProviders[asset.AssetType] = (native, provider);
            }
            else provider = registered.Provider;
        }
        else if (!providers.TryGetValue(providerType, out provider))
            providers.Add(asset.AssetType, provider = new Provider(asset.AssetType, 0,
                data => customThumbnail.Generate(game, renderer, batch, data), RequiresCompilation: false));
        if (!requests.TryGetValue(asset.Id, out var request)) requests.Add(asset.Id, request = new Request(asset, provider) { Sequence = ++sequence });
        if (!ReferenceEquals(request.Provider, provider))
        {
            Retire(request); ForgetRead(request); request.Provider = provider;
            request.Key = request.BlockedIdentity = null; request.Invalid = true; request.Failed = false;
        }
        request.Asset = asset; request.VisibleFrame = frame;
        if (request.Invalid)
        {
            request.Invalid = false; request.ValidatedFrame = frame;
            try
            {
                var snapshot = source(asset);
                request.StatusEpoch = -1;
                var key = snapshot.CacheKey;
                if (request.BlockedIdentity != null && request.BlockedIdentity == snapshot.CompiledIdentity)
                { Retire(request); return false; }
                request.BlockedIdentity = null;
                // A new bundle can contain identical content. Keep its live owner even
                // when the content key and existing thumbnail remain unchanged.
                var artifacts = snapshot.NativeCompilation?.AcquireArtifactLease();
                try { request.SourceArtifacts?.Dispose(); }
                catch { artifacts?.Dispose(); throw; }
                request.SourceArtifacts = artifacts; request.Source = snapshot;
                request.Failed = false; request.Failure = AssetThumbnailFailure.None;
                if (request.Key != key)
                {
                    Retire(request); request.Key = key;
                    ForgetRead(request); request.Failed = false; request.Failure = AssetThumbnailFailure.None;
                }
            }
            catch (Exception error)
            {
                if (!request.Failed && error is not AssetThumbnailNotCompiledException) reportError(asset, error);
                request.Failed = true; request.Failure = ClassifyFailure(asset, error); request.StatusEpoch = -1; Retire(request);
            }
        }
        if (request.Thumbnail == null || request.BlockedIdentity != null) return false;
        reference = request.Thumbnail.Reference; return true;
    }
    public void RenderPending(long currentFrame)
    {
        if (disposed) return;
        if (!ReferenceEquals(device, game.GraphicsDevice)) { BeginShutdown(); return; }
        BeginFrame(currentFrame);
        Request? next = null, firstRead = null, secondRead = null;
        var reading = 0; var nativeReading = 0;
        var nativeReady = new List<Request>();
        foreach (var request in requests.Values)
        {
            if (request.CacheRead is { IsCompleted: false })
            { if (request.Provider.NativeCompiler != null) nativeReading++; else reading++; }
            if (request.Invalid || request.VisibleFrame != frame || request.Thumbnail != null || request.Failed || request.RetryFrame > frame || request.BlockedIdentity != null || request.Source == null) continue;
            if (request.CacheRead == null)
            {
                if (request.Provider.NativeCompiler != null) { nativeReady.Add(request); continue; }
                if (firstRead == null || ComesFirst(request, firstRead)) { secondRead = firstRead; firstRead = request; }
                else if (secondRead == null || ComesFirst(request, secondRead)) secondRead = request;
            }
            else if (request.CacheRead.IsCompleted && (next == null || ComesFirst(request, next))) next = request;
        }
        if (reading < 2 && firstRead != null) { StartCacheRead(firstRead); reading++; }
        if (reading < 2 && secondRead != null) StartCacheRead(secondRead);
        nativeReady.Sort((left, right) => left.Provider.Priority != right.Provider.Priority
            ? left.Provider.Priority.CompareTo(right.Provider.Priority) : left.Sequence.CompareTo(right.Sequence));
        foreach (var request in nativeReady)
        { if (nativeReading >= 16) break; StartCacheRead(request); nativeReading++; }
        if (next == null) return;
        if (!StringComparer.Ordinal.Equals(next.ReadKey, next.Key)
            || next.Provider.NativeCompiler != null && !StringComparer.Ordinal.Equals(next.ReadNativeIdentity, next.Source?.NativeCompilation?.Identity))
        { ForgetRead(next); next.Invalid = true; return; }
        try
        {
            var png = next.CacheRead!.GetAwaiter().GetResult();
            if (png != null)
            {
                try { next.Thumbnail = AssetThumbnailImage.LoadCached(game, renderer, png); }
                catch (Exception error)
                {
                    EditorGraphicsFailures.ThrowIfDeviceLost(game.GraphicsDevice, error);
                    if (next.Provider.NativeCompiler != null)
                    {
                        if (next.NativeRead?.FromDisk != true) throw;
                        next.SkipPersistentCache = true; ForgetRead(next); return;
                    }
                    png = null; // A truncated/corrupt persistent entry is regenerated from actual content.
                }
            }
            if (png == null)
            {
                var data = next.Source!;
                var generated = next.Provider.Generate(data);
                next.Thumbnail = generated.Thumbnail; Store(next.Key!, generated.Png);
            }
            next.CacheRead = null; next.NativeCancellation?.Dispose(); next.NativeCancellation = null;
            gpuCount++; TrimGpuCache();
            next.Failure = AssetThumbnailFailure.None;
            next.StatusEpoch = -1;
        }
        catch (Exception error)
        {
            if (error is NativeThumbnailWorkerDeviceException or NativeThumbnailWorkerUnavailableException)
            {
                // A lost independent thumbnail device retires that generation;
                // it cannot mark a valid user asset broken or persist bad pixels.
                ForgetRead(next); next.Invalid = true; next.Failed = false;
                next.Failure = AssetThumbnailFailure.None; next.RetryFrame = frame + 120;
                reportError(next.Asset, error); return;
            }
            try { EditorGraphicsFailures.ThrowIfDeviceLost(game.GraphicsDevice, error); }
            catch
            {
                ForgetRead(next); next.Invalid = true;
                // A removed GPU must not poison a valid asset's thumbnail/status cache.
                throw;
            }
            next.Failed = true; next.Failure = ClassifyFailure(next.Asset, error); next.StatusEpoch = -1; reportError(next.Asset, error);
        }
        finally { next.Sequence = ++sequence; }
    }
    private void StartCacheRead(Request request)
    {
        request.ReadKey = request.Key; request.ReadNativeIdentity = request.Source?.NativeCompilation?.Identity;
        var path = Path.Combine(directory, request.Key + ".png"); var token = cancellation.Token;
        if (request.Provider.NativeCompiler == null)
        {
            request.CacheRead = Task.Run(() => File.Exists(path) && new FileInfo(path).Length <= 1024 * 1024 ? File.ReadAllBytes(path) : null, token);
            return;
        }
        var capture = request.Source!.NativeCompilation ?? throw new InvalidDataException("The native thumbnail request has no certified authored snapshot.");
        request.NativeCancellation = CancellationTokenSource.CreateLinkedTokenSource(token); token = request.NativeCancellation.Token;
        var generation = Interlocked.Increment(ref request.ReadGeneration);
        var nativeRead = new NativeReadState(); request.NativeRead = nativeRead;
        var skipPersistentCache = request.SkipPersistentCache;
        var priority = request.Provider.Priority;
        var artifacts = capture.AcquireArtifactLease();
        request.CacheRead = Task.Run<byte[]?>(async () =>
        {
            using var retainedArtifacts = artifacts;
            token.ThrowIfCancellationRequested();
            if (!skipPersistentCache && NativeThumbnailCacheEntry.TryRead(directory, capture.Identity, out var cached, out var cachedStatus))
            { nativeRead.FromDisk = true; nativeRead.Level = (int)cachedStatus.Level; return cached; }
            var result = await nativeQueue.Submit(capture, priority, token).ConfigureAwait(false);
            if (result.Result == ThumbnailBuildResult.Cancelled) throw new OperationCanceledException(token);
            if (result.Result != ThumbnailBuildResult.Succeeded || result.Png == null)
                throw new InvalidDataException(result.Error ?? "The native thumbnail build failed.");
            var png = result.Png;
            if (Volatile.Read(ref request.ReadGeneration) == generation) Volatile.Write(ref nativeRead.Level, (int)result.Level);
            token.ThrowIfCancellationRequested();
            NativeThumbnailCacheEntry.Store(directory, capture.Identity, png,
                new((LogMessageType)Volatile.Read(ref nativeRead.Level), AssetThumbnailFailure.None), token);
            TrimPersistentCache(token);
            return png;
        });
    }
    public IThumbnailCompiler? GetNativeCompiler(Type assetType)
    {
        var compiler = nativeRegistry.GetCompiler(assetType, typeof(ThumbnailCompilationContext)) as IThumbnailCompiler;
        return compiler is CustomAssetThumbnailCompiler ? null : compiler;
    }
    private void OnNativeAssemblyChanged(object? sender, AssemblyRegisteredEventArgs args)
    {
        if (args.Categories.Contains(AssemblyCommonCategories.Assets)) sourceChanges.Enqueue(null);
    }
    private static bool ComesFirst(Request candidate, Request current) => candidate.Provider.Priority < current.Provider.Priority
        || candidate.Provider.Priority == current.Provider.Priority && candidate.Sequence < current.Sequence;
    private static AssetThumbnailFailure ClassifyFailure(AssetEntry asset, Exception error) => error switch
    {
        AssetThumbnailNotCompiledException => AssetThumbnailFailure.AwaitingCompilation,
        NotSupportedException or NotImplementedException => AssetThumbnailFailure.Unsupported,
        AssetSourceUnavailableException when asset.AssetType == typeof(TextureAsset) => AssetThumbnailFailure.MissingSource,
        _ => AssetThumbnailFailure.Broken,
    };
    public void SetBuildStatus(AssetId id, AssetThumbnailStatus status)
    {
        if (buildStatuses.GetValueOrDefault(id, AssetThumbnailStatus.Clear) == status) return;
        if (status.Level < LogMessageType.Warning && status.Failure == AssetThumbnailFailure.None) buildStatuses.Remove(id);
        else buildStatuses[id] = status;
        statusEpoch++;
    }
    public AssetThumbnailStatus GetStatus(AssetId id)
    {
        var status = buildStatuses.GetValueOrDefault(id, AssetThumbnailStatus.Clear);
        if (!requests.TryGetValue(id, out var request)) return status;
        var nativeLevel = request.NativeRead is { } read ? (LogMessageType)Volatile.Read(ref read.Level) : LogMessageType.Info;
        if (nativeLevel > status.Level) status = new(nativeLevel, AssetThumbnailFailure.None);
        if (request.StatusEpoch == statusEpoch && request.StatusNativeLevel == nativeLevel) return request.Status;
        request.StatusEpoch = statusEpoch;
        request.StatusNativeLevel = nativeLevel;
        if (request.Failure is AssetThumbnailFailure.Broken or AssetThumbnailFailure.MissingSource)
            return request.Status = new(LogMessageType.Error, request.Failure);
        if ((request.Failure is AssetThumbnailFailure.Unsupported or AssetThumbnailFailure.AwaitingCompilation)
            && status.Level < LogMessageType.Warning) return request.Status = new(LogMessageType.Info, request.Failure);
        if (request.Source != null)
            for (var index = 0; index < request.Source.DependencyIds.Count; index++)
                if (buildStatuses.TryGetValue(request.Source.DependencyIds[index], out var dependent) && dependent.Level > status.Level)
                    status = new(dependent.Level, AssetThumbnailFailure.None);
        if (request.Thumbnail == null && status.Level >= LogMessageType.Error && status.Failure == AssetThumbnailFailure.None)
            status = status with { Failure = AssetThumbnailFailure.Broken };
        return request.Status = status;
    }
    public void Invalidate(AssetId? id = null, bool waitForCompilation = false)
    {
        invalidateSource(id);
        foreach (var pair in requests)
        {
            if (id != null && pair.Key != id && !DependsOn(pair.Value, id.Value)) continue;
            var request = pair.Value; request.Invalid = true; request.Failed = false;
            request.StatusEpoch = -1;
            var waitForContent = waitForCompilation && request.Provider.RequiresCompilation
                && request.Provider.NativeCompiler == null && request.Source?.StaticIcon == null;
            request.BlockedIdentity = waitForContent ? request.Source?.CompiledIdentity : null;
            if (waitForContent) { Retire(request); request.Key = null; ForgetRead(request); }
        }
        static bool DependsOn(Request request, AssetId assetId) => request.Source != null && request.Source.DependencyIds.Contains(assetId);
    }
    private void Retire(Request request)
    {
        if (request.Thumbnail == null) return;
        request.Thumbnail.Dispose(); request.Thumbnail = null; gpuCount--;
        request.StatusEpoch = -1;
    }
    private void ForgetRead(Request request)
    {
        Interlocked.Increment(ref request.ReadGeneration);
        request.NativeRead = null;
        request.NativeCancellation?.Cancel(); request.NativeCancellation?.Dispose(); request.NativeCancellation = null;
        if (request.CacheRead != null)
        {
            if (!request.CacheRead.IsCompleted) retiredReads.Add(request.CacheRead);
            else if (request.CacheRead.IsFaulted) _ = request.CacheRead.Exception;
        }
        request.CacheRead = null;
        request.ReadKey = request.ReadNativeIdentity = null;
    }
    private void TrimGpuCache()
    {
        while (gpuCount > MaximumGpuThumbnails)
        {
            Request? oldest = null;
            foreach (var request in requests.Values)
                if (request.Thumbnail != null && (oldest == null || request.VisibleFrame < oldest.VisibleFrame)) oldest = request;
            if (oldest == null) break;
            Retire(oldest);
        }
        while (requests.Count > 2048)
        {
            Request? oldest = null;
            foreach (var request in requests.Values)
                if (request.VisibleFrame != frame && (oldest == null || request.VisibleFrame < oldest.VisibleFrame)) oldest = request;
            if (oldest == null) break;
            Retire(oldest); ForgetRead(oldest);
            oldest.SourceArtifacts?.Dispose(); oldest.SourceArtifacts = null; oldest.Source = null;
            requests.Remove(oldest.Asset.Id);
        }
    }
    private void Store(string key, byte[] png)
    {
        if (disposed) return;
        var token = cancellation.Token;
        retiredReads.Add(Task.Run(() =>
        {
            string? temporary = null;
            try
            {
                token.ThrowIfCancellationRequested(); Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, key + ".png"); temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllBytes(temporary, png); token.ThrowIfCancellationRequested(); File.Move(temporary, path, true);
                TrimPersistentCache(token);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or OperationCanceledException) { }
            finally { if (temporary != null && File.Exists(temporary)) try { File.Delete(temporary); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { } }
        }, token));
    }
    private void TrimPersistentCache(CancellationToken token)
    {
        try
        {
            var files = new DirectoryInfo(directory).EnumerateFiles("*.png").OrderByDescending(file => file.LastWriteTimeUtc).Skip(1024);
            foreach (var file in files)
            { token.ThrowIfCancellationRequested(); file.Delete(); NativeThumbnailCacheEntry.RemoveCompanion(file.FullName); }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
    private void OnAssetDirtyChanged(AssetItem item, bool before, bool after) => sourceChanges.Enqueue(item.Asset is GameSettingsAsset or EffectShaderAsset ? null : item.Id);
    private void OnFileChanged(object sender, FileSystemEventArgs args)
    {
        var watched = Volatile.Read(ref watchedSources);
        foreach (var id in watched.Plan.Affected(args.FullPath)) sourceChanges.Enqueue(watched.Globals.Contains(id) ? null : id);
    }
    private void OnFileRenamed(object sender, RenamedEventArgs args)
    {
        Interlocked.Exchange(ref recoverWatchers, 1);
        OnFileChanged(sender, args);
        var watched = Volatile.Read(ref watchedSources);
        foreach (var id in watched.Plan.Affected(args.OldFullPath)) sourceChanges.Enqueue(watched.Globals.Contains(id) ? null : id);
    }
    private void OnWatcherError(object sender, ErrorEventArgs args)
    {
        if (disposed) return;
        // Lost watched directories and overflow are genuine invalidation events.
        // Rebuild roots on the owner thread and fall back to surviving ancestors.
        Interlocked.Exchange(ref recoverWatchers, 1); sourceChanges.Enqueue(null);
    }
    public void Dispose()
        => BeginShutdown();
    public void BeginShutdown() => disposed = true;
    public bool PumpShutdown()
    {
        if (shutdownComplete) return true;
        BeginShutdown();
        if (!eventsReleased)
        {
            session.AssetDirtyChanged -= OnAssetDirtyChanged;
            AssemblyRegistry.AssemblyRegistered -= OnNativeAssemblyChanged;
            AssemblyRegistry.AssemblyUnregistered -= OnNativeAssemblyChanged;
            eventsReleased = true;
        }
        while (watchers.Count != 0) { watchers[^1].Dispose(); watchers.RemoveAt(watchers.Count - 1); }
        if (!cancellationReleased) cancellation.Cancel();
        foreach (var request in requests.Values)
        {
            Retire(request);
            ForgetRead(request);
        }
        nativeShutdown ??= nativeQueue.DisposeAsync().AsTask();
        if (!nativeShutdown.IsCompleted) return false;
        if (nativeShutdown.IsFaulted || nativeShutdown.IsCanceled)
        {
            var failed = nativeShutdown; nativeShutdown = null;
            failed.GetAwaiter().GetResult();
        }
        nativeShutdown.GetAwaiter().GetResult();
        // No provider/session/cache owner is released while a real job, disk
        // reader or writer can still use its immutable capture or factory.
        foreach (var task in retiredReads) if (!task.IsCompleted) return false;
        var failures = retiredReads.Where(task => task.IsFaulted).SelectMany(task => task.Exception!.InnerExceptions).ToArray();
        if (failures.Length != 0) ShutdownDiagnostic = new AggregateException("Thumbnail work failed while draining.", failures);
        retiredReads.Clear();
        if (!customReleased) { customThumbnail.Dispose(); customReleased = true; }
        if (!batchReleased)
        { var resources = new ThumbnailGpuResources(game); resources.Retain(batch); resources.Retire(); batchReleased = true; }
        if (!cancellationReleased) { cancellation.Dispose(); cancellationReleased = true; }
        nativeRegistry.Dispose(); nativeProviders.Clear();
        NativeThumbnailArtifactCleanup.DisposeAll(requests.Values.Select(request => request.SourceArtifacts).OfType<IDisposable>());
        foreach (var request in requests.Values) { request.SourceArtifacts = null; request.Source = null; }
        NativeThumbnailAssemblyArtifact.PumpPendingCleanup(requireComplete: true);
        requests.Clear(); buildStatuses.Clear(); providers.Clear(); sourceAuthored = null;
        shutdownComplete = true; return true;
    }

    // Exactly the non-static built-in WPF thumbnail compiler registrations.
    public static bool HasRenderedProvider(Type type) => RenderedProviderType(type) != null;
    // Native AssetCompilerRegistry inherits compilation contexts, not asset types.
    // Unregistered asset subclasses receive the real generic Asset fallback.
    private static Type? RenderedProviderType(Type type) => IsRenderedProvider(type) ? type : null;
    private static bool IsRenderedProvider(Type type) => type == typeof(TextureAsset) || type == typeof(ModelAsset)
        || type == typeof(ProceduralModelAsset) || type == typeof(MaterialAsset) || type == typeof(SpriteSheetAsset)
        || type == typeof(SpriteFontAsset) || type == typeof(PrecompiledSpriteFontAsset) || type == typeof(PrefabModelAsset)
        || type == typeof(HeightmapAsset) || type == typeof(PrefabAsset) || type == typeof(UIPageAsset)
        || type == typeof(SpriteStudioModelAsset) || type == typeof(AnimationAsset);
}
