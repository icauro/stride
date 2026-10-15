using Stride.Core.Assets;
using Stride.Core.Assets.Analysis;
using Stride.Core.Assets.Tracking;
using Stride.Core.Diagnostics;
using Stride.Core.IO;
using Stride.Core.Serialization.Contents;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private sealed record PreparedFactoryAsset(AssetItem Item, string Path);
    private sealed record FactoryReferenceCertificate(AssetItem Item, Asset Asset, Asset AuthoredAsset, Package Package, string Location, string Path, EditorAuthoringState? State, long Revision);
    private sealed record FactoryFolderCertificate(string Id, Package Package, string Path, bool Code);
    private sealed record FactorySourceRequest(ProjectWorkspace Workspace, Package Package, string PackagePath, string? AssetNamespace,
        string AssetFolder, PreparedFactoryAsset[] Assets, Dictionary<AssetId, string[]> Sources,
        FactoryReferenceCertificate[] References, FactoryFolderCertificate[] Folders, IDisposable[] Leases, Action Publish, Action? Success, Action? ValidateProducer, Action? Prepare);
    private Task<FactorySourceBaseline>? factorySourceTask;
    private CancellationTokenSource? factorySourceCancellation;
    private FactorySourceRequest? factorySourceRequest;

    private void RequestFactorySourcePublication(Package package, PreparedFactoryAsset[] assets, string title, Action publish,
        EditorAssetTemplateAdapterLease? producer = null, Action? success = null, Action? prepare = null)
    {
        if (!CanAddAsset || !IsCurrentEditableAssetPackage(package)) throw new InvalidOperationException("The asset creation target is no longer available.");
        var collector = new SourceFilesCollector();
        var sources = assets.ToDictionary(asset => asset.Item.Id, asset => collector.GetSourceFiles(asset.Item.Asset)
            .Where(source => source.Value).Select(source => Path.GetFullPath(source.Key.ToOSPath(), Path.GetDirectoryName(asset.Path)!))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
        var paths = sources.Values.SelectMany(values => values).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (paths.Length == 0) { prepare?.Invoke(); PublishFactorySourceBatch(title, publish); success?.Invoke(); return; }
        var created = assets.Select(asset => asset.Item.Id).ToHashSet();
        var references = assets.SelectMany(asset => AssetReferenceAnalysis.Visit(asset.Item.Asset)).Select(link => link.Reference)
            .OfType<IReference>().Select(reference => reference.Id).Where(id => !created.Contains(id)).Distinct()
            .Select(id => workspace!.Session.FindAsset(id)).OfType<AssetItem>().Select(item =>
            {
                var state = authoring!.Find(item.Id);
                return new FactoryReferenceCertificate(item, item.Asset, FactoryReferenceAsset(item, state), item.Package, item.Location.ToString(), item.FullPath.ToOSPath(), state, (state?.Document as IAssetTagDocument)?.EditRevision ?? 0);
            }).ToArray();
        var folders = assets.Select(asset =>
        {
            var mount = assetBrowser.Packages.First(root => ReferenceEquals(root.Package, package)).Children.First(folder => folder.IsCode == (asset.Item.Asset is IProjectAsset));
            var relative = NativeAssetUrls.Unqualify(package, asset.Item.Location).Replace('\\', '/');
            var slash = relative.LastIndexOf('/'); var parent = slash < 0 ? "" : relative[..slash];
            var folder = assetBrowser.FindFolder(mount.Id + (parent.Length == 0 ? "" : "/" + parent));
            while (folder == null && parent.Length > 0)
            { slash = parent.LastIndexOf('/'); parent = slash < 0 ? "" : parent[..slash]; folder = assetBrowser.FindFolder(mount.Id + (parent.Length == 0 ? "" : "/" + parent)); }
            folder ??= mount;
            return new FactoryFolderCertificate(folder.Id, package, folder.RelativePath, folder.IsCode);
        }).Distinct().ToArray();
        var leases = new List<IDisposable>();
        var transferred = false;
        try
        {
            foreach (var reference in references) if (reference.State != null) leases.Add(reference.State.AcquireView());
            var retained = producer?.Retain(); if (retained != null) leases.Add(retained);
            var cancellation = new CancellationTokenSource();
            factorySourceRequest = new(workspace!, package, package.FullPath.ToOSPath(), package.Container?.AssetNamespace ?? package.AssetNamespace,
                package.GetDefaultAssetFolder().ToString(), assets, sources, references, folders, leases.ToArray(),
                () => PublishFactorySourceBatch(title, publish), success, retained == null ? null : retained.Validate, prepare);
            factorySourceCancellation = cancellation;
            transferred = true;
            factorySourceTask = StartFactorySourceHashTask(paths, cancellation);
            BeginAssetImportProgress(false);
        }
        catch
        {
            if (transferred) StopFactorySourceCreation();
            else ReleaseFactorySourceLeases(leases);
            throw;
        }
    }

    private static Asset FactoryReferenceAsset(AssetItem item, EditorAuthoringState? state)
        => state?.Document is IAssetTagDocument canonical ? canonical.AuthoredAsset : item.Asset;

    private Task<FactorySourceBaseline> StartFactorySourceHashTask(string[] paths, CancellationTokenSource cancellation)
        => Task.Run(() => FactorySourceBaseline.Prepare(paths, cancellation.Token, (index, file) =>
        {
            if (!cancellation.IsCancellationRequested) Volatile.Write(ref assetImportProgress, new AssetImportWorkerProgress
            { File = file, FileIndex = index, FileCount = paths.Length, Message = $"Computing hashes of {paths.Length} source files..." });
        }), cancellation.Token);

    private void PublishFactorySourceBatch(string title, Action publish)
    {
        ValidatePickerCreationScope();
        var before = CaptureCreationSelection();
        try
        {
            using var capture = sessionHistory!.CaptureOperationsAtomic(() =>
            {
                if (pendingResourcePackage is { } package) JoinPendingResourceCopies(package);
                publish();
            });
            capture.Publish(sessionHistory, title);
            MarkPickerCreationPublished();
        }
        catch (Exception failure) { RestoreFailedCreationSelection(before, failure); throw; }
    }

    private void PumpFactorySourceCreation()
    {
        PumpFactorySourceRetirements();
        if (factorySourceTask is not { IsCompleted: true } task) return;
        var request = factorySourceRequest; var cancellation = factorySourceCancellation;
        factorySourceTask = null; factorySourceRequest = null; factorySourceCancellation = null;
        FactorySourceBaseline? baseline = null;
        try
        {
            if (task.IsCompletedSuccessfully) baseline = task.Result;
            if (task.IsCanceled || cancellation?.IsCancellationRequested == true) { CompleteAssetImportProgress(null, null, true); return; }
            baseline ??= task.GetAwaiter().GetResult();
            if (request == null || !ReferenceEquals(workspace, request.Workspace) || !CanAddAsset || !IsCurrentEditableAssetPackage(request.Package)
                || !SamePath(request.Package.FullPath.ToOSPath(), request.PackagePath)
                || request.AssetNamespace != (request.Package.Container?.AssetNamespace ?? request.Package.AssetNamespace)
                || request.AssetFolder != request.Package.GetDefaultAssetFolder().ToString())
                throw new InvalidOperationException("The asset creation target changed during source preparation.");
            baseline.Validate();
            request.ValidateProducer?.Invoke();
            foreach (var folder in request.Folders)
                if (assetBrowser.FindFolder(folder.Id) is not { } current || !ReferenceEquals(current.Package, folder.Package)
                    || current.RelativePath != folder.Path || current.IsCode != folder.Code)
                    throw new InvalidOperationException("The creation folder changed during source preparation.");
            foreach (var reference in request.References)
                if (!ReferenceEquals(workspace!.Session.FindAsset(reference.Item.Id), reference.Item) || !ReferenceEquals(reference.Item.Asset, reference.Asset)
                    || !ReferenceEquals(reference.Item.Package, reference.Package) || reference.Item.Location.ToString() != reference.Location || !SamePath(reference.Item.FullPath.ToOSPath(), reference.Path)
                    || !ReferenceEquals(authoring!.Find(reference.Item.Id), reference.State)
                    || !ReferenceEquals(FactoryReferenceAsset(reference.Item, reference.State), reference.AuthoredAsset)
                    || ((reference.State?.Document as IAssetTagDocument)?.EditRevision ?? 0) != reference.Revision)
                    throw new InvalidOperationException("A referenced asset changed during source preparation.");
            request.Prepare?.Invoke(); baseline.Validate();
            foreach (var asset in request.Assets)
            {
                if (!AssetRegistry.GetPublicTypes().Contains(asset.Item.Asset.GetType())) throw new InvalidOperationException("The factory asset type is no longer available.");
                if (request.Sources[asset.Item.Id].Length == 0) continue;
                SourceHashesHelper.ConvertPaths(asset.Item.Asset, new UDirectory(Path.GetDirectoryName(asset.Path)!), UPathType.Absolute);
                var hashes = SourceHashesHelper.GetAllHashes(asset.Item.Asset);
                foreach (var path in request.Sources[asset.Item.Id])
                    hashes[new UFile(path)] = baseline.Hashes.ContentHashes.TryGetValue(path, out var hash)
                        ? hash : throw new InvalidDataException("An affecting source has no native hash.");
                SourceHashesHelper.UpdateHashes(asset.Item.Asset, hashes);
                SourceHashesHelper.ConvertPaths(asset.Item.Asset, new UDirectory(Path.GetDirectoryName(asset.Path)!), UPathType.Relative);
            }
            request.Publish(); request.Success?.Invoke(); CompleteAssetImportProgress(null, null, false);
        }
        catch (Exception error)
        { CompleteAssetImportProgress(null, error, false); outputLog.Append("Project", LogMessageType.Error, "Asset import", error.Message, error.ToString()); }
        finally
        {
            try { RetireFactorySourceRequest(task, cancellation, request, baseline); }
            finally { Volatile.Write(ref assetImportProgress, null); }
        }
    }

    private void StopFactorySourceCreation()
    {
        var task = factorySourceTask; var cancellation = factorySourceCancellation; var request = factorySourceRequest;
        factorySourceTask = null; factorySourceCancellation = null; factorySourceRequest = null;
        if (task != null || cancellation != null || request != null)
            RetireFactorySourceRequest(task, cancellation, request, cancel: true);
        else PumpFactorySourceRetirements();
    }
    private void ReleaseFactorySourceLeases(IEnumerable<IDisposable> leases)
    {
        foreach (var lease in leases) RetireTemplateReferenceLease(lease);
    }
}
