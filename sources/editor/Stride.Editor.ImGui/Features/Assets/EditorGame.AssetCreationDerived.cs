using System.Reflection;
using Stride.Core;
using Stride.Core.Assets;
using Stride.Core.IO;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private bool CanCreateDerivedAsset(AssetEntry entry) => workspace != null && authoring != null && sessionHistory != null
        && entry.AssetType.GetCustomAttribute<AssetDescriptionAttribute>()?.AllowArchetype == true
        && FindDerivedAssetTarget(workspace.FindAsset(entry.Id)) != null
        && projectLoading == null && assetPreviewBuild == null && !IsAuthoringPipelineUpdating && !IsUserProjectBuilding && !IsAssetImportUpdating
        && !closeRequested && !closePromptOpen && closingDocument == null;

    private AssetBrowserFolder? FindDerivedAssetTarget(AssetItem source)
    {
        var code = source.Asset is IProjectAsset;
        var originalRoot = assetBrowser.Packages.FirstOrDefault(root => ReferenceEquals(root.Package, source.Package));
        if (IsEditableAssetPackage(source.Package))
        {
            var mount = originalRoot?.Children.FirstOrDefault(folder => folder.IsCode == code);
            if (mount != null)
            {
                var url = source.UnqualifiedUrl.ToString(); var slash = url.LastIndexOf('/');
                return slash < 0 ? mount : assetBrowser.FindFolder(mount.Id + "/" + url[..slash]);
            }
        }
        var currentRoot = assetBrowser.Packages.FirstOrDefault(root => ReferenceEquals(root.Package, workspace!.Package));
        if (currentRoot != null && IsEditableAssetPackage(currentRoot.Package))
            return currentRoot.Children.FirstOrDefault(mount => mount.IsCode == code);
        return originalRoot?.Children.FirstOrDefault(mount => mount.IsCode == code && IsEditableAssetPackage(mount.Package));
    }

    private bool CanCreateReplacingAsset(AssetEntry entry) => CanCreateDerivedAsset(entry) && !typeof(SourceCodeAsset).IsAssignableFrom(entry.AssetType);

    private void CreateDerivedAsset(AssetEntry entry) => CreateChildAsset(entry, false);
    private void CreateReplacingAsset(AssetEntry entry) => CreateChildAsset(entry, true);

    private void CreateChildAsset(AssetEntry entry, bool replaces)
    {
        if (!(replaces ? CanCreateReplacingAsset(entry) : CanCreateDerivedAsset(entry))) return;
        documents.CommitAll();
        var original = workspace!.FindAsset(entry.Id);
        var folder = FindDerivedAssetTarget(original) ?? throw new InvalidOperationException("This asset has no valid creation location.");
        var owner = GetAssetTagDocument(entry, false); owner.CommitEdits();
        if (owner is IEditorAssetGraphDocument canonical) _ = canonical.AuthoringGraph;
        var state = authoring!.Find(entry.Id) ?? throw new InvalidOperationException("The base asset has no canonical authored owner.");
        using var retention = state.AcquireView();
        var authored = original.Clone(true, newAsset: owner.AuthoredAsset);
        var derived = authored.CreateDerivedAsset();
        if (replaces) derived.Replaces = authored.ToReference();
        var title = replaces ? "Create replacing asset" : "Create derived asset";
        var package = folder.Package;
        var sourceFolder = derived is IProjectAsset ? package.RootDirectory : package.GetDefaultAssetFolder();
        var absoluteFolder = sourceFolder.IsAbsolute ? sourceFolder : UPath.Combine(package.RootDirectory, sourceFolder);
        var prefix = folder.RelativePath.Length == 0 ? "" : folder.RelativePath.TrimEnd('/') + "/";
        var extension = AssetRegistry.GetDefaultExtension(derived.GetType());
        string Destination(string name) => Path.GetFullPath(Path.Combine(absoluteFolder.ToOSPath(), (prefix + name + extension).Replace('/', Path.DirectorySeparatorChar)));
        var name = NamingHelper.ComputeNewName(Path.GetFileName(original.UnqualifiedUrl.ToString()) + (replaces ? "" : "-Derived"), leaf =>
        {
            var location = package.Container?.Qualify(new UFile(prefix + leaf)) ?? new UFile(prefix + leaf);
            var path = Destination(leaf.ToString());
            return package.Assets.Any(item => item.Location.ToString().Equals(location.ToString(), StringComparison.OrdinalIgnoreCase))
                || File.Exists(path) || Directory.Exists(path) || authoring.Find(path) != null;
        });
        var path = Destination(name);
        var url = new UFile(prefix + name);
        var item = new AssetItem(package.Container?.Qualify(url) ?? url, derived) { SourceFolder = sourceFolder };
        using var capture = sessionHistory!.CaptureOperationsAtomic(() =>
        {
            CreatePreparedAsset(package, item, path, title);
            var child = authoring.Find(item.Id)?.Document as IAssetReferenceDocument
                ?? throw new InvalidOperationException("The derived asset has no canonical authored graph.");
            if (child is IEditorAssetGraphDocument childGraph)
            {
                var current = workspace!.FindAsset(item.Id);
                var currentState = authoring.Find(item.Id);
                if (!ReferenceEquals(current, item) || !ReferenceEquals(current.Package, package)
                    || currentState is not { IsPublished: true, IsDisposed: false } || !ReferenceEquals(currentState.Document, child))
                    throw new InvalidOperationException("The derived asset lost its actual published package identity.");
                // The graph is constructed before package publication. Certify its
                // actual namespace/location now, before the first native save or
                // link replay; later graph refresh must not change serialization.
                _ = childGraph.AuthoringGraph;
                childGraph.UpdateAuthoringAssetIdentity(current);
            }
            // Reconcile first, then rebase through the actual override-aware child journal.
            var edit = child.PrepareReferenceLocations(new Dictionary<AssetId, string>(), original.FullPath.ToOSPath(), path);
            if (edit != null)
            {
                edit.Apply();
                try { edit.Commit(); } catch { edit.Restore(); throw; }
            }
        });
        capture.Publish(sessionHistory, title);
    }
}
