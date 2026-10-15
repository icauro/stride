using System.Text;
using Stride.Core;
using Stride.Core.Assets;
using Stride.Core.IO;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private AssetItem[] PasteSourceAssets(IReadOnlyList<AssetItem> copied, AssetBrowserFolder folder, IReadOnlySet<string>? reservedLocations = null)
    {
        if (!IsCurrentEditableAssetPackage(folder.Package)) throw new InvalidOperationException("The source paste package is no longer available.");
        if (assetBrowser.FindFolder(folder.Id) is not { } currentFolder || !ReferenceEquals(currentFolder.Package, folder.Package)
            || currentFolder.RelativePath != folder.RelativePath || currentFolder.IsCode != folder.IsCode)
            throw new InvalidOperationException("The source paste folder is no longer available.");
        if (copied.Any(item => item.Asset is not SourceCodeAsset)) throw new InvalidOperationException("The clipboard selection is not native source assets.");
        var package = folder.Package; var history = sessionHistory!;
        var prepared = new List<(AssetItem Item, ProjectBuildSettingsDocument? Project, ScriptSourceTopologyOperation Operation)>();
        var leases = new List<IDisposable>(); var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Apply()
        {
            foreach (var input in copied)
            {
                var source = (SourceCodeAsset)input.Asset;
                var projectSource = source is IProjectAsset;
                var project = projectSource ? package.Container as SolutionProject
                    ?? throw new InvalidOperationException("The source asset requires an actual project.") : null;
                var sourceFolder = projectSource ? package.RootDirectory : package.GetDefaultAssetFolder();
                if (!sourceFolder.IsAbsolute) sourceFolder = UPath.Combine(package.RootDirectory, sourceFolder);
                var root = Path.GetFullPath(sourceFolder.ToOSPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                var projectDocument = project == null ? null : GetProjectBuildSettingsDocument(project, history);
                projectDocument?.ValidateProjectFileBaseline();
                var projectState = project == null ? null : authoring!.Find(project.FullPath.ToOSPath());
                var metadata = PackageMetadataRevision.Prepare(history, workspace!, [package]);
                var relative = input.Location.ToString().Replace('\\', '/');
                if (input.Location.IsAbsolute || relative.Split('/').Any(part => !IsValidAssetLeafName(part))) throw new InvalidOperationException("The clipboard contains an invalid source URL.");
                var extension = AssetRegistry.GetDefaultExtension(source.GetType()) ?? throw new InvalidOperationException("The source serializer has no native extension.");
                var desired = (folder.RelativePath.Length == 0 ? "" : folder.RelativePath.TrimEnd('/') + "/") + relative;
                string SourcePath(string url)
                {
                    var path = Path.GetFullPath(Path.Combine(root, url.Replace('/', Path.DirectorySeparatorChar) + extension));
                    if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("The source path is outside its package.");
                    return path;
                }
                var url = NamingHelper.ComputeNewName(desired, candidate =>
                {
                    var path = SourcePath(candidate.ToString());
                    var location = package.Container?.Qualify(candidate) ?? candidate;
                    return reservedLocations?.Contains(location.ToString()) == true || used.Contains(path) || File.Exists(path) || Directory.Exists(path) || authoring!.Find(path) != null || package.Assets.Find(location) != null;
                });
                var path = SourcePath(url); used.Add(path);
                var directories = new OwnedSourceDirectoriesOperation(package.RootDirectory.ToOSPath());
                try { directories.Ensure(Path.GetDirectoryName(path)!); }
                catch { directories.CancelInitial(); throw; }
                if (directories.HasDirectories)
                {
                    try { history.PushOperation(directories, projectState ?? metadata[0].State); }
                    catch { directories.CancelInitial(); throw; }
                }
                var location = package.Container?.Qualify(new UFile(url)) ?? new UFile(url);
                source.Id = SourceCodeAsset.GenerateIdFromLocation(package.Meta.Name, location.ToOSPath());
                var item = AssetItem.CreateForPackage(package, location, source); item.SourceFolder = sourceFolder;
                var document = new AssetScriptSourceDocument(path, source, source.Text, new UTF8Encoding(false, true), history);
                if (project != null) document.SetProjectOwnership(project.FullPath.ToOSPath(), Path.GetFileName(path), relative.Split('/')[..^1]);
                document.BindSourceAssetItem(item);
                EditorAuthoringState<ScriptSourceDocument> state;
                try { state = authoring!.RegisterUnpublished<ScriptSourceDocument>(document, item.Id); }
                catch { document.Dispose(); throw; }
                leases.Add(state.AcquireView());
                var operation = new ScriptSourceTopologyOperation(this, package, item, state, projectState, true, metadata);
                if (projectSource) projectDocument!.ChangeProjectSourceMembership(item, true);
                operation.ApplyInitial();
                try { history.PushOperation(operation, operation.Participants); }
                catch (Exception failure)
                {
                    try { operation.CancelInitial(); }
                    catch (Exception recovery)
                    {
                        history.RetainFailedInitialOperation(operation, operation.Participants);
                        throw new AggregateException("The source paste could not be recorded or restored.", failure, recovery);
                    }
                    throw;
                }
                prepared.Add((item, projectDocument, operation));
            }
        }
        try
        {
            if (history.IsCapturingOperations) Apply();
            else { using var capture = history.CaptureOperationsAtomic(Apply); capture.Publish(history, "Paste sources"); }
            var browser = AssetCreationBrowser;
            browser.RefreshEntries(workspace!);
            browser.RestoreSelection(prepared.Select(entry => entry.Item.Id), prepared.LastOrDefault().Item?.Id);
            if (ReferenceEquals(browser, assetBrowser)) ActivateAssetPropertyContext();
            return prepared.Select(entry => entry.Item).ToArray();
        }
        finally { foreach (var lease in leases) lease.Dispose(); }
    }
}
