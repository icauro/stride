using System.Globalization;
using System.Security.Cryptography;
using Stride.Assets.Entities;
using Stride.Assets.Quantum;
using Stride.UI.Assets;
using Stride.Core.Assets;
using Stride.Core.Assets.Quantum;
using Stride.Core.Diagnostics;
using Stride.Core.IO;
using Stride.Core.Reflection;
using Stride.Engine;
using Stride.UI.Controls;

namespace Stride.Editor.ImGui;

internal sealed record StockAssetDispatchState(EditorDocumentEntry Entry, IEditorDocument Document,
    EditorAuthoringState State, string AssetId, Guid PartId, string Value, bool Dirty,
    int HistoryPosition, bool CanUndo, bool CanRedo, string DiskSha256);

internal sealed partial class EditorGame
{
    private readonly Dictionary<string, int> stockBrowserOpenFrames = new(StringComparer.OrdinalIgnoreCase);
    private object? stockBrowserLastOpen;
    private void RecordStockBrowserOpen(AssetEntry asset)
    {
        var frame = Hexa.NET.ImGui.ImGui.GetFrameCount();
        stockBrowserOpenFrames[Path.GetFullPath(asset.Path)] = frame;
        stockBrowserLastOpen = new { frame, asset.Path, asset.Name, assetId = asset.Id.ToString(), nativeType = asset.AssetType.FullName };
    }
    internal bool StockBrowserOpenAccepted(string path, int sinceFrame)
        => stockBrowserOpenFrames.TryGetValue(Path.GetFullPath(path), out var frame) && frame >= sinceFrame;
    internal int StockBrowserOpenFrame(string path) => stockBrowserOpenFrames.GetValueOrDefault(Path.GetFullPath(path));
    internal bool StockBrowserSelectionMatches(string path) => assetBrowser.SelectedCount == 1
        && assetBrowser.SelectedContentFolders.Count == 0 && assetBrowser.PrimaryEntry is { } asset && SamePath(asset.Path, path);
    internal object InspectStockBrowserDispatch(string path)
    {
        var selected = VerifyRecoveryAcceptanceNativeTab(path, out var nativeTab);
        var entry = documents.Find(path);
        var group = entry == null ? null : DocumentGroupPanel.Model.Find(entry);
        return new { lastOpen = stockBrowserLastOpen, acceptedFrame = stockBrowserOpenFrames.GetValueOrDefault(Path.GetFullPath(path)),
            globalActivePath = documents.Active?.Path, entryId = entry?.Id, groupId = group?.Id,
            groupActivePath = group?.Active?.Path, nativeSelected = selected, nativeTab };
    }
    private readonly Dictionary<string, object> stockHistoryToolbarCurrent = new();
    private readonly Dictionary<string, object> stockHistoryToolbarPressed = new();
    private readonly Dictionary<string, (int Frame, string? Path)> stockHistoryToolbarAccepted = new();
    private readonly Dictionary<string, (int Frame, uint NativeId, bool Enabled, string? Path)> stockHistoryToolbarReadiness = new();
    private readonly Dictionary<string, StockHistoryGesture> stockHistoryGestures = new();
    private readonly List<StockHistoryGesture> stockHistoryGestureEvidence = [];
    private sealed record StockHistoryNativeSample(int Frame, uint Id, bool Disabled, bool Down, bool Released,
        bool Pressed, string? Path, object Guards);
    private sealed class StockHistoryGesture
    {
        public required string Action { get; init; }
        [System.Text.Json.Serialization.JsonIgnore]
        public StockAssetDispatchState Before { get; init; } = null!;
        public object CanonicalBefore => new { Before.Document.Path, Before.AssetId, Before.PartId, Before.Value,
            Before.Dirty, Before.HistoryPosition, Before.DiskSha256, generation = Before.State.Generation };
        public bool NativeEnabledAtRequest => true;
        public required string HistorySignature { get; init; }
        public required int PressFrame { get; init; }
        public required uint NativeId { get; init; }
        public List<StockHistoryNativeSample> Samples { get; } = [];
        public bool DownObserved { get; set; }
        public bool ReleaseObserved { get; set; }
        public bool Canceled { get; set; }
        public string? Certificate { get; set; }
    }
    private string StockGlobalHistorySignature() => sessionHistory == null ? "" :
        sessionHistory.History.Position + ":" + string.Join(";", sessionHistory.History.Entries.Select(entry => entry.Id + ":" + entry.IsDone));
    internal void BeginStockHistoryToolbarGesture(string action, bool library)
    {
        var before = InspectStockAssetDispatch(library) ?? throw new InvalidOperationException("Native history gesture lost its canonical owner.");
        if (!StockHistoryToolbarReady(action, before.Document.Path)) throw new InvalidOperationException("Native history gesture was not enabled when requested.");
        var control = stockHistoryToolbarReadiness[action].NativeId;
        if (control == 0) throw new InvalidOperationException("Native history gesture has no actual toolbar item identity.");
        var gesture = new StockHistoryGesture { Action = action, Before = before, HistorySignature = StockGlobalHistorySignature(),
            PressFrame = Hexa.NET.ImGui.ImGui.GetFrameCount(), NativeId = control };
        stockHistoryGestures[action] = gesture; stockHistoryGestureEvidence.Add(gesture);
    }
    internal bool CertifyStockHistoryToolbarCancellation(string action, bool library)
    {
        if (!stockHistoryGestures.TryGetValue(action, out var gesture) || gesture.Canceled || !gesture.ReleaseObserved) return false;
        var samples = gesture.Samples;
        var down = samples.FirstOrDefault(sample => sample.Down);
        var release = down == null ? null : samples.FirstOrDefault(sample => sample.Frame > down.Frame && !sample.Down);
        StockHistoryNativeSample[] interval = down == null || release == null ? [] : samples.Where(sample => sample.Frame >= down.Frame && sample.Frame <= release.Frame).ToArray();
        if (down == null || down.Disabled || release == null || !release.Released || interval.Zip(interval.Skip(1)).Any(pair => pair.Second.Frame != pair.First.Frame + 1)
            || samples.Any(sample => sample.Pressed)
            || StockHistoryToolbarAccepted(action, gesture.Before.Document.Path, gesture.PressFrame)
            || !samples.Any(sample => sample.Frame >= down.Frame && sample.Frame <= release.Frame && sample.Disabled)
            || samples.Any(sample => sample.Id != gesture.NativeId || sample.Path == null || !SamePath(sample.Path, gesture.Before.Document.Path))) return false;
        var current = InspectStockAssetDispatch(library);
        if (current == null || !ReferenceEquals(current.Document, gesture.Before.Document) || !ReferenceEquals(current.State, gesture.Before.State)
            || current.AssetId != gesture.Before.AssetId || current.PartId != gesture.Before.PartId || current.Value != gesture.Before.Value
            || current.Dirty != gesture.Before.Dirty || current.HistoryPosition != gesture.Before.HistoryPosition
            || current.DiskSha256 != gesture.Before.DiskSha256 || StockGlobalHistorySignature() != gesture.HistorySignature) return false;
        gesture.Canceled = true;
        gesture.Certificate = "Native enabled request canceled by actual disabled transition during acknowledged press/release; no activation, canonical value, disk and exact global journal IDs/position unchanged.";
        return true;
    }
    internal bool StockHistoryToolbarReady(string action, string path)
        => stockHistoryToolbarReadiness.TryGetValue(action, out var receipt) && receipt.Enabled
            && receipt.Frame >= Hexa.NET.ImGui.ImGui.GetFrameCount() - 1
            && receipt.Path != null && SamePath(receipt.Path, path)
            && !RecoveryTransitionBusy && projectLoading == null && assetPreviewBuild == null && !IsAuthoringPipelineUpdating
            && !closeRequested && !closePromptOpen && closingDocument == null
            && (action == "redo" ? CanRedoCurrent : CanUndoCurrent);
    internal bool StockHistoryToolbarAccepted(string action, string path, int sinceFrame)
        => stockHistoryToolbarAccepted.TryGetValue(action, out var receipt) && receipt.Frame >= sinceFrame
            && receipt.Path != null && SamePath(receipt.Path, path);
    private unsafe void RecordStockHistoryToolbar(string action, bool pressed)
    {
        var context = Hexa.NET.ImGui.ImGui.GetCurrentContext();
        var disabled = (context.LastItemData.ItemFlags & (Hexa.NET.ImGui.ImGuiItemFlags)Hexa.NET.ImGui.ImGuiItemFlagsPrivate.Disabled) != 0;
        stockHistoryToolbarReadiness[action] = (Hexa.NET.ImGui.ImGui.GetFrameCount(), context.LastItemData.ID, !disabled, CommandDocument?.Path);
        if (stockHistoryGestures.TryGetValue(action, out var gesture) && !gesture.ReleaseObserved && gesture.Samples.Count < 128)
        {
            var frame = Hexa.NET.ImGui.ImGui.GetFrameCount();
            if (frame > gesture.PressFrame && (gesture.Samples.Count == 0 || gesture.Samples[^1].Frame != frame))
            {
                var down = Hexa.NET.ImGui.ImGui.IsMouseDown(Hexa.NET.ImGui.ImGuiMouseButton.Left);
                gesture.Samples.Add(new(frame, context.LastItemData.ID, disabled, down,
                    Hexa.NET.ImGui.ImGui.IsMouseReleased(Hexa.NET.ImGui.ImGuiMouseButton.Left), pressed,
                    CommandDocument?.Path, InspectStockHistoryToolbarGuards()));
                gesture.DownObserved |= down;
                gesture.ReleaseObserved = gesture.DownObserved && !down;
            }
        }
        var receipt = new
        {
            frame = Hexa.NET.ImGui.ImGui.GetFrameCount(), id = context.LastItemData.ID, pressed,
            hovered = Hexa.NET.ImGui.ImGui.IsItemHovered(), active = Hexa.NET.ImGui.ImGui.IsItemActive(),
            activeId = context.ActiveId, hoveredId = context.HoveredId,
            disabled,
            target = CurrentHistoryTarget(action == "redo").ToString(),
            commandPath = CommandDocument?.Path, pendingPropertyStrings = HasPendingPropertyStrings,
            readiness = InspectStockHistoryToolbarGuards(),
            pendingRename = HasPendingRenameChanges, history = sessionHistory?.History
        };
        stockHistoryToolbarCurrent[action] = receipt;
        if (pressed)
        {
            stockHistoryToolbarPressed[action] = receipt;
            stockHistoryToolbarAccepted[action] = (Hexa.NET.ImGui.ImGui.GetFrameCount(), CommandDocument?.Path);
        }
    }
    private object InspectStockHistoryToolbarGuards() => new
    {
        CanUndoCurrent, CanRedoCurrent, RecoveryTransitionBusy, projectLoading = projectLoading != null,
        assetPreviewBuild = assetPreviewBuild != null, IsAuthoringPipelineUpdating,
        IsEditorGpuRetirementPending, IsEditorGraphicsRecovering, IsActiveProjectSwitching, IsAddingSolutionProjects,
        IsPackagePlatformUpdating, IsPackageMetadataSaving, IsProjectCreationUpdating, IsCompositorSettingsUpdating,
        IsAssetImportUpdating, IsImportSettingsUpdating, IsTextureSettingsUpdating, IsSkyboxSettingsUpdating,
        IsFontSettingsUpdating, IsMediaPreviewUpdating, IsCatalogPreviewUpdating, IsExternalAssetPreviewUpdating,
        IsExternalAssetPreviewDrainPending, IsPackageDependencyBusy, HasPendingSpriteSourceEdits,
        IsSceneLightProbeBusy, IsEditorExtensionCommandRunning, closeRequested, closePromptOpen,
        closingDocument = closingDocument != null
    };
    internal object InspectStockHistoryReplay(bool library)
    {
        var state = InspectStockAssetDispatch(library);
        var strings = propertyGridPanel?.Strings;
        var pending = strings == null ? null : typeof(PropertyStringEditor).GetField("pending",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(strings);
        object? DraftField(string name) => pending?.GetType().GetField(name,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(pending);
        return new
        {
            state = state == null ? null : new { state.AssetId, state.PartId, state.Value, state.Dirty,
                state.HistoryPosition, state.CanUndo, state.CanRedo, state.DiskSha256, generation = state.State.Generation },
            globalHistory = sessionHistory?.History,
            pendingDraft = pending == null ? null : new { text = DraftField("Text"), edited = DraftField("Edited"),
                active = DraftField("Active"), error = DraftField("LastValidationError") },
            commandPath = CommandDocument?.Path, pendingPropertyStrings = HasPendingPropertyStrings,
            readiness = InspectStockHistoryToolbarGuards(),
            current = stockHistoryToolbarCurrent, lastPressed = stockHistoryToolbarPressed,
            nativeGestures = stockHistoryGestureEvidence
        };
    }
    private string? stockPrefabPath, stockLibraryPath;
    private Guid stockPrefabRoot, stockLibraryRoot;

    // Genuine native graph/serializer fixture construction only. Browser opening,
    // authored mutations, history, saving and closing are performed by UI actors.
    private void PrepareStockAssetDispatchFixtures(string packagePath)
    {
        var directory = Path.Combine(Path.GetDirectoryName(packagePath)!, "Assets");
        stockPrefabPath = Path.Combine(directory, "StockDispatchPrefab.sdprefab");
        stockLibraryPath = Path.Combine(directory, "StockDispatchLibrary.sduilib");
        if (File.Exists(stockPrefabPath) || File.Exists(stockLibraryPath)) throw new IOException("Stock asset fixture destination is occupied.");
        var prefab = new PrefabAsset();
        var entity = new Entity("StockPrefabRoot"); stockPrefabRoot = entity.Id;
        prefab.Hierarchy.RootParts.Add(entity); prefab.Hierarchy.Parts.Add(new EntityDesign(entity));
        var library = new UILibraryAsset();
        var button = (Button)ObjectFactoryRegistry.NewInstance(typeof(Button));
        button.Name = "Stock reusable"; button.Width = 180; button.Height = 48; stockLibraryRoot = button.Id;
        library.Hierarchy.RootParts.Add(button); library.Hierarchy.Parts.Add(new UIElementDesign(button));
        library.PublicUIElements.Add(button.Id, button.Name);
        SaveStockNativeFixture(stockPrefabPath, prefab, "StockDispatchPrefab");
        SaveStockNativeFixture(stockLibraryPath, library, "StockDispatchLibrary");
        var loadedPrefab = AssetFileSerializer.Load<PrefabAsset>(stockPrefabPath).Asset;
        var loadedLibrary = AssetFileSerializer.Load<UILibraryAsset>(stockLibraryPath).Asset;
        if (loadedPrefab.Id != prefab.Id || loadedPrefab.Hierarchy.RootParts.Single().Id != stockPrefabRoot
            || loadedLibrary.Id != library.Id || loadedLibrary.Hierarchy.RootParts.Single().Id != stockLibraryRoot
            || loadedLibrary.PublicUIElements[stockLibraryRoot] != button.Name)
            throw new InvalidOperationException("Stock native fixtures failed independent serializer identity roundtrip.");
        var package = AssetFileSerializer.Load<Package>(packagePath).Asset;
        var packageOwner = package.Container ?? new StandalonePackage(package);
        package.RootAssets.Add(new AssetReference(prefab.Id, packageOwner.Qualify(new UFile("StockDispatchPrefab"))));
        package.RootAssets.Add(new AssetReference(library.Id, packageOwner.Qualify(new UFile("StockDispatchLibrary"))));
        AssetFileSerializer.Save(packagePath, package, null);
    }
    private static void SaveStockNativeFixture(string path, Asset asset, string name)
    {
        AssetCollectionItemIdHelper.GenerateMissingItemIds(asset);
        AssetQuantumRegistry.RegisterAssembly(typeof(AssetPropertyGraph).Assembly);
        AssetQuantumRegistry.RegisterAssembly(typeof(EntityHierarchyPropertyGraph).Assembly);
        var container = new AssetPropertyGraphContainer(new AssetNodeContainer { NodeBuilder = { NodeFactory = new AssetNodeFactory() } });
        var item = new AssetItem(name, asset); var log = new LoggerResult();
        using var graph = container.InitializeAsset(item, log)
            ?? throw new InvalidOperationException("Stock native fixture has no registered property graph.");
        graph.Initialize(); graph.PrepareForSave(log, item);
        if (log.HasErrors) throw new InvalidOperationException("Stock fixture graph preparation failed: " + log.ToText());
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); AssetFileSerializer.Save(path, asset, item.YamlMetadata);
    }
    internal (string Path, string Control, string[] Locations) InspectStockAssetBrowserNavigation(bool library)
    {
        var current = workspace ?? throw new InvalidOperationException("Stock native project is absent.");
        var path = (library ? stockLibraryPath : stockPrefabPath) ?? throw new InvalidOperationException("Stock asset fixture is absent.");
        var target = current.Assets.Single(asset => SamePath(asset.Path, path));
        var item = current.FindAsset(target.Id);
        if (library ? item.Asset is not UILibraryAsset : item.Asset is not PrefabAsset)
            throw new InvalidOperationException("Stock fixture has another native registered asset type.");
        var root = assetBrowser.Packages.Single(folder => ReferenceEquals(folder.Package, item.Package));
        var folder = root.Children.First(child => !child.IsCode); var locations = new List<string> { root.Id, folder.Id };
        var url = item.UnqualifiedUrl.ToString().Replace('\\', '/'); var slash = url.LastIndexOf('/');
        if (slash >= 0) foreach (var part in url[..slash].Split('/', StringSplitOptions.RemoveEmptyEntries))
        { folder = folder.Children.Single(child => string.Equals(child.Name, part, StringComparison.OrdinalIgnoreCase)); locations.Add(folder.Id); }
        return (path, "asset/" + target.Name, locations.ToArray());
    }
    internal StockAssetDispatchState? InspectStockAssetDispatch(bool library, bool independentDisk = false)
    {
        var path = library ? stockLibraryPath : stockPrefabPath;
        if (path == null || documents.Find(path) is not { } entry) return null;
        var current = workspace ?? throw new InvalidOperationException("Stock asset lost its native workspace.");
        var item = current.AssetItems.Single(asset => SamePath(asset.FullPath.ToOSPath(), path));
        if (entry.State is not { IsPublished: true, IsDisposed: false } state || !ReferenceEquals(authoring?.Find(item.Id), state)
            || !ReferenceEquals(state.Document, entry.Document) || !entry.Document.CanSave
            || documents.Entries.Count(owner => SamePath(owner.Path, path)) != 1)
            throw new InvalidOperationException("Stock dispatch lost its unique canonical native asset owner.");
        string value; Guid part; EditorHistorySnapshot history; bool undo, redo;
        if (library)
        {
            if (entry.Document is not UIDocument ui || ui.Asset is not UILibraryAsset authored || authored.Id != item.Id)
                throw new InvalidOperationException("UILibrary default dispatch did not open its actual native UIDocument.");
            var root = authored.Hierarchy.RootParts.Single(); part = root.Id; value = root.Name;
            if (part != stockLibraryRoot || authored.PublicUIElements[part] != value)
                throw new InvalidOperationException("Library root rename/export must retain actual element ID and matching reusable native name.");
            history = ui.History; undo = ui.CanUndo; redo = ui.CanRedo;
            if (independentDisk)
            {
                var disk = AssetFileSerializer.Load<UILibraryAsset>(path).Asset;
                if (disk.Id != item.Id || disk.Hierarchy.RootParts.Single().Id != part
                    || disk.Hierarchy.RootParts.Single().Name != value || disk.PublicUIElements[part] != value)
                    throw new InvalidOperationException("Independent native library reload lost element/export/asset identity.");
            }
        }
        else
        {
            if (entry.Document is not PrefabDocument prefab || prefab.Asset.Id != item.Id)
                throw new InvalidOperationException("Prefab default dispatch did not open its actual native PrefabDocument.");
            var root = prefab.Asset.Hierarchy.RootParts.Single(); part = root.Id; value = root.Transform.Position.X.ToString("R", CultureInfo.InvariantCulture);
            if (part != stockPrefabRoot || prefab.Asset.Hierarchy.Parts.Count != 1 || prefab.Asset.Hierarchy.Parts[part].Base != null)
                throw new InvalidOperationException("Standalone prefab must retain original native part ownership without inventing inheritance.");
            history = prefab.History; undo = prefab.CanUndo; redo = prefab.CanRedo;
            if (independentDisk)
            {
                var disk = AssetFileSerializer.Load<PrefabAsset>(path).Asset;
                if (disk.Id != item.Id || disk.Hierarchy.RootParts.Single().Id != part
                    || disk.Hierarchy.RootParts.Single().Transform.Position.X.ToString("R", CultureInfo.InvariantCulture) != value)
                    throw new InvalidOperationException("Independent native prefab reload lost transform/part/asset identity.");
            }
        }
        return new(entry, entry.Document, state, item.Id.ToString(), part, value, entry.Document.IsDirty,
            history.Position, undo, redo, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
    }
    internal bool StockAssetDispatchClosed(bool library) => documents.Find((library ? stockLibraryPath : stockPrefabPath)!) == null;
    internal bool StockPrefabRootSelected => stockPrefabPath != null && documents.Find(stockPrefabPath)?.Document is PrefabDocument prefab
        && prefab.Selected?.Id == stockPrefabRoot;
    internal object InspectStockPrefabInspector() => new
    {
        selected = stockPrefabPath == null ? null : (documents.Find(stockPrefabPath)?.Document as PrefabDocument)?.Selected?.Id,
        expected = stockPrefabRoot,
        controls = Controls.Centers.Keys.Where(key => key.StartsWith("Position", StringComparison.Ordinal)
            || key.StartsWith("component/", StringComparison.Ordinal) || key.StartsWith("tooltab/editor.properties", StringComparison.Ordinal)).Take(32).ToArray(),
        nativeTabs = InspectRecoveryAcceptanceToolTabs(), transformLayout = PropertyGrid.InspectorTransformLayout
    };
}
