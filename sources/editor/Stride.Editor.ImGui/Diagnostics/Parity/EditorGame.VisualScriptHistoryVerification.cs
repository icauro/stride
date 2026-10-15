using System.Security.Cryptography;
using Stride.Assets.Scripts;
using Stride.Core.Assets;
using Stride.Core.IO;

namespace Stride.Editor.ImGui;

internal sealed record VisualScriptHistoryNativeState(EditorDocumentEntry ProducerEntry, VisualScriptDocument Producer,
    EditorAuthoringState ProducerState, ScriptSourceDocument Generated, EditorAuthoringState GeneratedState,
    string CompilerText, int HistoryPosition, Guid[] TransactionIds, Guid[][] ParticipantGenerations);

internal sealed partial class EditorGame
{
    private string? visualScriptHistoryFixturePath;
    private string? visualScriptHistoryProjectPath, visualScriptHistoryGeneratedPath;
    private EditorDocumentEntry? visualScriptHistoryPreviousEntry;
    private readonly Dictionary<string, string> visualScriptHistoryDiskBaselines = new(StringComparer.OrdinalIgnoreCase);
    private object? visualScriptHistoryAddPropertyUi;
    private readonly List<object> visualScriptHistoryAddPropertyEvents = [];
    private (uint Item, uint Hovered, uint Active, bool Pressed, bool Down, bool Released)? visualScriptHistoryLastButtonState;
    private int visualScriptHistoryLastAcceptedFrame = -1;
    private uint visualScriptHistoryButtonId;
    private System.Numerics.Vector2 visualScriptHistoryButtonMinimum, visualScriptHistoryButtonMaximum;

    private void RecordVisualScriptHistoryAddPropertyUi(VisualScriptDocument document, bool pressed)
    {
        if (!Controls.Enabled || visualScriptHistoryFixturePath == null || !SamePath(document.Path, visualScriptHistoryFixturePath)) return;
        var context = Hexa.NET.ImGui.ImGui.GetCurrentContext();
        var io = Hexa.NET.ImGui.ImGui.GetIO();
        var window = Hexa.NET.ImGui.ImGuiP.GetCurrentWindowRead();
        var minimum = Hexa.NET.ImGui.ImGui.GetItemRectMin(); var maximum = Hexa.NET.ImGui.ImGui.GetItemRectMax();
        visualScriptHistoryButtonId = Hexa.NET.ImGui.ImGui.GetItemID();
        visualScriptHistoryButtonMinimum = minimum; visualScriptHistoryButtonMaximum = maximum;
        if (pressed) visualScriptHistoryLastAcceptedFrame = Hexa.NET.ImGui.ImGui.GetFrameCount();
        var released = Hexa.NET.ImGui.ImGui.IsMouseReleased(Hexa.NET.ImGui.ImGuiMouseButton.Left);
        visualScriptHistoryAddPropertyUi = new { frame = Hexa.NET.ImGui.ImGui.GetFrameCount(), itemId = Hexa.NET.ImGui.ImGui.GetItemID(), pressed,
            hovered = Hexa.NET.ImGui.ImGui.IsItemHovered(), clicked = Hexa.NET.ImGui.ImGui.IsItemClicked(Hexa.NET.ImGui.ImGuiMouseButton.Left),
            mouseDown = io.MouseDown[0], mouseReleased = released, clickCount = Hexa.NET.ImGui.ImGui.GetMouseClickedCount(Hexa.NET.ImGui.ImGuiMouseButton.Left),
            mouse = new { io.MousePos.X, io.MousePos.Y }, minimum = new { minimum.X, minimum.Y }, maximum = new { maximum.X, maximum.Y },
            context.ActiveId, context.HoveredId, windowId = window.ID,
            hoveredWindowId = context.HoveredWindow.IsNull ? 0 : context.HoveredWindow.ID,
            document.CanSave, properties = document.Asset.Properties.Count,
            quiescent = DocumentGroupParityCaseQuiescent, canonicalActiveId = documents.Active?.Id,
            history = sessionHistory?.History.Entries.Select(entry => new { entry.Id, entry.Name, entry.IsDone }).ToArray(),
            historyPosition = sessionHistory?.History.Position };
        var state = (Hexa.NET.ImGui.ImGui.GetItemID(), context.HoveredId, context.ActiveId, pressed, io.MouseDown[0], released);
        if (visualScriptHistoryLastButtonState == state) return;
        visualScriptHistoryLastButtonState = state;
        if (visualScriptHistoryAddPropertyEvents.Count >= 32) visualScriptHistoryAddPropertyEvents.RemoveAt(0);
        visualScriptHistoryAddPropertyEvents.Add(visualScriptHistoryAddPropertyUi);
    }
    internal bool VisualScriptHistoryAddPropertyTargetReady
    {
        get
        {
            var context = Hexa.NET.ImGui.ImGui.GetCurrentContext(); var point = Hexa.NET.ImGui.ImGui.GetIO().MousePos;
            return visualScriptHistoryButtonId != 0 && context.HoveredIdPreviousFrame == visualScriptHistoryButtonId
                && point.X >= visualScriptHistoryButtonMinimum.X && point.X < visualScriptHistoryButtonMaximum.X
                && point.Y >= visualScriptHistoryButtonMinimum.Y && point.Y < visualScriptHistoryButtonMaximum.Y;
        }
    }
    internal int VisualScriptHistoryLastAcceptedFrame => visualScriptHistoryLastAcceptedFrame;

    // Fixture arrangement only, before the batch's one real project load.
    internal void PrepareVisualScriptHistoryFixtureBeforeLoad()
    {
        if (!args.Contains("--ui-test") || workspace != null || visualScriptHistoryFixturePath != null)
            throw new InvalidOperationException("Prepare the visual-script history fixture once before loading its private project.");
        var privateRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Stride-ImGui-UI")) + Path.DirectorySeparatorChar;
        var packagePath = Path.GetFullPath(projectPathInput);
        if (!packagePath.StartsWith(privateRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(packagePath))
            throw new InvalidOperationException("Visual-script history arrangement requires its actual private package.");
        var path = Path.Combine(Path.GetDirectoryName(packagePath)!, "Assets", "VisualScriptHistory.sdvs");
        if (File.Exists(path)) throw new IOException("The owned history producer fixture already exists.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AssetFileSerializer.Save(path, new VisualScriptAsset { Namespace = "EditorParity" }, null, assetNamespace: "EditorParity");
        visualScriptHistoryFixturePath = path;
    }

    internal string CompleteVisualScriptHistoryFixturePreparation()
    {
        var current = workspace ?? throw new InvalidOperationException("The private fixture project must be loaded once.");
        var path = visualScriptHistoryFixturePath ?? throw new InvalidOperationException("The pre-load history producer is absent.");
        var item = current.AssetItems.Single(asset => SamePath(asset.FullPath.ToOSPath(), path));
        if (item.Asset is not VisualScriptAsset || item.Package.Container is not SolutionProject project || item.Package.IsReadOnly)
            throw new InvalidOperationException("The fixture must be a real writable native visual-script project asset.");
        visualScriptHistoryPreviousEntry = documents.Active;
        visualScriptHistoryProjectPath = project.FullPath.ToOSPath();
        // Native initial compiler/save establishes the clean arrangement only.
        var producer = (VisualScriptDocument)GetCanonicalDocument(path,
            () => new VisualScriptDocument(path, sessionHistory!, false));
        var entry = documents.Find(producer) ?? documents.Add(producer);
        InitializeVisualScriptDocument(producer);
        var originalProjectHash = VisualScriptHistoryFileHash(visualScriptHistoryProjectPath);
        producer.Save();
        if (VisualScriptHistoryFileHash(visualScriptHistoryProjectPath) != originalProjectHash)
            throw new InvalidOperationException("Producer arrangement must not save or modify the project file.");
        visualScriptHistoryGeneratedPath = item.GetGeneratedAbsolutePath().ToOSPath();
        if (authoring!.Find(visualScriptHistoryGeneratedPath)?.Document is not ScriptSourceDocument generated
            || generated.GeneratedByAssetId != item.Id || generated.CanEditText || generated.IsDirty)
            throw new InvalidOperationException("Native initial save must publish actual clean compiler-owned generated source.");
        foreach (var file in new[] { path, visualScriptHistoryGeneratedPath, visualScriptHistoryProjectPath, current.PackagePath })
            visualScriptHistoryDiskBaselines[file] = VisualScriptHistoryFileHash(file);
        CloseDocument(entry);
        if (visualScriptHistoryPreviousEntry is { } previous && documents.Entries.Contains(previous)) ActivateDocument(previous);
        return path;
    }

    internal VisualScriptHistoryNativeState? InspectVisualScriptHistoryNativeState()
    {
        if (visualScriptHistoryFixturePath == null || documents.Find(visualScriptHistoryFixturePath) is not { } entry
            || entry.Document is not VisualScriptDocument producer || entry.State is not { IsPublished: true, IsDisposed: false } producerState
            || visualScriptHistoryGeneratedPath == null || authoring?.Find(visualScriptHistoryGeneratedPath) is not { IsPublished: true, IsDisposed: false } generatedState
            || generatedState.Document is not ScriptSourceDocument generated) return null;
        if (!ReferenceEquals(authoring.Find(producer.Asset.Id), producerState)
            || !generated.IsGeneratedOutputOwnedBy(producerState) || generated.CanEditText
            || generated.GeneratedByAssetId != producer.Asset.Id || !ReferenceEquals(generated.SessionHistory, sessionHistory))
            throw new InvalidOperationException("Repeated generation must retain its real canonical producer lease and protected source authority.");
        var owner = GetVisualScriptCompilerOwner(producer);
        var compilerText = producer.CaptureCompilation(owner.Item).Result.GeneratedSource
            ?? throw new InvalidDataException("The actual native compiler emitted no source.");
        var transactions = sessionHistory!.Stack.RetrieveAllTransactions().ToArray();
        return new(entry, producer, producerState, generated, generatedState, compilerText, sessionHistory.History.Position,
            transactions.Select(transaction => transaction.Id).ToArray(),
            transactions.Select(transaction => sessionHistory.GetParticipants(transaction).Select(state => state.Generation).ToArray()).ToArray());
    }

    internal void CertifyVisualScriptHistoryFiles()
    {
        foreach (var file in visualScriptHistoryDiskBaselines)
            if (VisualScriptHistoryFileHash(file.Key) != file.Value)
                throw new InvalidOperationException("UI generation/undo/redo modified a saved producer, generated output, package or project file: " + file.Key);
    }

    internal object? InspectVisualScriptHistoryBrowserReadiness()
    {
        if (!Controls.Enabled) return null;
        var target = workspace?.Assets.FirstOrDefault(asset => visualScriptHistoryFixturePath != null && SamePath(asset.Path, visualScriptHistoryFixturePath));
        var context = Hexa.NET.ImGui.ImGui.GetCurrentContext();
        return new { filterDraft = AssetBrowserPanel.SearchText,
            addPropertyUi = visualScriptHistoryAddPropertyUi, addPropertyEvents = visualScriptHistoryAddPropertyEvents,
            addTargetReady = VisualScriptHistoryAddPropertyTargetReady, lastAcceptedFrame = visualScriptHistoryLastAcceptedFrame,
            nameFilters = assetBrowser.Names.Select(filter => new { filter.Text, filter.Enabled }).ToArray(),
            tagFilters = assetBrowser.Tags.Select(filter => new { filter.Text, filter.Enabled }).ToArray(),
            typeFilterCount = assetBrowser.TypeFilterCount,
            availableTypes = assetBrowser.AvailableTypes.Select(type => new { type = type.FullName, selected = assetBrowser.HasType(type), enabled = assetBrowser.IsTypeEnabled(type) }).ToArray(),
            selectedFolder = assetBrowser.SelectedFolder == null ? null : new { assetBrowser.SelectedFolder.Id, assetBrowser.SelectedFolder.Name,
                assetBrowser.SelectedFolder.RelativePath, assetBrowser.SelectedFolder.IsCode },
            selectedCategory = assetBrowser.SelectedCategory == null ? null : new { assetBrowser.SelectedCategory.Id, assetBrowser.SelectedCategory.Name },
            assetBrowser.DisplayMode, assetBrowser.IncludeDescendants,
            target = target == null ? null : new { target.Name, target.Path, target.Id, type = target.AssetType.FullName,
                nativeControl = "asset/" + target.Name, isProjectAsset = typeof(IProjectAsset).IsAssignableFrom(target.AssetType) },
            visibleAssets = assetBrowser.Visible.Take(64).Select(asset => new { asset.Name, asset.Path, asset.Id, type = asset.AssetType.FullName }).ToArray(),
            activeId = context.ActiveId, navWindowId = context.NavWindow.IsNull ? 0 : context.NavWindow.ID,
            hoveredId = context.HoveredId, filterControlPresent = Controls.Centers.ContainsKey("asset/filter"),
            recordedAssetControls = Controls.Centers.Keys.Where(key => key.StartsWith("asset/", StringComparison.Ordinal)).Take(64).ToArray() };
    }

    internal (string NativeControl, string[] Locations) GetVisualScriptHistoryBrowserNavigation()
    {
        var target = workspace!.Assets.Single(asset => SamePath(asset.Path, visualScriptHistoryFixturePath!));
        var item = workspace.FindAsset(target.Id);
        var root = assetBrowser.Packages.Single(folder => ReferenceEquals(folder.Package, item.Package));
        var folder = root.Children.Single(child => child.IsCode);
        var locations = new List<string> { root.Id, folder.Id };
        var url = target.Name.Replace('\\', '/');
        var slash = url.LastIndexOf('/');
        if (slash >= 0)
            foreach (var part in url[..slash].Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                folder = folder.Children.Single(child => string.Equals(child.Name, part, StringComparison.OrdinalIgnoreCase));
                locations.Add(folder.Id);
            }
        return ("asset/" + target.Name, locations.ToArray());
    }
    internal bool IsVisualScriptHistoryBrowserLocationSelected(string id)
        => string.Equals(assetBrowser.SelectedFolder?.Id, id, StringComparison.OrdinalIgnoreCase);

    internal void CloseVisualScriptHistoryFixture()
    {
        var state = InspectVisualScriptHistoryNativeState() ?? throw new InvalidOperationException("The canonical history fixture owner is absent.");
        if (state.Producer.IsDirty || state.Generated.IsDirty)
            throw new InvalidOperationException("UI history must restore the clean producer and compiler output baseline before closing.");
        CertifyVisualScriptHistoryFiles();
        CloseDocument(state.ProducerEntry);
        if (documents.Find(state.Generated) is { } sourceEntry) CloseDocument(sourceEntry);
        if (visualScriptHistoryPreviousEntry is { } previous && documents.Entries.Contains(previous)) ActivateDocument(previous);
        visualScriptHistoryPreviousEntry = null;
    }

    private static string VisualScriptHistoryFileHash(string path)
        => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
