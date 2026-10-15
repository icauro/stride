using Stride.Core.Assets;
using Stride.Core.Diagnostics;
using Stride.Core.IO;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private sealed class ResourceCopyAuthority { internal bool Canceled; }
    private readonly List<ResourceCopyRequest> executingResourceCopies = [];
    private sealed record ResourceCopyRequest(ProjectWorkspace Workspace, Package Package, string PackagePath,
        string[] Resources, string[] Sources, FactoryFolderCertificate Target, Action<string[]> Continue, IDisposable? Producer, ResourceCopyAuthority Authority);
    private ResourceSourceCopyPanel? resourceSourceCopyPanel;
    private ResourceCopyRequest? resourceCopyRequest;
    private ResourceCopyRequest? resourceCopyReady;
    private Task<PreparedResourceSourceCopy[]>? resourceSourceCopyTask;
    private CancellationTokenSource? resourceSourceCopyCancellation;
    private ResourceSourceCopyOperation? pendingResourceCopies;
    private Package? pendingResourcePackage;

    private string[] ResourceDirectories(Package package) => package.ResourceFolders.Select(folder => Path.GetFullPath(
        (folder.IsAbsolute ? folder : UPath.Combine(package.RootDirectory, folder)).ToOSPath())).ToArray();
    private bool RequestResourceSourceCopies(string[] files, Package package, string folder, Action<string[]> next, IDisposable? producer = null)
    {
        var ownsProducer = true;
        try
        {
        if (!CanAddAsset || !IsCurrentEditableAssetPackage(package)) { throw new InvalidOperationException("The asset import target is not available."); }
        if (Path.IsPathRooted(folder) || folder.Replace('\\', '/').Split('/').Any(segment => segment is "." or ".."))
        { throw new ArgumentException("Invalid asset folder.", nameof(folder)); }
        folder = folder.Replace('\\', '/').Trim('/');
        var sources = files.Select(Path.GetFullPath).ToArray(); var roots = ResourceDirectories(package);
        if (sources.All(source => roots.Any(root => source.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))))
        { return false; }
        if (resourceCopyRequest != null || pendingResourceCopies != null) { throw new InvalidOperationException("Resource source preparation is already active."); }
        var selectedFolder = AssetCreationBrowser.SelectedFolder;
        if (selectedFolder == null || !ReferenceEquals(selectedFolder.Package, package) || selectedFolder.RelativePath != folder)
        { throw new InvalidOperationException("The asset import target is not available."); }
        var target = new FactoryFolderCertificate(selectedFolder.Id, package, selectedFolder.RelativePath, selectedFolder.IsCode);
        var request = new ResourceCopyRequest(workspace!, package, package.FullPath.ToOSPath(), roots, sources, target, next, producer, new());
        resourceCopyRequest = request; ownsProducer = false;
        try
        {
            (resourceSourceCopyPanel ??= new()).Open(sources, roots, folder, path => WindowsFileDialogs.Save(Window.NativeWindow.Handle,
                EditorLocalization.Text("Save"), Path.GetExtension(path) + "\0*" + Path.GetExtension(path) + "\0\0", roots.FirstOrDefault(), Path.GetExtension(path).TrimStart('.'), Path.GetFileName(path)),
                (choices, selected) =>
                {
                    try
                    {
                    if (!ReferenceEquals(resourceCopyRequest, request) || request.Authority.Canceled) return;
                    ValidateResourceCopyRequest(request);
                    if (choices.Length == 0)
                    {
                        resourceCopyRequest = null;
                        RunResourceCopyContinuation(request, selected);
                        return;
                    }
                    // The worker receives only actual paths/explicit decisions, never canonical owners.
                    var cancellation = new CancellationTokenSource(); resourceSourceCopyCancellation = cancellation;
                    resourceCopyRequest = request with { Sources = selected };
                    resourceSourceCopyTask = Task.Run(() => ResourceSourceCopy.Prepare(choices, cancellation.Token), cancellation.Token);
                    BeginAssetImportProgress(false);
                    }
                    catch { StopResourceSourceCopies(); throw; }
                }, StopResourceSourceCopies);
        }
        catch { StopResourceSourceCopies(); throw; }
        return true;
        }
        finally { if (ownsProducer && producer != null) RetireTemplateReferenceLease(producer); }
    }
    private void RunResourceCopyContinuation(ResourceCopyRequest request, string[] files)
    {
        executingResourceCopies.Add(request);
        try { ValidateResourceCopyRequest(request); request.Continue(files); }
        finally { executingResourceCopies.Remove(request); RetireResourceSourceRequest(null, null, request); }
    }
    private void ValidateResourceCopyRequest(ResourceCopyRequest request, bool publication = true)
    {
        if (publication && request.Authority.Canceled) throw new OperationCanceledException("Resource source preparation was canceled.");
        ValidatePickerCreationScope();
        if (!ReferenceEquals(workspace, request.Workspace) || !IsCurrentEditableAssetPackage(request.Package)
            || !SamePath(request.Package.FullPath.ToOSPath(), request.PackagePath)
            || !ResourceDirectories(request.Package).SequenceEqual(request.Resources, StringComparer.OrdinalIgnoreCase)
            || assetBrowser.FindFolder(request.Target.Id) is not { } target || !ReferenceEquals(target.Package, request.Package)
            || target.RelativePath != request.Target.Path || target.IsCode != request.Target.Code)
            throw new InvalidOperationException("The resource copy target changed during import.");
        if (request.Producer is EditorAssetTemplateAdapterLease provider) provider.Validate();
    }
    private void PumpResourceSourceCopies()
    {
        PumpResourceSourceRetirements();
        if (resourceCopyReady is { } ready && assetImportProgressRetired)
        {
            resourceCopyReady = null;
            try { RunResourceCopyContinuation(ready, ready.Sources); }
            catch (Exception error)
            {
                StopResourceSourceCopies();
                var canceled = error is OperationCanceledException && ready.Authority.Canceled;
                CompleteAssetImportProgress(null, canceled ? null : error, canceled);
                if (!canceled) outputLog.Append("Project", LogMessageType.Error, "Asset import", error.Message, error.ToString());
            }
        }
        if (resourceSourceCopyTask is not { IsCompleted: true } task) return;
        var request = resourceCopyRequest; var cancellation = resourceSourceCopyCancellation;
        resourceSourceCopyTask = null; resourceCopyRequest = null; resourceSourceCopyCancellation = null;
        PreparedResourceSourceCopy[]? files = null;
        ResourceSourceCopyOperation? initialOperation = null;
        var ownsResult = true;
        try
        {
            if (task.IsCompletedSuccessfully) files = task.Result;
            if (task.IsCanceled || cancellation?.IsCancellationRequested == true) { CompleteAssetImportProgress(null, null, true); return; }
            files ??= task.GetAwaiter().GetResult();
            if (request == null) throw new InvalidOperationException("The resource copy target is unavailable.");
            ValidateResourceCopyRequest(request);
            if (!CanAddAsset) throw new InvalidOperationException("The asset import target is not available.");
            var certificate = request with { Producer = null, Continue = static _ => { } };
            var operation = initialOperation = new ResourceSourceCopyOperation(files, request.Package.RootDirectory.ToOSPath(), () => ValidateResourceCopyRequest(certificate, false), request.Resources);
            ownsResult = false;
            operation.ApplyInitial(); pendingResourceCopies = operation; pendingResourcePackage = request.Package; initialOperation = null;
            CompleteAssetImportProgress(null, null, false);
            resourceCopyReady = request; request = null;
        }
        catch (Exception error)
        {
            CompleteAssetImportProgress(null, error, false);
            outputLog.Append("Project", LogMessageType.Error, "Asset import", error.Message, error.ToString());
        }
        finally { RetireResourceSourceRequest(task, cancellation, request, operation: initialOperation, ownsResult: ownsResult); }
    }
    private void JoinPendingResourceCopies(Package package)
    {
        if (pendingResourceCopies == null) return;
        if (!ReferenceEquals(pendingResourcePackage, package)) throw new InvalidOperationException("The resource copy publication target changed.");
        var operation = pendingResourceCopies;
        operation.ValidateApplied();
        var state = sessionHistory!.Registry.GetOrCreate(package.FullPath.ToOSPath(), null, () => new PackageMetadataDocument(workspace!, package, sessionHistory));
        using var transaction = sessionHistory.CreateTransaction("Copy resources");
        sessionHistory.PushOperation(operation, state); pendingResourceCopies = null; pendingResourcePackage = null;
    }
    private void CancelPendingResourceCopies()
    {
        var operation = pendingResourceCopies; pendingResourceCopies = null; pendingResourcePackage = null;
        RetireResourceSourceRequest(null, null, null, operation: operation);
    }
    private void StopResourceSourceCopies()
    {
        foreach (var executing in executingResourceCopies) executing.Authority.Canceled = true;
        if (!stoppingAssetImportUpdate && assetImportTask != null) { StopAssetImportUpdate(); return; }
        if (factorySourceTask != null) StopFactorySourceCreation();
        resourceSourceCopyPanel?.Clear();
        var task = resourceSourceCopyTask; var cancellation = resourceSourceCopyCancellation; var request = resourceCopyRequest;
        var ready = resourceCopyReady; resourceCopyReady = null;
        if (request != null) request.Authority.Canceled = true;
        if (ready != null) ready.Authority.Canceled = true;
        resourceSourceCopyTask = null; resourceSourceCopyCancellation = null; resourceCopyRequest = null;
        var copies = pendingResourceCopies; pendingResourceCopies = null; pendingResourcePackage = null;
        var readers = new Task?[] { assetImportTask, factorySourceTask }.OfType<Task>().ToArray();
        RetireResourceSourceRequest(task, cancellation, request, ready, copies, readers: readers, cancel: true);
    }
}
