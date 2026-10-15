using Stride.Core;
using Stride.Core.Assets;
using Stride.Core.Assets.Analysis;
using Stride.Core.Assets.Tracking;
using Stride.Core.Diagnostics;
using Stride.Core.IO;
using Stride.Core.Reflection;
using Stride.Core.Serialization.Contents;
using Stride.Core.Serialization;
using Stride.Assets.Models;
using Stride.Rendering;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private Task<AssetSourceImportResult>? assetImportTask;
    private CancellationTokenSource? assetImportCancellation;
    private ProjectWorkspace? assetImportWorkspace;
    private Package? assetImportPackage;
    private string assetImportFolder = "";
    private int detachedAssetImports;
    private AssetImportWorkerProgress? assetImportProgress;
    private AssetImportOptions? requestedAssetImportOptions;
    private IReadOnlyDictionary<string, AssetImportOptions>? requestedAssetImportFileOptions;
    private AssetImportWorkerProgress? AssetImportProgress => Volatile.Read(ref assetImportProgress);
    private AssetImportChoice[] GetAssetImportChoices(string file, Type? preferredType = null) => AssetSourceImport.Choices(file, preferredType);
    private void CancelAssetImport() { assetImportCancellation?.Cancel(); assetDropSourcesCancellation?.Cancel(); factorySourceCancellation?.Cancel(); resourceSourceCopyCancellation?.Cancel(); preparedAssetDrop = null; }
    private bool IsAssetImportUpdating => assetImportTask != null || assetDropSourcesTask != null || factorySourceTask != null || resourceSourceCopyTask != null || resourceCopyReady != null || preparedAssetDrop != null || Volatile.Read(ref detachedAssetImports) != 0;

    private void RequestAssetImportFiles(string[] files, Package targetPackage, string relativeFolder, Type? preferredAssetType = null, AssetImportOptions? options = null,
        IReadOnlyDictionary<string, AssetImportOptions>? fileOptions = null, bool resourceSourcesPrepared = false)
    {
        if (!resourceSourcesPrepared)
        {
            files = files.ToArray();
            options = options == null ? null : options with { Outputs = options.Outputs == null ? null : new Dictionary<Type, bool>(options.Outputs) };
            fileOptions = fileOptions?.ToDictionary(pair => pair.Key, pair => pair.Value with
                { Outputs = pair.Value.Outputs == null ? null : new Dictionary<Type, bool>(pair.Value.Outputs) }, StringComparer.OrdinalIgnoreCase);
        }
        if (!resourceSourcesPrepared && RequestResourceSourceCopies(files, targetPackage, relativeFolder, copied =>
        {
            var remapped = fileOptions?.ToDictionary(pair =>
            {
                var index = Array.FindIndex(files, file => SamePath(file, pair.Key));
                return index < 0 ? pair.Key : copied[index];
            }, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            RequestAssetImportFiles(copied, targetPackage, relativeFolder, preferredAssetType, options, remapped, true);
        })) return;
        if (!CanAddAsset || IsAssetImportUpdating || targetPackage.IsReadOnly)
            throw new InvalidOperationException("The asset import target is not available.");
        if (!workspace!.Session.Packages.Contains(targetPackage)) throw new InvalidOperationException("The target package is not part of this session.");
        var folder = relativeFolder.Replace('\\', '/').Trim('/');
        if (Path.IsPathRooted(relativeFolder) || folder.Split('/').Any(segment => segment is "." or ".."))
            throw new ArgumentException("Invalid asset folder.", nameof(relativeFolder));
        var sources = files.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (sources.Length == 0) return;
        foreach (var source in sources)
            if (!File.Exists(source)) throw new FileNotFoundException("The source file isn't available.", source);
        documents.CommitAll();
        var immutableOptions = options == null ? null : options with { Outputs = options.Outputs == null ? null : new Dictionary<Type, bool>(options.Outputs) };
        var immutableFiles = fileOptions?.ToDictionary(pair => Path.GetFullPath(pair.Key), pair => pair.Value with
            { Outputs = pair.Value.Outputs == null ? null : new Dictionary<Type, bool>(pair.Value.Outputs) }, StringComparer.OrdinalIgnoreCase);
        if (immutableFiles?.Keys.Any(path => !sources.Contains(path, StringComparer.OrdinalIgnoreCase)) == true)
            throw new ArgumentException("An importer option does not belong to the requested source batch.");
        foreach (var option in immutableFiles?.Values.AsEnumerable() ?? [])
            if (option.ReuseSkeleton is { } id && workspace.Session.FindAsset(id)?.Asset is not SkeletonAsset)
                throw new InvalidOperationException("The selected skeleton is no longer available.");
        if (immutableOptions?.ReuseSkeleton is { } skeletonId && workspace.Session.FindAsset(skeletonId)?.Asset is not SkeletonAsset)
            throw new InvalidOperationException("The selected skeleton is no longer available.");
        var assemblies = CaptureAssetImporterAssemblies();
        assetImportWorkspace = workspace; assetImportPackage = targetPackage; assetImportFolder = folder;
        requestedAssetImportOptions = immutableOptions; Volatile.Write(ref assetImportProgress, null);
        requestedAssetImportFileOptions = immutableFiles;
        var cancellation = new CancellationTokenSource(); assetImportCancellation = cancellation;
        // The task retains detached importer outputs and file names only, never
        // the workspace, canonical owner, package, or session graph.
        assetImportTask = Task.Run(() => AssetSourceImport.Prepare(sources, preferredAssetType, cancellation.Token,
            assemblies: assemblies, options: immutableOptions, progress: update =>
            { if (!cancellation.IsCancellationRequested) Volatile.Write(ref assetImportProgress, update); }, fileOptions: immutableFiles), cancellation.Token);
        BeginAssetImportProgress(false);
    }

    private void PumpAssetImportUpdate()
    {
        PumpPickerAssetCreation();
        PumpResourceSourceCopies();
        PumpFactorySourceCreation();
        PumpAssetDropSources();
        RefreshModifiedSourceStatus();
        if (assetImportTask is not { IsCompleted: true } task) return;
        var owner = assetImportWorkspace; var package = assetImportPackage; var folder = assetImportFolder;
        assetImportTask = null; assetImportWorkspace = null; assetImportPackage = null;
        var cancellation = assetImportCancellation; assetImportCancellation = null;
        try
        {
            if (task.IsCanceled || cancellation?.IsCancellationRequested == true)
            { CompleteAssetImportProgress(null, null, true); return; }
            var result = task.GetAwaiter().GetResult();
            CompleteAssetImportProgress(result.Log.Messages, null, false);
            foreach (var message in result.Log.Messages) outputLog.Append("Project", message.Type, "Asset import", message.Text, message.ToString());
            if (!ReferenceEquals(workspace, owner) || package == null || package.IsReadOnly || !workspace!.Session.Packages.Contains(package)) return;
            if (IsUserProjectBuilding || projectLoading != null || closeRequested || closingDocument != null)
                throw new InvalidOperationException("The asset import target is no longer available.");
            if (sourceReimportRequest != null) PublishReimportedAsset(result);
            else PublishImportedAssets(package, folder, result);
        }
        catch (Exception error)
        {
            var messages = (error as SourceReimportBatchException)?.Messages;
            CompleteAssetImportProgress(messages, error, false);
            if (messages != null)
                foreach (var message in messages) outputLog.Append("Project", message.Type, "Asset import", message.Text, message.ToString());
            outputLog.Append("Project", LogMessageType.Error, "Asset import", error.Message, error.ToString());
        }
        finally { CancelPendingResourceCopies(); cancellation?.Dispose(); ReleaseSourceReimportRequest(); requestedAssetImportOptions = null; requestedAssetImportFileOptions = null; Volatile.Write(ref assetImportProgress, null); }
    }

    private void PublishImportedAssets(Package package, string folder, AssetSourceImportResult result)
    {
        ValidatePickerCreationScope();
        AssetSourceImport.ValidateSources(result.SourceHashes);
        documents.CommitAll();
        var defaultFolder = package.GetDefaultAssetFolder();
        var absoluteFolder = defaultFolder.IsAbsolute ? defaultFolder : UPath.Combine(package.RootDirectory, defaultFolder);
        var prefix = folder.Length == 0 ? "" : folder + "/";
        var prepared = new List<(AssetItem Item, string Path)>();
        var locations = new Dictionary<AssetId, UFile>();
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var original in result.Assets)
        {
            if (locations.ContainsKey(original.Id) || workspace!.Session.FindAsset(original.Id) != null)
                throw new InvalidDataException("The imported asset identity is already in use.");
            var name = original.UnqualifiedUrl.FullPath.Replace('\\', '/');
            if (original.UnqualifiedUrl.IsAbsolute || name.Split('/').Any(part => !IsValidAssetLeafName(part)))
                throw new InvalidDataException("The importer returned an invalid asset location.");
            var extension = AssetRegistry.GetDefaultExtension(original.Asset.GetType());
            if (string.IsNullOrEmpty(extension)) throw new InvalidDataException("The imported asset has no registered file extension.");
            UFile Url(string leaf) { var relative = new UFile(prefix + leaf); return package.Container?.Qualify(relative) ?? relative; }
            string FullPath(string leaf) => Path.GetFullPath(UPath.Combine(absoluteFolder, new UFile(prefix + leaf + extension)).ToOSPath());
            bool Occupied(UFile leaf) => reserved.Contains(Url(leaf.ToString()).ToString())
                || package.Assets.Any(item => string.Equals(item.Location, Url(leaf.ToString()), StringComparison.OrdinalIgnoreCase))
                || File.Exists(FullPath(leaf.ToString())) || Directory.Exists(FullPath(leaf.ToString())) || authoring!.Find(FullPath(leaf.ToString())) != null;
            var unique = NamingHelper.ComputeNewName(name, Occupied, "{0}_{1}");
            var url = Url(unique); var path = FullPath(unique);
            reserved.Add(url.ToString()); locations.Add(original.Id, url);
            var item = new AssetItem(url, original.Asset) { SourceFolder = defaultFolder }; item.YamlMetadata.ReplaceWith(original.YamlMetadata);
            prepared.Add((item, path));
        }
        foreach (var preparedAsset in prepared)
        {
            var asset = preparedAsset.Item.Asset; var directory = Path.GetDirectoryName(preparedAsset.Path)!;
            var options = requestedAssetImportOptions;
            if (result.AssetSources?.TryGetValue(preparedAsset.Item.Id, out var sourceFile) == true
                && requestedAssetImportFileOptions?.TryGetValue(sourceFile, out var selected) == true) options = selected;
            var reusedSkeleton = options?.ReuseSkeleton is { } skeletonId
                ? workspace!.Session.FindAsset(skeletonId) ?? throw new InvalidOperationException("The selected skeleton is no longer available.") : null;
            if (reusedSkeleton != null && reusedSkeleton.Asset is not SkeletonAsset) throw new InvalidOperationException("The selected skeleton changed type.");
            if (asset is ModelAsset model && reusedSkeleton != null)
                model.Skeleton = AttachedReferenceManager.CreateProxyObject<Skeleton>(reusedSkeleton.Id, reusedSkeleton.Location);
            foreach (var link in AssetReferenceAnalysis.Visit(asset))
                if (link.Reference is IReference reference && locations.TryGetValue(reference.Id, out var location))
                    link.UpdateReference(reference.Id, location.ToString());
            foreach (var source in new SourceFilesCollector().GetSourceMembers(asset))
                if (source.Value != null && source.Value.IsAbsolute
                    && !source.Key.Apply(asset, MemberPathAction.ValueSet, new UFile(Path.GetRelativePath(directory, source.Value.ToOSPath()).Replace('\\', '/'))))
                    throw new InvalidOperationException("An imported source member could not be rebound.");
            var affecting = new SourceFilesCollector().GetSourceFiles(asset).Where(source => source.Value).Select(source =>
                Path.GetFullPath(source.Key.ToOSPath(), directory)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (affecting.Length > 0)
            {
                SourceHashesHelper.ConvertPaths(asset, new UDirectory(directory), UPathType.Absolute);
                var hashes = SourceHashesHelper.GetAllHashes(asset);
                foreach (var source in affecting) hashes[new UFile(source)] = result.ContentHashes.TryGetValue(source, out var hash)
                    ? hash : throw new InvalidDataException("An imported affecting source has no native hash.");
                SourceHashesHelper.UpdateHashes(asset, hashes);
            }
            SourceHashesHelper.ConvertPaths(asset, new UDirectory(directory), UPathType.Relative);
        }
        if (prepared.Count == 0) { CancelPendingResourceCopies(); return; }
        var before = CaptureCreationSelection();
        try
        {
            using var capture = sessionHistory!.CaptureOperationsAtomic(() =>
            {
                JoinPendingResourceCopies(package);
                foreach (var asset in prepared) CreatePreparedAsset(package, asset.Item, asset.Path, "Import assets");
            });
            capture.Publish(sessionHistory, "Import assets");
        }
        catch (Exception failure) { RestoreFailedCreationSelection(before, failure); throw; }
        SelectCreatedAssetBatch(prepared.Select(asset => asset.Item.Id));
        MarkPickerCreationPublished();
    }

    private bool stoppingAssetImportUpdate;
    private void StopAssetImportUpdate()
    {
        if (stoppingAssetImportUpdate) return;
        stoppingAssetImportUpdate = true;
        try
        {
        // Capture factory readers and unpublished owned copies before generic resource compensation.
        StopFactorySourceCreation();
        StopResourceSourceCopies();
        StopAssetDropSources();
        var task = assetImportTask; var cancellation = assetImportCancellation;
        assetImportTask = null; assetImportCancellation = null; assetImportWorkspace = null; assetImportPackage = null;
        CompleteAssetImportProgress(null, null, true);
        requestedAssetImportOptions = null; Volatile.Write(ref assetImportProgress, null);
        requestedAssetImportFileOptions = null;
        ReleaseSourceReimportRequest(); ClearModifiedSourceStatus();
        cancellation?.Cancel();
        if (task == null) { cancellation?.Dispose(); assetDropTemplatePanel?.Clear(); return; }
        Interlocked.Increment(ref detachedAssetImports);
        _ = task.ContinueWith(completed =>
        {
            try { if (completed.IsFaulted) _ = completed.Exception; }
            finally { cancellation?.Dispose(); Interlocked.Decrement(ref detachedAssetImports); }
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        assetDropTemplatePanel?.Clear();
        }
        finally { stoppingAssetImportUpdate = false; }
    }
    private AssetImportAssemblyContext[] CaptureAssetImporterAssemblies()
        => AssetSourceImport.CaptureAssemblies(workspace!.Session.Packages.SelectMany(package => package.LoadedAssemblies)
            .Where(owner => owner.Assembly != null && !owner.Assembly.IsDynamic && !string.IsNullOrEmpty(owner.Path))
            .Select(owner => AssetSourceImport.CaptureBoundAssemblyPath(owner.Assembly!, owner.Path)));
}
