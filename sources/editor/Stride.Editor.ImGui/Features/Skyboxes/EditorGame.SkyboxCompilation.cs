using System.Security.Cryptography;
using System.Text;
using Stride.Assets;
using Stride.Assets.Effect;
using Stride.Assets.Media;
using Stride.Assets.Skyboxes;
using Stride.Assets.Textures;
using Stride.Core;
using Stride.Core.Assets;
using Stride.Core.IO;
using Stride.Core.Serialization;
using Stride.Core.Serialization.Contents;
using Stride.Core.Storage;
using Stride.Graphics;
using Stride.Rendering.Materials;
using Stride.Rendering.Skyboxes;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private sealed record InstalledSkybox(CompiledProjectContent Content, ContentManager Cache, Skybox Skybox, string Url, ObjectId Identity);
    private readonly Dictionary<AssetMetadataDocument, InstalledSkybox> installedSkyboxes = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<AssetMetadataDocument, string> attemptedSkyboxRevisions = new(ReferenceEqualityComparer.Instance);
    private readonly List<(IDisposable Resource, int Frame)> retiredSkyboxResources = [];
    private ProjectWorkspace? skyboxWorkspace;
    private AssetMetadataDocument? requestedSkyboxDocument, compilingSkyboxDocument;
    private Task<string>? skyboxBuild;
    private CancellationTokenSource? skyboxCancellation;
    private IDisposable? skyboxOwnerLease;
    private string? compilingSkyboxRevision, pendingSkyboxRevision;
    private AssetMetadataDocument? pendingSkyboxDocument;
    private long skyboxGlobalContentEpoch;
    private readonly Dictionary<AssetId, long> skyboxTextureContentEpochs = [];
    private DateTime nextSkyboxCheck, skyboxDebounce;
    private bool IsSkyboxSettingsUpdating => skyboxBuild != null;
    private SkyboxCompilationSnapshot CaptureSkyboxSnapshot(AssetMetadataDocument owner)
    {
        var current = workspace!; var item = current.FindAsset(owner.AuthoredAsset.Id);
        var settingsItem = current.Session.Packages.SelectMany(package => package.Assets).FirstOrDefault(asset => asset.Asset is GameSettingsAsset);
        var settings = settingsItem != null && authoring?.Find(settingsItem.Id)?.Document is IAssetTagDocument settingsOwner
            ? (GameSettingsAsset)settingsOwner.AuthoredAsset : current.Package.GetGameSettingsAssetOrDefault();
        var rendering = settings.GetOrDefault<RenderingSettings>();
        var parameters = new SkyboxPreviewBuildParameters
        {
            RootInput = CaptureSkyboxInput(item),
            AssetPath = owner.Path, Url = item.Location.ToString(), Platform = PlatformType.Windows,
            GraphicsPlatform = Stride.Graphics.GraphicsDevice.Platform, GraphicsProfile = rendering.DefaultGraphicsProfile,
            ColorSpace = rendering.ColorSpace, TextureQuality = settings.GetOrDefault<TextureSettings>().TextureQuality,
        };
        var textures = new Dictionary<AssetId, SkyboxTextureSnapshot>();
        void TextureSnapshot(AssetId id, string url)
        {
            var dependency = current.Session.FindAsset(id) ?? throw new InvalidOperationException("A skybox texture dependency is missing.");
            var authored = authoring?.Find(id)?.Document is IAssetTagDocument retained ? retained.AuthoredAsset : dependency.Asset;
            if (authored is not TextureAsset texture) throw new InvalidOperationException("A skybox texture dependency has an incompatible asset type.");
            var clone = AssetCloner.Clone(texture);
            var path = dependency.FullPath.ToOSPath();
            if (!string.IsNullOrEmpty(clone.Source)) clone.Source = new UFile(Path.GetFullPath(clone.Source, Path.GetDirectoryName(path)!));
            textures[id] = new(new SkyboxPreviewDependency { Id = id.ToString(), Url = dependency.Location.ToString(), AssetPath = path,
                Input = CaptureSkyboxInput(dependency) }, clone);
        }
        if (ContentAssetReference.Read(owner.SkyboxSettings!.CubeMap) is { } reference) TextureSnapshot(reference.Id, reference.Location);
        foreach (var profile in new[] { GraphicsProfile.Level_9_1, GraphicsProfile.Level_10_0 })
        {
            var lutReference = AttachedReferenceManager.GetAttachedReference(MaterialSpecularMicrofacetEnvironmentGGXLUT.CreateLookupTableReference(profile));
            TextureSnapshot(lutReference.Id, lutReference.Url);
        }
        var shaders = current.Session.Packages.SelectMany(package => package.Assets).Where(asset => asset.Asset is EffectShaderAsset).DistinctBy(asset => asset.Id)
            .Select(shader => new SkyboxShaderSnapshot(new SkyboxPreviewDependency
            { Id = shader.Id.ToString(), Url = shader.Location.ToString(), AssetPath = shader.FullPath.ToOSPath(), Input = CaptureSkyboxInput(shader) },
                authoring?.Find(shader.FullPath.ToOSPath())?.Document is ScriptSourceDocument source ? source.Text : null)).ToArray();
        return new(AssetCloner.Clone(owner.SkyboxSettings), parameters, textures.Values.ToArray(), shaders);
    }
    private static MediaPreviewAssetInput CaptureSkyboxInput(AssetItem item) => new()
    {
        Id = item.Id.ToString(), Url = item.Location.ToString(), LocalUrl = item.UnqualifiedUrl.ToString(),
        AssetPath = item.FullPath.ToOSPath(), SourcePackagePath = item.Package.FullPath.ToOSPath(),
        AssetNamespace = item.Package.Container?.AssetNamespace, PackageDependencies = CapturedPackageDependencies(item.Package),
    };
    private string SkyboxRevision(AssetMetadataDocument owner)
    {
        var current = workspace!;
        var key = new StringBuilder().Append(skyboxGlobalContentEpoch).Append('|').Append(owner.AuthoredAsset.Id).Append('|').Append(owner.SkyboxSettingsEditVersion).Append('|').Append(owner.EditRevision);
        void Source(string path)
        {
            var file = new FileInfo(path); key.Append('|').Append(path).Append(':').Append(file.Exists ? file.Length : -1).Append(':').Append(file.LastWriteTimeUtc.Ticks);
            if (authoring?.Find(path)?.Document is ScriptSourceDocument source) key.Append(':').Append(source.Version);
        }
        void TextureRevision(AssetId id)
        {
            key.Append('|').Append(id).Append(':').Append(skyboxTextureContentEpochs.GetValueOrDefault(id));
            if (current.Session.FindAsset(id) is not { } item) { key.Append(":missing"); return; }
            var authored = authoring?.Find(item.Id)?.Document as AssetMetadataDocument;
            key.Append(':').Append(authored?.TextureSettingsEditVersion ?? 0).Append(':').Append(authored?.EditRevision ?? 0);
            if ((authored?.AuthoredAsset ?? item.Asset) is TextureAsset texture && !string.IsNullOrEmpty(texture.Source))
                Source(Path.GetFullPath(texture.Source, Path.GetDirectoryName(item.FullPath.ToOSPath())!));
        }
        if (ContentAssetReference.Read(owner.SkyboxSettings!.CubeMap) is { } reference) TextureRevision(reference.Id);
        foreach (var profile in new[] { GraphicsProfile.Level_9_1, GraphicsProfile.Level_10_0 })
            TextureRevision(AttachedReferenceManager.GetAttachedReference(MaterialSpecularMicrofacetEnvironmentGGXLUT.CreateLookupTableReference(profile)).Id);
        foreach (var item in current.Session.Packages.SelectMany(package => package.Assets))
        {
            if (item.Asset is EffectShaderAsset) Source(item.FullPath.ToOSPath());
            if (item.Asset is GameSettingsAsset settings)
            {
                if (authoring?.Find(item.Id)?.Document is IAssetTagDocument retained) settings = (GameSettingsAsset)retained.AuthoredAsset;
                var rendering = settings.GetOrDefault<RenderingSettings>();
                key.Append('|').Append(rendering.ColorSpace).Append(':').Append(rendering.DefaultGraphicsProfile).Append(':').Append(settings.GetOrDefault<TextureSettings>().TextureQuality).Append(':').Append(settings.GetOrDefault<EditorSettings>().RenderingMode);
            }
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key.ToString())));
    }
    private string? lastSkyboxCompilationError;
    private void PumpSkyboxSettingsUpdate()
    {
        try { PumpSkyboxSettingsUpdateCore(); }
        catch (Exception error)
        {
            if (lastSkyboxCompilationError != error.ToString())
            { lastSkyboxCompilationError = error.ToString(); RecordAssetPreviewRenderFailure("Skybox compilation", error); statusMessage = error.Message; }
        }
    }
    private void PumpSkyboxSettingsUpdateCore()
    {
        if (!ReferenceEquals(skyboxWorkspace, workspace)) { StopSkyboxSettingsUpdate(); skyboxWorkspace = workspace; }
        for (var index = retiredSkyboxResources.Count - 1; index >= 0; index--)
            if (frames >= retiredSkyboxResources[index].Frame) { RetireEditorPreview(retiredSkyboxResources[index].Resource); retiredSkyboxResources.RemoveAt(index); }
        if (skyboxBuild?.IsCompleted == true)
        {
            var completed = skyboxBuild; skyboxBuild = null;
            var cancelled = skyboxCancellation!.IsCancellationRequested; skyboxCancellation.Dispose(); skyboxCancellation = null;
            var owner = compilingSkyboxDocument!; compilingSkyboxDocument = null;
            try
            {
                if (completed.IsCanceled) return;
                if (completed.IsFaulted) { if (!cancelled) statusMessage = completed.Exception!.GetBaseException().Message; return; }
                var output = completed.Result;
                if (cancelled || workspace == null || authoring?.Find(owner.AuthoredAsset.Id)?.Document != owner
                    || compilingSkyboxRevision != SkyboxRevision(owner)) { CompiledSkyboxBuild.ReleaseOutput(output); return; }
                try { InstallSkyboxPreviewContent(owner, output); }
                catch { CompiledSkyboxBuild.ReleaseOutput(output); throw; }
            }
            finally { skyboxOwnerLease?.Dispose(); skyboxOwnerLease = null; }
        }
        if (workspace == null || DateTime.UtcNow < nextSkyboxCheck) return;
        nextSkyboxCheck = DateTime.UtcNow.AddMilliseconds(250);
        if (skyboxBuild != null)
        {
            if (compilingSkyboxDocument == null || !ReferenceEquals(authoring?.Find(compilingSkyboxDocument.AuthoredAsset.Id)?.Document, compilingSkyboxDocument)
                || SkyboxRevision(compilingSkyboxDocument) != compilingSkyboxRevision) skyboxCancellation!.Cancel();
            return;
        }
        var candidates = new[] { requestedSkyboxDocument }.Where(owner => owner != null).Cast<AssetMetadataDocument>()
            .Concat(installedSkyboxes.Keys)
            .Concat(documents.AuthoredDocuments.OfType<AssetMetadataDocument>().Where(owner => owner.SkyboxSettings != null))
            .Distinct<AssetMetadataDocument>(ReferenceEqualityComparer.Instance);
        AssetMetadataDocument? document = null; string? revision = null;
        foreach (var candidate in candidates)
        {
            if (!ReferenceEquals(authoring?.Find(candidate.AuthoredAsset.Id)?.Document, candidate)) continue;
            var currentRevision = SkyboxRevision(candidate);
            if (attemptedSkyboxRevisions.GetValueOrDefault(candidate) == currentRevision) continue;
            document = candidate; revision = currentRevision; break;
        }
        if (document == null) return;
        if (!ReferenceEquals(pendingSkyboxDocument, document) || pendingSkyboxRevision != revision)
        { pendingSkyboxDocument = document; pendingSkyboxRevision = revision; skyboxDebounce = DateTime.UtcNow.AddMilliseconds(250); return; }
        if (DateTime.UtcNow < skyboxDebounce || !CanStartAuthoringPreviewUpdate) return;
        var snapshot = CaptureSkyboxSnapshot(document);
        attemptedSkyboxRevisions[document] = compilingSkyboxRevision = revision; compilingSkyboxDocument = document;
        skyboxOwnerLease = authoring!.Find(document.AuthoredAsset.Id)!.AcquireView();
        skyboxCancellation = new(); var cancellation = skyboxCancellation.Token;
        skyboxBuild = Task.Run(() => CompiledSkyboxBuild.Build(snapshot, cancellation, RecordBuildOutput), cancellation);
    }
    private void InstallSkyboxPreviewContent(AssetMetadataDocument owner, string output)
    {
        var content = new CompiledProjectContent(this, output, () => CompiledSkyboxBuild.ReleaseOutput(output));
        var url = workspace!.FindAsset(owner.AuthoredAsset.Id).Location.ToString();
        var cache = content.CreateObjectCache(); Skybox? skybox = null; SkyboxAssetPreview? next = null; bool published = false;
        try
        {
            skybox = cache.Load<Skybox>(url, ContentManagerLoaderSettings.StreamingDisabled);
            var authoredSettings = NavigationGameSettings() ?? workspace.Package.GetGameSettingsAssetOrDefault();
            var settings = authoredSettings.GetOrDefault<RenderingSettings>();
            var mode = authoredSettings.GetOrDefault<EditorSettings>().RenderingMode;
            if (ReferenceEquals(requestedSkyboxDocument, owner))
            {
                next = new SkyboxAssetPreview(this, renderer!, content, url, mode == RenderingMode.HDR, settings.ColorSpace);
                if (skyboxPreview != null)
                {
                    next.SetMaterial(skyboxPreview.Metalness, skyboxPreview.Glossiness);
                    next.Camera.RestoreState(skyboxPreview.Camera.CaptureState());
                }
            }
            if (!Content.FileProvider.ObjectDatabase.ContentIndexMap.TryGetValue(url, out var identity)) throw new InvalidOperationException("The compiled skybox has no content identity.");
            foreach (var view in SnapshotSceneResourceViews())
                view.Viewport.ReplaceRuntimeSkybox(url, skybox, view.Document);
            foreach (var oldOwner in installedSkyboxes.Where(entry => entry.Value.Url == url).Select(entry => entry.Key).ToArray())
                if (installedSkyboxes.Remove(oldOwner, out var previous)) RetireInstalledSkybox(previous);
            installedSkyboxes[owner] = new(content, cache, skybox, url, identity); published = true;
            if (next != null)
            {
                if (skyboxPreview != null) retiredSkyboxResources.Add((skyboxPreview, frames + 2));
                skyboxPreview = next; next = null; focusAssetPreview = true;
            }
            InvalidateAssetThumbnails(owner.AuthoredAsset.Id);
        }
        catch
        {
            if (next != null) RetireEditorPreview(next);
            if (!published)
            {
                if (skybox != null) RetireInstalledSkybox(new(content, cache, skybox, url, default));
                else RetireEditorPreview(content);
            }
            throw;
        }
    }
    internal bool TryGetRefreshedSkybox(string url, out Skybox skybox)
    {
        var installed = installedSkyboxes.Values.FirstOrDefault(value => value.Url == url);
        if (installed != null && Content.FileProvider.ObjectDatabase.ContentIndexMap.TryGetValue(url, out var identity) && identity == installed.Identity)
        { skybox = installed.Skybox; return true; }
        skybox = null!; return false;
    }
    private sealed class SkyboxCacheRetirement(EditorGame host, InstalledSkybox installed) : IDisposable
    {
        private bool skyboxReleased, contentReleased;
        public void Dispose()
        {
            if (!skyboxReleased && host.SceneViewsUseImportedContent([installed.Skybox]))
                throw new PreviewResourceDrainPendingException();
            if (!skyboxReleased) { installed.Cache.Unload(installed.Skybox); skyboxReleased = true; }
            if (!contentReleased) { installed.Content.Dispose(); contentReleased = true; }
            host.skyboxCleanupOwners.Remove(installed);
        }
    }
    private readonly Dictionary<InstalledSkybox, SkyboxCacheRetirement> skyboxCleanupOwners = new(ReferenceEqualityComparer.Instance);
    private SkyboxCacheRetirement GetSkyboxCleanupOwner(InstalledSkybox installed)
    {
        if (!skyboxCleanupOwners.TryGetValue(installed, out var owner))
            skyboxCleanupOwners.Add(installed, owner = new(this, installed));
        return owner;
    }
    private void RetireInstalledSkybox(InstalledSkybox installed) => retiredSkyboxResources.Add((GetSkyboxCleanupOwner(installed), frames + 2));
    private void NotifySkyboxCompiledDependencyChanged(AssetId? texture)
    {
        if (texture is { } id) skyboxTextureContentEpochs[id] = skyboxTextureContentEpochs.GetValueOrDefault(id) + 1;
        else skyboxGlobalContentEpoch++;
        nextSkyboxCheck = DateTime.MinValue;
    }
    private void ReleaseSkyboxSettingsDocument(IEditorDocument owner)
    {
        if (owner is AssetMetadataDocument metadata) attemptedSkyboxRevisions.Remove(metadata);
        if (ReferenceEquals(requestedSkyboxDocument, owner)) requestedSkyboxDocument = null;
        if (ReferenceEquals(compilingSkyboxDocument, owner)) skyboxCancellation?.Cancel();
        // The installed borrowed runtime generation remains retained until replacement
        // or scene/project release, just like compiled texture generations.
    }
    private void StopSkyboxSettingsUpdate()
    {
        skyboxCancellation?.Cancel();
        if (skyboxBuild != null)
        {
            var pending = skyboxBuild;
            _ = pending.ContinueWith(task => { if (task.Status == TaskStatus.RanToCompletion) CompiledSkyboxBuild.ReleaseOutput(task.Result); else _ = task.Exception; }, TaskScheduler.Default);
        }
        skyboxBuild = null; skyboxCancellation?.Dispose(); skyboxCancellation = null; skyboxOwnerLease?.Dispose(); skyboxOwnerLease = null;
        compilingSkyboxDocument = requestedSkyboxDocument = pendingSkyboxDocument = null; pendingSkyboxRevision = compilingSkyboxRevision = null;
        skyboxTextureContentEpochs.Clear(); skyboxGlobalContentEpoch++;
        skyboxPreview?.Dispose(); skyboxPreview = null;
        foreach (var retired in retiredSkyboxResources.OrderBy(entry => entry.Resource is SkyboxCacheRetirement).ToArray())
        { retired.Resource.Dispose(); retiredSkyboxResources.Remove(retired); }
        foreach (var installed in installedSkyboxes.ToArray())
        { GetSkyboxCleanupOwner(installed.Value).Dispose(); installedSkyboxes.Remove(installed.Key); }
        attemptedSkyboxRevisions.Clear();
    }
}
