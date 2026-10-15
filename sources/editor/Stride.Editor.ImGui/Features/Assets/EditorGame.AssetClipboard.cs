using Stride.Core.Assets;
using Stride.Assets.Scripts;
using Stride.Core.Assets.Analysis;
using Stride.Core.Assets.Editor.Services;
using Stride.Core.Assets.Quantum;
using Stride.Core.Assets.Serializers;
using Stride.Core.Assets.Tracking;
using Stride.Core.Diagnostics;
using Stride.Core.IO;
using Stride.Core.Serialization.Contents;
using Stride.Core.Yaml;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private bool CanCopySelectedAssetsWithDependencies => workspace != null && authoring != null && assetBrowser.SelectedCount > 0
        && assetBrowser.SelectedEntries.All(entry => CanCopyAssetItem(workspace.FindAsset(entry.Id)));
    private bool CanCopySelectedAssets => assetBrowser.SelectedContentFolders.Count > 0 ? CanCopyAssetContent : workspace != null && authoring != null && assetBrowser.SelectedCount != 0
        && assetBrowser.SelectedEntries.All(entry => CanCopyAssetItem(workspace.FindAsset(entry.Path)));

    private bool CanCopyAssetItem(AssetItem item)
    {
        var asset = authoring?.Find(item.Id)?.Document is IAssetTagDocument document ? document.AuthoredAsset : item.Asset;
        return asset is SourceCodeAsset || asset is Stride.Assets.Scripts.VisualScriptAsset || asset is not IProjectAsset;
    }

    private void CopySelectedAssets(bool includeDependencies)
    {
        if (!includeDependencies && assetBrowser.SelectedContentFolders.Count > 0) { CopyAssetContent(); return; }
        if (includeDependencies ? !CanCopySelectedAssetsWithDependencies : !CanCopySelectedAssets) return;
        CopyAssetEntries(assetBrowser.SelectedEntries.ToArray(), includeDependencies);
    }

    private void CopyAssetEntries(IReadOnlyList<AssetEntry> selection, bool includeDependencies)
    {
        if (workspace == null || authoring == null || selection.Count == 0) return;
        documents.CommitAll();
        var items = selection.Select(entry => workspace.FindAsset(entry.Id)).ToList();
        if (items.Any(item => !CanCopyAssetItem(item))) throw new InvalidOperationException("This asset selection can't be copied.");
        if (includeDependencies)
        {
            // The reference editor includes editable dependencies only; package defaults stay referenced.
            var ids = items.Select(item => item.Id).ToHashSet();
            var dependencySnapshot = workspace!.Session.DependencyManager.CaptureDependencies(documents.AuthoredDocuments
                .OfType<IAssetTagDocument>().ToDictionary(document => document.AuthoredAsset.Id, document => document.AuthoredAsset));
            var pending = new Queue<AssetId>(ids); var visited = new HashSet<AssetId>();
            while (pending.TryDequeue(out var id))
            {
                if (!visited.Add(id)) continue;
                var direct = dependencySnapshot.GetValueOrDefault(id)?.LinksOut
                    .Where(link => (link.Type & ContentLinkType.Reference) != 0).Select(link => link.Item) ?? [];
                foreach (var dependency in direct)
                {
                    // WPF computes the full recursive closure, then includes editable assets.
                    // Read-only intermediates remain referenced but don't stop traversal.
                    pending.Enqueue(dependency.Id);
                    if (!IsEditableAssetPackage(dependency.Package) || !ids.Add(dependency.Id)) continue;
                    if (!CanCopyAssetItem(dependency)) throw new InvalidOperationException("A referenced asset requires its source clipboard workflow.");
                    items.Add(dependency);
                }
            }
        }
        var text = SerializeAssetClipboard(items);
        if (!string.IsNullOrEmpty(text)) Gui.SetClipboardText(text);
    }

    private string SerializeAssetClipboard(IReadOnlyList<AssetItem> items, IReadOnlyDictionary<AssetId, UFile>? locations = null)
    {
        var nodes = new AssetNodeContainer { NodeBuilder = { NodeFactory = new AssetNodeFactory() } };
        var container = new AssetPropertyGraphContainer(nodes) { PropagateChangesFromBase = false };
        var graphs = new List<AssetPropertyGraph>();
        var copied = new List<AssetItem>();
        var log = new LoggerResult();
        try
        {
            foreach (var item in items)
            {
                if (!CanCopyAssetItem(item)) throw new InvalidOperationException("This asset requires its source clipboard workflow.");
                var owner = authoring!.Find(item.Id)?.Document;
                if (item.Asset is SourceCodeAsset sourceCode)
                {
                    var clone = AssetCloner.Clone(sourceCode);
                    if (owner is ScriptSourceDocument authoredSource) { authoredSource.CommitEdits(); clone.Text = authoredSource.Text; }
                    else clone.Text = sourceCode.Text;
                    var sourceItem = new AssetItem(locations?.GetValueOrDefault(item.Id) ?? new UFile(item.UnqualifiedUrl.GetFileNameWithoutExtension()), clone);
                    item.YamlMetadata.CopyInto(sourceItem.YamlMetadata); copied.Add(sourceItem);
                    continue;
                }
                var assetNamespace = item.Package.Container?.AssetNamespace;
                byte[] yaml;
                switch (owner)
                {
                    case EntityHierarchyDocument hierarchy:
                        var hierarchySnapshot = hierarchy.CaptureAssemblyReload();
                        yaml = hierarchySnapshot.Yaml; assetNamespace = hierarchySnapshot.Namespace; break;
                    case ProceduralAssetDocument procedural:
                        var proceduralSnapshot = procedural.CaptureAssemblyReload(item);
                        yaml = proceduralSnapshot.Yaml; assetNamespace = proceduralSnapshot.Namespace; break;
                    case AssetMetadataDocument metadata:
                        var metadataSnapshot = metadata.CaptureAssemblyReload(item);
                        yaml = metadataSnapshot.Yaml; assetNamespace = metadataSnapshot.Namespace; break;
                    case null:
                        using (var stream = new MemoryStream())
                        {
                            AssetFileSerializer.Save(stream, item.Asset, item.YamlMetadata, assetNamespace: assetNamespace);
                            yaml = stream.ToArray();
                        }
                        break;
                    default: throw new InvalidOperationException("This asset has an unsupported clipboard document.");
                }
                using var serialized = new MemoryStream(yaml, false);
                var snapshot = AssetFileSerializer.Load<Asset>(serialized, new UFile(item.FullPath.ToOSPath()), log, assetNamespace);
                if (log.HasErrors || snapshot.Asset.Id != item.Id) throw new InvalidOperationException("The asset clipboard snapshot could not be restored.");
                SourceHashesHelper.ConvertPaths(snapshot.Asset, new UDirectory(Path.GetDirectoryName(item.FullPath.ToOSPath())!), UPathType.Absolute);
                // Copy commands flatten directly selected assets, as AssetCollectionViewModel does.
                var clipboardItem = new AssetItem(locations?.GetValueOrDefault(item.Id) ?? new UFile(item.UnqualifiedUrl.GetFileNameWithoutExtension()), snapshot.Asset);
                snapshot.YamlMetadata.CopyInto(clipboardItem.YamlMetadata);
                foreach (var link in AssetReferenceAnalysis.Visit(snapshot.Asset))
                    if (link.Reference is UPath source && !source.IsAbsolute && !string.IsNullOrEmpty(source.FullPath)
                        && link.Path.GetCustomAttribute<UPathAttribute>()?.RelativeTo != UPathRelativeTo.None)
                        link.UpdateReference(null, Path.GetFullPath(source.ToOSPath(), Path.GetDirectoryName(item.FullPath.ToOSPath())!).Replace('\\', '/'));
                AssetQuantumRegistry.RegisterAssembly(snapshot.Asset.GetType().Assembly);
                var graph = container.InitializeAsset(clipboardItem, log)
                    ?? throw new InvalidOperationException("This asset has no clipboard Quantum graph.");
                graphs.Add(graph); copied.Add(clipboardItem);
            }
            var settings = AssetYamlSerializer.Default.GetSerializerSettings();
            settings.RegisterTagMapping("!CopyPasteData", typeof(CopyPasteData));
            settings.RegisterTagMapping("!CopyPasteItem", typeof(CopyPasteItem));
            return new CopyPasteService(container).CopyMultipleAssets(copied);
        }
        finally { foreach (var graph in graphs) graph.Dispose(); }
    }
}
