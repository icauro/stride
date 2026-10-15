using Stride.Assets.Entities;
using Stride.Core;
using Stride.Core.Assets;
using Stride.Core.Assets.Analysis;
using Stride.Core.Assets.Editor.Services;
using Stride.Core.Assets.Quantum;
using Stride.Core.Assets.Tracking;
using Stride.Core.Assets.Yaml;
using Stride.Core.Diagnostics;
using Stride.Core.IO;
using Stride.Core.Serialization.Contents;
using Stride.Core.Yaml;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private CopyPasteService CreateAssetPasteService(AssetPropertyGraphContainer container)
    {
        var settings = AssetYamlSerializer.Default.GetSerializerSettings();
        settings.RegisterTagMapping("!CopyPasteData", typeof(CopyPasteData));
        settings.RegisterTagMapping("!CopyPasteItem", typeof(CopyPasteItem));
        var service = new CopyPasteService(container);
        service.RegisterProcessor(new AssetScenePostPasteProcessor());
        service.RegisterProcessor(new AssetClipboardPasteProcessor(id => workspace!.Session.FindAsset(id) != null
            || authoring!.Find(id) != null, service.PostProcessors));
        return service;
    }

    private AssetBrowserFolder? DefaultAssetPasteFolder => assetBrowser.SelectedCount == 0
        && assetBrowser.SelectedContentFolders is { Count: 1 } folders ? folders[0] : assetBrowser.SelectedFolder;

    private bool CanPasteAssets(AssetBrowserFolder? target = null)
    {
        if (target == null && assetBrowser.CaptureNavigationScope() is { } scope && (scope.EntireWorkspace || scope.Folders.Length != 1)) return false;
        target ??= DefaultAssetPasteFolder;
        if (workspace == null || authoring == null || sessionHistory == null || target == null
            || !IsCurrentEditableAssetPackage(target.Package) || projectLoading != null
            || assetPreviewBuild != null || IsAuthoringPipelineUpdating || IsUserProjectBuilding || IsAssetImportUpdating
            || RecoveryTransitionBusy || editorExtensionsStopping || closeRequested || closePromptOpen || closingDocument != null) return false;
        if (assetBrowser.FindFolder(target.Id) is not { } currentTarget || !ReferenceEquals(currentTarget.Package, target.Package)
            || currentTarget.RelativePath != target.RelativePath || currentTarget.IsCode != target.IsCode) return false;
        var text = Gui.GetClipboardTextS() ?? "";
        if (text.Length == 0 || text.Length > 16 * 1024 * 1024) return false;
        try
        {
            var container = new AssetPropertyGraphContainer(new AssetNodeContainer { NodeBuilder = { NodeFactory = new AssetNodeFactory() } });
            if (!AssetFolderClipboardMetadata.TryRead(text, out var yaml, out var folders) || folders?.HasCode == true && target.Package.Container is not SolutionProject) return false;
            return CreateAssetPasteService(container).CanPaste(yaml, typeof(List<AssetItem>), typeof(List<AssetItem>), typeof(List<AssetItem>));
        }
        catch (SystemException) { return false; }
    }

    private void PasteAssets()
    {
        if (!CanPasteAssets()) return;
        documents.CommitAll();
        var folder = DefaultAssetPasteFolder!;
        var package = folder.Package;
        AssetBrowserFolder TargetFolder(bool code)
        {
            if (code && package.Container is not SolutionProject) throw new InvalidOperationException("Code assets require an actual project.");
            if (folder.Parent != null && folder.IsCode == code) return folder;
            return assetBrowser.Packages.First(root => ReferenceEquals(root.Package, package)).Children.First(mount => mount.IsCode == code);
        }
        UDirectory SourceFolder(bool code) => code ? package.RootDirectory : package.GetDefaultAssetFolder();
        string TargetRoot(bool code)
        {
            var configured = SourceFolder(code);
            var absolute = configured.IsAbsolute ? configured : UPath.Combine(package.RootDirectory, configured);
            return Path.GetFullPath(absolute.ToOSPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        }
        string Prefix(bool code) => TargetFolder(code).RelativePath.Length == 0 ? "" : TargetFolder(code).RelativePath.TrimEnd('/') + "/";
        var assetNamespace = package.Container?.AssetNamespace ?? package.AssetNamespace;
        string Unqualified(UFile location)
        {
            var url = location.ToString();
            var prefix = assetNamespace == null ? null : "/" + assetNamespace + "/";
            if (prefix != null && url.StartsWith(prefix, StringComparison.Ordinal)) return url[prefix.Length..];
            if (location.IsAbsolute) throw new InvalidOperationException("The clipboard asset URL belongs to another namespace.");
            return url;
        }
        string Destination(UFile location, string extension, bool code)
        {
            var targetRoot = TargetRoot(code);
            var path = Path.GetFullPath(Path.Combine(targetRoot, Unqualified(location).Replace('/', Path.DirectorySeparatorChar) + extension));
            if (!path.StartsWith(targetRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("The asset clipboard path is outside its target folder.");
            return path;
        }
        var container = new AssetPropertyGraphContainer(new AssetNodeContainer { NodeBuilder = { NodeFactory = new AssetNodeFactory() } })
            { PropagateChangesFromBase = false };
        var service = CreateAssetPasteService(container);
        if (!AssetFolderClipboardMetadata.TryRead(Gui.GetClipboardTextS() ?? "", out var yaml, out var copiedFolders)) return;
        if (copiedFolders?.HasCode == true && package.Container is not SolutionProject)
            throw new InvalidOperationException("Code folders require an actual project.");
        var result = service.DeserializeCopiedData(yaml, new List<AssetItem>(), typeof(List<AssetItem>));
        if (result.Items.Count != 1 || result.Items[0].Data is not List<AssetItem> copied) return;
        if (copied.Any(item => item.Asset is IProjectAsset) && package.Container is not SolutionProject)
            throw new InvalidOperationException("Project source assets require an actual project.");
        var log = new LoggerResult();
        var folderNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var usedFolderNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string RenameFolderPath(string relative, bool code)
        {
            var slash = relative.IndexOf('/');
            var originalFolder = slash < 0 ? relative : relative[..slash];
            var key = (code ? "code/" : "assets/") + originalFolder;
            if (!folderNames.TryGetValue(key, out var renamedFolder))
            {
                renamedFolder = NamingHelper.ComputeNewName(originalFolder, candidate =>
                {
                    var name = candidate.ToString();
                    var relativeFolder = Prefix(code) + name;
                    return usedFolderNames.Contains((code ? "code/" : "assets/") + name) || TargetFolder(code).Children.Any(child => child.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                        || !code && package.ExplicitFolders.Any(existing => existing.ToString().Equals(relativeFolder, StringComparison.OrdinalIgnoreCase))
                        || Directory.Exists(Path.Combine(TargetRoot(code), relativeFolder.Replace('/', Path.DirectorySeparatorChar)));
                }, "{0} ({1})");
                folderNames.Add(key, renamedFolder); usedFolderNames.Add((code ? "code/" : "assets/") + renamedFolder);
            }
            return renamedFolder + (slash < 0 ? "" : relative[slash..]);
        }
        string[] FolderPaths(bool code) => copiedFolders?.ForMount(code).Select(path => Prefix(code) + RenameFolderPath(path, code)).ToArray() ?? [];
        var assetFolderPaths = FolderPaths(false); var codeFolderPaths = FolderPaths(true);
        void PasteFolders()
        {
            if (assetFolderPaths.Length != 0) PasteClipboardFolders(package, assetFolderPaths, false);
            if (codeFolderPaths.Length != 0) PasteClipboardFolders(package, codeFolderPaths, true);
        }
        for (var index = 0; index < copied.Count; index++)
            if (copied[index].Asset is SourceCodeAsset && copied[index].Location.ToString().Contains('/'))
                copied[index] = copied[index].Clone(false, new UFile(RenameFolderPath(copied[index].Location.ToString(), copied[index].Asset is IProjectAsset)), copied[index].Asset);
        AssetItem[] PasteSources(IReadOnlyList<AssetItem> sources, IReadOnlySet<string>? reserved = null)
        {
            var published = new Dictionary<AssetItem, AssetItem>();
            foreach (var group in sources.GroupBy(item => item.Asset is IProjectAsset))
            {
                var originals = group.ToArray();
                var results = PasteSourceAssets(originals, TargetFolder(group.Key), reserved);
                for (var index = 0; index < originals.Length; index++) published.Add(originals[index], results[index]);
            }
            return sources.Select(item => published[item]).ToArray();
        }
        if (copied.Count == 0 || copied.All(item => item.Asset is SourceCodeAsset))
        {
            if (copied.Count == 0 && assetFolderPaths.Length + codeFolderPaths.Length == 0) return;
            using var capture = sessionHistory!.CaptureOperationsAtomic(() =>
            {
                PasteFolders();
                if (copied.Count != 0)
                {
                    var sources = PasteSources(copied);
                    assetBrowser.RestoreSelection(sources.Select(item => item.Id), sources.LastOrDefault()?.Id);
                    ActivateAssetPropertyContext();
                }
            });
            capture.Publish(sessionHistory, "Paste assets"); assetBrowser.RefreshFolders(workspace!); return;
        }
        var copiedSources = copied.Where(item => item.Asset is SourceCodeAsset).ToArray();
        var sourceIds = copiedSources.Select(item => item.Id).ToArray();
        copied = copied.Where(item => item.Asset is not SourceCodeAsset).ToList();
        if (copied.Any(item => item.Asset is IProjectAsset && item.Asset is not Stride.Assets.Scripts.VisualScriptAsset))
            throw new InvalidOperationException("This selection requires its source project clipboard workflow.");
        for (var index = 0; index < copied.Count; index++)
        {
            var item = copied[index];
            var relative = item.Location.ToString().Replace('\\', '/');
            if (item.Location.IsAbsolute || relative.Split('/').Any(part => !IsValidAssetLeafName(part)))
                throw new InvalidOperationException("The clipboard contains an invalid asset URL.");
            if (relative.Contains('/')) relative = RenameFolderPath(relative, item.Asset is IProjectAsset);
            var url = new UFile(Prefix(item.Asset is IProjectAsset) + relative);
            item = item.Clone(false, package.Container?.Qualify(url) ?? url, item.Asset);
            copied[index] = item;
            item.SourceFolder = SourceFolder(item.Asset is IProjectAsset);
            // DeserializeCopiedData has applied remapped override paths to these exact native nodes.
            var node = (IAssetNode)container.NodeContainer.GetOrCreateNode(item.Asset);
            item.YamlMetadata.AttachMetadata(AssetObjectSerializerBackend.OverrideDictionaryKey,
                AssetPropertyGraph.GenerateOverridesForSerialization(node));
        }
        var extensions = copied.Select(item => (Extension: AssetRegistry.GetDefaultExtension(item.Asset.GetType()), Code: item.Asset is IProjectAsset)).Distinct().ToArray();
        var resolver = new AssetResolver(location => package.Assets.Find(location) != null || extensions.Any(extension =>
        {
            var path = Destination(location, extension.Extension, extension.Code);
            return File.Exists(path) || Directory.Exists(path) || authoring!.Find(path) != null;
        }), id => workspace!.Session.FindAsset(id) != null || authoring!.Find(id) != null);
        var prepared = new List<AssetItem>();
        AssetCollision.Clean(null!, copied, prepared, resolver, false, false);
        var graphs = new List<AssetPropertyGraph>();
        try
        {
            foreach (var item in prepared)
            {
                var path = Destination(item.Location, AssetRegistry.GetDefaultExtension(item.Asset.GetType()), item.Asset is IProjectAsset);
                var targetDirectory = new UDirectory(Path.GetDirectoryName(path)!);
                SourceHashesHelper.ConvertPaths(item.Asset, targetDirectory, UPathType.Absolute);
                SourceHashesHelper.ConvertPaths(item.Asset, targetDirectory, UPathType.Relative);
                foreach (var link in AssetReferenceAnalysis.Visit(item.Asset))
                    if (link.Reference is UPath source && source.IsAbsolute && link.Path.GetCustomAttribute<UPathAttribute>()?.RelativeTo != UPathRelativeTo.None)
                        link.UpdateReference(null, Path.GetRelativePath(Path.GetDirectoryName(path)!, source.ToOSPath()).Replace('\\', '/'));
                AssetQuantumRegistry.RegisterAssembly(item.Asset.GetType().Assembly);
                var graph = container.InitializeAsset(item, log) ?? throw new InvalidOperationException("The pasted asset has no Quantum graph.");
                graphs.Add(graph); graph.PrepareForSave(log, item);
                if (log.HasErrors) throw new InvalidOperationException(log.ToText());
            }
            var previousSelection = assetBrowser.CaptureSelection();
            var previousPrimary = assetBrowser.PrimaryEntry?.Id;
            var locations = prepared.ToDictionary(item => item.Id, item => item.Location.ToString());
            var referenceEdits = new List<PreparedAssetReferenceEdit>();
            foreach (var existing in workspace!.AssetItems)
            {
                var retained = authoring!.Find(existing.Id)?.Document as IAssetReferenceDocument;
                var current = retained?.AuthoredAsset ?? existing.Asset;
                if (!AssetReferenceAnalysis.Visit(current).Any(link => link.Reference is IReference reference
                    && locations.TryGetValue(reference.Id, out var location) && reference.Location != location)) continue;
                if (existing.Package.IsReadOnly) throw new InvalidOperationException("An asset referencing the pasted selection can't be edited.");
                var entry = workspace.Assets.FirstOrDefault(entry => entry.Id == existing.Id)
                    ?? throw new InvalidOperationException("An asset referencing the pasted selection isn't available.");
                var consumer = retained ?? (IAssetReferenceDocument)GetAssetTagDocument(entry, false);
                consumer.CommitEdits();
                var edit = consumer.PrepareReferenceLocations(locations);
                if (edit != null) referenceEdits.Add(edit);
            }
            try
            {
                AssetItem[] pastedSources = [];
                using var capture = sessionHistory!.CaptureOperationsAtomic(() =>
                {
                    PasteFolders();
                    if (copiedSources.Length != 0)
                    {
                        pastedSources = PasteSources(copiedSources, prepared.Select(item => item.Location.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase));
                        var remapping = sourceIds.Select((id, index) => (id, item: pastedSources[index])).ToDictionary(pair => pair.id, pair => pair.item);
                        foreach (var item in prepared)
                        {
                            foreach (var link in AssetReferenceAnalysis.Visit(item.Asset))
                                if (link.Reference is IReference reference && remapping.TryGetValue(reference.Id, out var target))
                                    link.UpdateReference(target.Id, target.Location.ToString());
                            container.TryGetGraph(item.Id)!.PrepareForSave(log, item);
                            if (log.HasErrors) throw new InvalidOperationException(log.ToText());
                        }
                    }
                    foreach (var item in prepared)
                        CreatePreparedAsset(package, item, Destination(item.Location, AssetRegistry.GetDefaultExtension(item.Asset.GetType()), item.Asset is IProjectAsset), "Paste assets");
                    foreach (var edit in referenceEdits)
                    {
                        edit.Apply();
                        try { edit.Commit(); }
                        catch { edit.Restore(); throw; }
                    }
                });
                capture.Publish(sessionHistory, "Paste assets");
                assetBrowser.RefreshEntries(workspace!);
                assetBrowser.RestoreSelection(prepared.Select(item => item.Id).Concat(pastedSources.Select(item => item.Id)), prepared[^1].Id);
            }
            catch
            {
                assetBrowser.RefreshEntries(workspace!); assetBrowser.RestoreSelection(previousSelection, previousPrimary);
                ActivateAssetPropertyContext(); throw;
            }
            assetBrowser.RefreshEntries(workspace!);
            ActivateAssetPropertyContext();
        }
        finally { foreach (var graph in graphs) graph.Dispose(); }
    }

    private sealed class AssetScenePostPasteProcessor : IAssetPostPasteProcessor
    {
        public bool Accept(Type type) => typeof(SceneAsset).IsAssignableFrom(type);
        public void PostPasteDeserialization(Asset asset)
        {
            var scene = (SceneAsset)asset; scene.Parent = null; scene.ChildrenIds.Clear();
        }
    }
}
