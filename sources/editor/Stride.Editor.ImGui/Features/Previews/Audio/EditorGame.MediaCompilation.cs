using Stride.Audio.Assets;
using System.Security.Cryptography;
using System.Text;
using Stride.Assets;
using Stride.Assets.Effect;
using Stride.Assets.Media;
using Stride.Assets.Models;
using Stride.Assets.Materials;
using Stride.Assets.Textures;
using Stride.Core;
using Stride.Core.Assets;
using Stride.Core.Assets.Analysis;
using Stride.Core.Assets.Tracking;
using Stride.Core.Diagnostics;
using Stride.Core.IO;
using Stride.Core.Serialization;
using Stride.Core.Serialization.Contents;
using Stride.Graphics;
using Stride.Rendering.Materials;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private static string[] CapturedPackageDependencies(Package package) => package.Container?.FlattenedDependencies
        .Where(dependency => dependency.Package?.FullPath != null).Select(dependency => dependency.Package.FullPath.ToOSPath())
        .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray() ?? [];
    private AssetEntry? requestedMediaPreview;
    private ProjectWorkspace? mediaPreviewWorkspace;
    private Task<string>? mediaPreviewBuild;
    private CancellationTokenSource? mediaPreviewCancellation;
    private MediaCompilationSnapshot? compilingMediaSnapshot;
    private string? installedMediaRevision, attemptedMediaRevision, pendingMediaRevision;
    private DateTime mediaPreviewCheckTime, mediaPreviewDebounce;
    private int mediaPreviewBuildLevel;
    private int detachedMediaBuilds;
    private string? mediaPreviewCaptureError;
    private readonly Dictionary<string, (long Length, long Modified, string Hash)> authoredPreviewSourceHashes = new(StringComparer.OrdinalIgnoreCase);
    private readonly AuthoredImportPollingCache mediaPollingCache = new();
    private bool IsMediaPreviewUpdating => mediaPreviewBuild != null || Volatile.Read(ref detachedMediaBuilds) != 0;

    private MediaCompilationSnapshot CaptureMediaPreview(AssetEntry entry, bool validateSourceContent = false)
    {
        if (workspace!.FindAsset(entry.Id).Asset is not (AnimationAsset or SoundAsset))
            throw new InvalidOperationException("The requested asset is not animation or sound.");
        return CaptureAuthoredPreviewCompilation(entry, validateSourceContent);
    }
    private MediaCompilationSnapshot CaptureAuthoredPreviewCompilation(AssetEntry entry, bool validateSourceContent = false,
        IReadOnlyCollection<AssetId>? additionalRoots = null, bool excludeCompositorLayout = false)
    {
        var current = workspace!; var root = current.FindAsset(entry.Id);
        Asset Actual(AssetItem item) => authoring?.Find(item.Id)?.Document is IAssetTagDocument owner ? owner.AuthoredAsset : item.Asset;
        var rootAsset = Actual(root);
        var settings = root.Package.GetGameSettingsAssetOrDefault();
        var settingsItem = current.Session.FindAsset(settings.Id);
        if (settingsItem != null) settings = (GameSettingsAsset)Actual(settingsItem);
        var rendering = settings.GetOrDefault<RenderingSettings>();
        var parameters = new MediaPreviewBuildParameters
        {
            RootId = root.Id.ToString(), Platform = PlatformType.Windows, GraphicsPlatform = Stride.Graphics.GraphicsDevice.Platform,
            GameSettingsId = settingsItem?.Id.ToString(),
            AssemblyPaths = current.Session.Packages.SelectMany(package => package.LoadedAssemblies)
                .Where(owner => owner.Assembly != null && !string.IsNullOrEmpty(owner.Path))
                .Select(owner => Path.GetFullPath(owner.Path)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray(),
            GraphicsProfile = rendering.DefaultGraphicsProfile, ColorSpace = rendering.ColorSpace,
            TextureQuality = settings.GetOrDefault<TextureSettings>().TextureQuality,
        };
        string? modelUrl = null;
        AssetItem? previewModel = null;
        if (rootAsset is AnimationAsset animation)
        {
            // Match the native preview rule: actual package/dependency ID first,
            // then location for imported references whose ID has changed.
            var model = root.Package.FindAssetFromProxyObject(animation.PreviewModel);
            if (model != null && Actual(model) is ModelAsset { Skeleton: not null })
            { previewModel = model; modelUrl = model.Location.ToString(); }
        }
        var clipUrl = root.Location.ToString();
        if (rootAsset is AnimationAsset { Type: DifferenceAnimationAssetType }) clipUrl += AnimationAssetCompiler.SrcClipSuffix;
        var snapshots = new List<MediaAssetSnapshot>(); var queue = new Queue<AssetItem>(); var visited = new HashSet<AssetId>();
        queue.Enqueue(root);
        if (previewModel != null) queue.Enqueue(previewModel);
        if (additionalRoots != null)
            foreach (var identity in additionalRoots) queue.Enqueue(current.Session.FindAsset(identity)
                ?? throw new InvalidDataException("An actual additional compiler root is absent: " + identity));
        var materialDependenciesAdded = false;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Stamp(string text) => hash.AppendData(Encoding.UTF8.GetBytes(text));
        byte[] Bytes(AssetItem item, Asset asset)
        {
            var path = item.FullPath.ToOSPath();
            if (asset is SourceCodeAsset source)
            {
                var retained = (authoring?.Find(item.Id)?.Document ?? authoring?.Find(path)?.Document) as ScriptSourceDocument;
                return Encoding.UTF8.GetBytes(retained?.EditorText ?? source.Text);
            }
            if (authoring?.Find(item.Id)?.Document is AssetMetadataDocument metadata) return metadata.CaptureCompilationAsset(item, excludeCompositorLayout);
            if (authoring?.Find(item.Id)?.Document is EntityHierarchyDocument hierarchy) return hierarchy.CaptureCompilationAsset(item);
            if (excludeCompositorLayout && asset is Stride.Assets.Rendering.GraphicsCompositorAsset)
                return CompositorCompilationSnapshot.Capture(asset, item.YamlMetadata, item.Package.Container?.AssetNamespace);
            using var stream = new MemoryStream();
            AssetFileSerializer.Save(stream, AssetCloner.Clone(asset), item.YamlMetadata, assetNamespace: item.Package.Container?.AssetNamespace);
            return stream.ToArray();
        }
        void Source(string path, bool required)
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                if (required) throw new FileNotFoundException("The actual asset preview source is missing.", path);
                Stamp(path + "|missing"); return;
            }
            if (validateSourceContent || !authoredPreviewSourceHashes.TryGetValue(path, out var previous)
                || previous.Length != info.Length || previous.Modified != info.LastWriteTimeUtc.Ticks)
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                authoredPreviewSourceHashes[path] = (info.Length, info.LastWriteTimeUtc.Ticks, Convert.ToHexString(SHA256.HashData(stream)));
            }
            Stamp(path + "|" + authoredPreviewSourceHashes[path].Hash);
        }
        foreach (var assembly in parameters.AssemblyPaths) Source(assembly, true);
        while (queue.TryDequeue(out var item))
        {
            if (!visited.Add(item.Id)) continue;
            var asset = Actual(item); var path = item.FullPath.ToOSPath();
            if (asset is MaterialAsset && !materialDependenciesAdded)
            {
                materialDependenciesAdded = true;
                // MaterialAssetCompiler explicitly consumes the two actual DFG LUTs.
                foreach (var profile in new[] { GraphicsProfile.Level_9_1, GraphicsProfile.Level_10_0 })
                {
                    var reference = AttachedReferenceManager.GetAttachedReference(MaterialSpecularMicrofacetEnvironmentGGXLUT.CreateLookupTableReference(profile));
                    queue.Enqueue(current.Session.FindAsset(reference.Id)
                        ?? throw new InvalidDataException("An actual material preview lookup texture is absent: " + reference.Id));
                }
                foreach (var shader in current.Session.Packages.SelectMany(package => package.Assets).Where(candidate => candidate.Asset is EffectShaderAsset)) queue.Enqueue(shader);
            }
            var bytes = Bytes(item, asset);
            Stamp(item.Id + "|" + item.Location + "|" + path + "|" + item.Package.FullPath + "|" + item.Package.Container?.AssetNamespace
                + "|" + string.Join(";", CapturedPackageDependencies(item.Package))); hash.AppendData(bytes);
            var collector = new SourceFilesCollector(); var requiredSources = collector.GetCompilationInputFiles(asset);
            foreach (var source in collector.GetSourceFiles(asset).Keys)
                Source(Path.GetFullPath(source.ToOSPath(), Path.GetDirectoryName(path)!), requiredSources.Contains(source));
            if (asset.MainSource is { } main && !string.IsNullOrEmpty(main)) Source(Path.GetFullPath(main.ToOSPath(), Path.GetDirectoryName(path)!), true);
            if (asset is Stride.Assets.SpriteFont.SpriteFontAsset or Stride.Assets.SpriteFont.PrecompiledSpriteFontAsset)
                foreach (var fontSource in Stride.Assets.SpriteFont.FontSourcePaths.Files(asset, path)) Source(fontSource, true);
            if (asset is AnimationAsset { Type: DifferenceAnimationAssetType difference } && !string.IsNullOrEmpty(difference.BaseSource))
                Source(Path.GetFullPath(difference.BaseSource.ToOSPath(), Path.GetDirectoryName(path)!), true);
            foreach (var link in AssetReferenceAnalysis.Visit(asset))
                if (link.Reference is IReference reference && (reference.Id != AssetId.Empty || !string.IsNullOrEmpty(reference.Location)))
                {
                    var dependency = item.Package.FindAsset(reference)
                        ?? throw new InvalidDataException("An actual media preview dependency is absent: " + reference.Id + " (" + reference.Location + ")");
                    queue.Enqueue(dependency);
                }
            snapshots.Add(new(new MediaPreviewAssetInput { Id = item.Id.ToString(), Url = item.Location.ToString(), LocalUrl = item.UnqualifiedUrl.ToString(),
                SourcePackagePath = item.Package.FullPath.ToOSPath(), PackageDependencies = CapturedPackageDependencies(item.Package),
                AssetPath = path, AssetNamespace = item.Package.Container?.AssetNamespace },
                bytes, AssetRegistry.GetDefaultExtension(asset.GetType()) ?? throw new InvalidDataException("The media snapshot type has no asset extension.")));
        }
        if (settingsItem != null && !visited.Contains(settingsItem.Id))
        {
            var bytes = Bytes(settingsItem, settings); hash.AppendData(bytes); Stamp(settingsItem.Id.ToString());
            snapshots.Add(new(new MediaPreviewAssetInput { Id = settingsItem.Id.ToString(), Url = settingsItem.Location.ToString(),
                LocalUrl = settingsItem.UnqualifiedUrl.ToString(), SourcePackagePath = settingsItem.Package.FullPath.ToOSPath(),
                PackageDependencies = CapturedPackageDependencies(settingsItem.Package),
                AssetPath = settingsItem.FullPath.ToOSPath(), AssetNamespace = settingsItem.Package.Container?.AssetNamespace },
                bytes, AssetRegistry.GetDefaultExtension(settings.GetType())!));
        }
        Stamp(parameters.GraphicsPlatform + "|" + parameters.GraphicsProfile + "|" + parameters.ColorSpace + "|" + parameters.TextureQuality);
        var hdr = settings.GetOrDefault<EditorSettings>().RenderingMode == RenderingMode.HDR; Stamp(hdr.ToString());
        return new(parameters, snapshots.ToArray(), Convert.ToHexString(hash.GetHashAndReset()), clipUrl, modelUrl, hdr);
    }

    private void PumpAnimatedMediaPreviews()
    {
        if (projectContent == null)
        {
            // A native CPU-only session retains authoring, not an installed runtime bundle.
            if (mediaPreviewBuild != null || animationPreview != null || soundPreview != null) StopAnimatedMediaPreviews();
            return;
        }
        soundPreview?.Pump();
        if (soundPreview?.PlaybackError is { } playbackError && !ReferenceEquals(reportedSoundPreviewError, playbackError))
        { reportedSoundPreviewError = playbackError; RecordAssetPreviewRenderFailure("Sound preview", playbackError); statusMessage = playbackError.Message; }
        if (!ReferenceEquals(mediaPreviewWorkspace, workspace))
        {
            StopAnimatedMediaPreviews(); mediaPreviewWorkspace = workspace;
        }
        if (mediaPreviewBuild is { IsCompleted: true } completed)
        {
            mediaPreviewBuild = null; var cancelled = mediaPreviewCancellation!.IsCancellationRequested;
            mediaPreviewCancellation.Dispose(); mediaPreviewCancellation = null;
            var snapshot = compilingMediaSnapshot!; compilingMediaSnapshot = null; string? output = null;
            if (cancelled && attemptedMediaRevision == snapshot.Revision) attemptedMediaRevision = null;
            try
            {
                output = completed.GetAwaiter().GetResult();
                if (!cancelled && workspace != null && projectContent != null && requestedMediaPreview is { } current
                    && current.Id.ToString() == snapshot.Parameters.RootId && CaptureMediaPreview(current, true).Revision == snapshot.Revision)
                {
                    InstallMediaPreview(current, snapshot, output); output = null;
                    installedMediaRevision = snapshot.Revision;
                    SetAssetThumbnailBuildStatus(current.Id, new((LogMessageType)Volatile.Read(ref mediaPreviewBuildLevel), AssetThumbnailFailure.None));
                    InvalidateAssetThumbnails(current.Id);
                }
            }
            catch (OperationCanceledException) when (cancelled) { }
            catch (Exception error)
            {
                if (!cancelled && requestedMediaPreview is { } current)
                    RecordAssetPreviewBuildFailure(current.Id, error, AssetThumbnailFailure.Broken);
            }
            finally { if (output != null) CompiledMediaBuild.ReleaseOutput(output); }
        }
        if (workspace == null || requestedMediaPreview == null || DateTime.UtcNow < mediaPreviewCheckTime) return;
        var publishedEntry = workspace.Assets.FirstOrDefault(entry => entry.Id == requestedMediaPreview.Id);
        if (publishedEntry == null) { CloseAnimatedMediaPreviews(); return; }
        requestedMediaPreview = publishedEntry;
        mediaPreviewCheckTime = DateTime.UtcNow.AddMilliseconds(250);
        MediaCompilationSnapshot capture;
        try
        {
            var inputs = AuthoredImportPollingCache.CaptureInputs(workspace, authoring);
            capture = mediaPollingCache.Capture(workspace, authoring, requestedMediaPreview, inputs,
                _ => CaptureMediaPreview(requestedMediaPreview), includeInboundDependencies: false).Snapshot;
            mediaPreviewCaptureError = null;
        }
        catch (Exception error)
        {
            if (mediaPreviewCaptureError != error.ToString())
            { mediaPreviewCaptureError = error.ToString(); RecordAssetPreviewRenderFailure("Media preview", error); statusMessage = error.Message; }
            mediaPreviewCancellation?.Cancel(); return;
        }
        if (mediaPreviewBuild != null)
        {
            if (capture.Revision != compilingMediaSnapshot!.Revision) mediaPreviewCancellation!.Cancel();
            return;
        }
        if (capture.ModelUrl == null && workspace.FindAsset(requestedMediaPreview.Id).Asset is AnimationAsset)
        { CloseAnimationPreview(); installedMediaRevision = null; return; }
        if (capture.Revision == installedMediaRevision || capture.Revision == attemptedMediaRevision || !CanStartAuthoringPreviewUpdate) return;
        if (capture.Revision != pendingMediaRevision)
        { pendingMediaRevision = capture.Revision; mediaPreviewDebounce = DateTime.UtcNow.AddMilliseconds(180); return; }
        if (DateTime.UtcNow < mediaPreviewDebounce) return;
        capture = CaptureMediaPreview(requestedMediaPreview, true);
        attemptedMediaRevision = capture.Revision; compilingMediaSnapshot = capture; mediaPreviewBuildLevel = (int)LogMessageType.Info;
        mediaPreviewCancellation = new(); var token = mediaPreviewCancellation.Token;
        mediaPreviewBuild = Task.Run(() => CompiledMediaBuild.Build(capture, token,
            state => { if (!token.IsCancellationRequested || state.CompilerEvent != CompilerRunEvent.None) { AccumulatePreviewBuildLevel(ref mediaPreviewBuildLevel, state); RecordBuildOutput(state); } }), token);
    }
    private void InstallMediaPreview(AssetEntry entry, MediaCompilationSnapshot capture, string output)
    {
        var bundle = new CompiledProjectContent(this, output, () => CompiledMediaBuild.ReleaseOutput(output));
        try
        {
            if (workspace!.FindAsset(entry.Id).Asset is AnimationAsset)
            {
                var next = new AnimationAssetPreview(this, renderer!, bundle, capture.ModelUrl!, capture.ClipUrl,
                    capture.Hdr, capture.Parameters.ColorSpace, true);
                if (animationPreview != null)
                {
                    next.TimeScale = animationPreview.TimeScale; next.Loop = animationPreview.Loop;
                    next.Camera.RestoreState(animationPreview.Camera.CaptureState());
                    next.Seek(animationPreview.CurrentSeconds); if (!animationPreview.IsPlaying) next.Pause();
                    RetireEditorPreview(animationPreview);
                }
                animationPreview = next; reportedAnimationPreviewError = null;
            }
            else
            {
                var next = new SoundAssetPreview(this, bundle, capture.ClipUrl, true);
                if (soundPreview != null)
                {
                    next.Volume = soundPreview.Volume; next.Loop = soundPreview.Loop; next.Seek(soundPreview.Position.TotalSeconds);
                    if (soundPreview.IsPlaying) next.Play(); soundPreview.Stop(); RetireEditorPreview(soundPreview);
                }
                soundPreview = next; reportedSoundPreviewError = null;
            }
        }
        catch { bundle.Dispose(); throw; }
    }
    private void StopAnimatedMediaPreviews()
    {
        var build = mediaPreviewBuild; var cancellation = mediaPreviewCancellation;
        mediaPreviewBuild = null; mediaPreviewCancellation = null; cancellation?.Cancel();
        if (build == null) cancellation?.Dispose();
        else
        {
            // Compiler inputs are detached byte snapshots and paths. Closing the
            // project never waits for native cancellation or cache-directory IO.
            Interlocked.Increment(ref detachedMediaBuilds);
            _ = build.ContinueWith(completed =>
            {
                try
                {
                    if (completed.Status == TaskStatus.RanToCompletion) CompiledMediaBuild.ReleaseOutput(completed.Result);
                    else if (completed.IsFaulted) _ = completed.Exception;
                }
                catch (Exception error) { System.Diagnostics.Debug.WriteLine(error); }
                finally { cancellation?.Dispose(); Interlocked.Decrement(ref detachedMediaBuilds); }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }
        compilingMediaSnapshot = null; requestedMediaPreview = null; installedMediaRevision = attemptedMediaRevision = pendingMediaRevision = null;
        mediaPreviewWorkspace = null; mediaPreviewCaptureError = null; mediaPreviewCheckTime = default; authoredPreviewSourceHashes.Clear();
        mediaPollingCache.Clear();
        CloseAnimationPreview(); CloseSoundPreview();
    }
}
