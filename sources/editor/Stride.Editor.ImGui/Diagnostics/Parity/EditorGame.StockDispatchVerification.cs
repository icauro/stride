using System.Security.Cryptography;
using System.Text;
using Stride.Assets.Scripts;
using Stride.Core.Assets;
using Stride.Core.IO;

namespace Stride.Editor.ImGui;

internal sealed record StockScriptDispatchState(EditorDocumentEntry Entry, ScriptSourceDocument Document,
    EditorAuthoringState State, string AssetId, string ProjectPath, string Text, bool Dirty,
    EditorHistorySnapshot History, string DiskSha256);

internal sealed partial class EditorGame
{
    private string? stockDispatchSourcePath;
    // Arrangement only. A genuine included source is discovered by the normal
    // native package/project load; no document factory substitutes for dispatch.
    internal void PrepareStockDispatchFixtureBeforeLoad()
    {
        if (!args.Contains("--ui-test") || workspace != null || stockDispatchSourcePath != null)
            throw new InvalidOperationException("Stock dispatch preparation requires one unloaded disposable test project.");
        var package = Path.GetFullPath(projectPathInput);
        var privateRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Stride-ImGui-UI")) + Path.DirectorySeparatorChar;
        if (!package.StartsWith(privateRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Stock dispatch source must belong to the disposable private fixture.");
        stockDispatchSourcePath = Path.Combine(Path.GetDirectoryName(package)!, "StockDispatchSource.cs");
        if (File.Exists(stockDispatchSourcePath)) throw new IOException("Stock dispatch source destination already exists.");
        File.WriteAllText(stockDispatchSourcePath, "namespace EditorParity { internal static class StockDispatchSource { } }\n", new UTF8Encoding(false));
        PrepareStockAssetDispatchFixtures(package);
    }
    internal (string Path, string Control, string[] Locations) InspectStockScriptBrowserNavigation()
    {
        var current = workspace ?? throw new InvalidOperationException("Stock dispatch project is not loaded.");
        var target = current.Assets.Single(asset => stockDispatchSourcePath != null && SamePath(asset.Path, stockDispatchSourcePath));
        var item = current.FindAsset(target.Id);
        if (item.Asset is not ScriptSourceFileAsset || item.Package.Container is not SolutionProject project
            || !current.Session.Projects.Contains(project))
            throw new InvalidOperationException("Stock fixture must be an actual native ScriptSourceFileAsset owned by the loaded project.");
        using var evaluated = new AuthoredProjectProperties(File.ReadAllBytes(project.FullPath.ToOSPath()), project.FullPath.ToOSPath());
        var contexts = evaluated.EvaluateDependencyContexts("Debug");
        if (contexts.Length == 0 || contexts.Any(context => !context.CompilePaths.Any(path => SamePath(path, target.Path))))
            throw new InvalidOperationException("Stock source must be a real evaluated native Compile item in every target framework.");
        var root = assetBrowser.Packages.Single(folder => ReferenceEquals(folder.Package, item.Package));
        var folder = root.Children.Single(child => child.IsCode);
        var locations = new List<string> { root.Id, folder.Id };
        var url = target.Name.Replace('\\', '/'); var slash = url.LastIndexOf('/');
        if (slash >= 0)
            foreach (var part in url[..slash].Split('/', StringSplitOptions.RemoveEmptyEntries))
            { folder = folder.Children.Single(child => string.Equals(child.Name, part, StringComparison.OrdinalIgnoreCase)); locations.Add(folder.Id); }
        return (target.Path, "asset/" + target.Name, locations.ToArray());
    }
    internal StockScriptDispatchState? InspectStockScriptDispatchState()
    {
        if (stockDispatchSourcePath == null || documents.Find(stockDispatchSourcePath) is not { } entry) return null;
        var current = workspace ?? throw new InvalidOperationException("Stock source lost its loaded workspace.");
        var item = current.AssetItems.Single(asset => SamePath(asset.FullPath.ToOSPath(), stockDispatchSourcePath));
        if (item.Asset is not ScriptSourceFileAsset || item.Package.Container is not SolutionProject project
            || entry.Document is not AssetScriptSourceDocument document || entry.State is not { IsPublished: true, IsDisposed: false } state
            || !ReferenceEquals(authoring?.Find(item.Id), state) || document.ProjectPath == null
            || !SamePath(document.ProjectPath, project.FullPath.ToOSPath()) || !ReferenceEquals(document.SessionHistory, sessionHistory)
            || !document.CanSave || !document.CanEditText || document.GeneratedByAssetId != null
            || documents.Entries.Count(owner => SamePath(owner.Path, stockDispatchSourcePath)) != 1)
            throw new InvalidOperationException("Default browser source dispatch lost native asset/project/canonical/history/editing ownership.");
        return new(entry, document, state, item.Id.ToString(), project.FullPath.ToOSPath(), document.Text,
            document.IsDirty, document.History, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(stockDispatchSourcePath))));
    }
    internal bool StockScriptDispatchLocationSelected(string id)
        => string.Equals(assetBrowser.SelectedFolder?.Id, id, StringComparison.OrdinalIgnoreCase);
    internal (string? PressedId, int PressedFrame, string? AcceptedId, int AcceptedFrame, int CanceledFrame) InspectStockFolderPointer()
        => SolutionExplorerController.InspectPointerAcceptance();
    internal bool StockDispatchFiltersEmpty => assetBrowser.CaptureFilters().Length == 0 && AssetBrowserPanel.SearchText.Length == 0;
    internal object InspectStockDispatchBrowserState(string path) => new
    {
        draft = AssetBrowserPanel.SearchText, filters = assetBrowser.CaptureFilters(),
        selectedFolder = assetBrowser.SelectedFolder?.Id,
        target = workspace?.Assets.Where(asset => SamePath(asset.Path, path)).Select(asset => new { asset.Name, asset.Path, type = asset.AssetType.FullName }).ToArray(),
        visible = assetBrowser.Visible.Take(32).Select(asset => new { asset.Name, asset.Path }).ToArray(),
        clearControlVisible = Controls.Centers.ContainsKey("asset/filter/clear")
    };
    internal bool StockScriptDispatchClosed => stockDispatchSourcePath != null && documents.Find(stockDispatchSourcePath) == null;
    internal unsafe (Guid EntryId, bool InputActive, System.Numerics.Vector2? Center)? InspectStockScriptInput()
    {
        if (!args.Contains("--ui-test") || stockDispatchSourcePath == null)
            throw new InvalidOperationException("Stock source geometry requires its actual disposable diagnostic fixture.");
        var entry = documents.Find(stockDispatchSourcePath);
        if (entry?.Document is not ScriptSourceDocument source) return null;
        var group = DocumentGroupPanel.Model.Find(entry);
        if (group == null || !scriptEditors.TryGetValue(source, out var editor)) return (entry.Id, false, null);
        var window = Hexa.NET.ImGui.ImGuiP.FindWindowByName(group.WindowName);
        var center = CaptureScriptPresentationAuthority(source)() && !window.IsNull
            ? editor.InspectRecoverySourceSurface(window.ID) : null;
        return (entry.Id, editor.InputActive, center);
    }
}
