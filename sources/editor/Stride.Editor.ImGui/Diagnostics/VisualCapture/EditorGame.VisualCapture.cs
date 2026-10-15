using Stride.Graphics;
using Vector2 = System.Numerics.Vector2;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private VisualCaptureEndpoint? visualCapture;
    private int captureTitleRevision = -1;

    private void RefreshVisualCaptureTitles()
    {
        if (visualCapture == null || captureTitleRevision == EditorHotReload.Revision) return;
        captureTitleRevision = EditorHotReload.Revision;
        foreach (var panel in EditorPanels.All)
            RegisterVisualCaptureTargetDefinition(panel.CaptureId, panel.Kind, panel.Title, panel.WindowName);
        RegisterVisualCaptureTargetDefinition("editor.properties", "panel", "Property grid", "Property grid###Inspector");
        RegisterVisualCaptureTargetDefinition("dialogs.delete-assets", "modal", "Delete assets", "###DeleteAssetsConfirmation");
        RegisterVisualCaptureTargetDefinition("dialogs.delete-entity", "modal", "Delete entity", "###DeleteEntityConfirmation");
        RegisterVisualCaptureTargetDefinition("dialogs.close-editor", "modal", "Close editor", "###CloseEditorConfirmation");
        RegisterVisualCaptureTargetDefinition("dialogs.reload-assemblies", "modal", "Reload assemblies", "###ReloadAssembliesConfirmation");
    }

    private void InitializeVisualCapture()
    {
        var index = Array.IndexOf(args, "--capture-directory");
        if (index < 0) return;
        if (index + 1 >= args.Length) throw new ArgumentException("--capture-directory requires an endpoint directory.");
        visualCapture = new(args[index + 1], Window.NativeWindow.Handle);
        visualCapture.RevealTarget = RevealVisualCaptureTarget;
        visualCapture.TargetAction = PerformVisualCaptureAction;
        visualCapture.StateMetadata = () => new
        {
            drawReady = renderer != null, projectLoading = projectLoading != null,
            statusMessage, load = InspectVisualCaptureLoadState(),
            scheduling = InspectVisualCaptureScheduling(),
            matchedBenchmarkGeometry = InspectMatchedBenchmarkGeometry(),
            frameStages = InspectFrameStageMeasurements(),
            fontMemory = InspectVisualCaptureFontMemory(),
            thumbnailMemory = InspectVisualCaptureThumbnailMemory(),
            selectedAssetId = assetBrowser.PrimaryEntry?.Id.ToString(),
            selectedAssetPath = assetBrowser.PrimaryEntry?.Path,
            propertyAssetContext = IsCurrentAssetPropertyContext,
            propertyAssetId = PropertyGridCommandDocument is IAssetTagDocument propertyAsset ? propertyAsset.AuthoredAsset.Id.ToString() : null,
            propertyAssetPath = PropertyGridCommandDocument?.Path,
            previewAssetId = VisualCapturePreviewAssetId,
            previewAssetPath = VisualCapturePreviewAssetPath,
            previewUrl = VisualCapturePreviewUrl,
            previewReady = VisualCapturePreviewReady,
            documentAssetId = document?.Asset.Id.ToString(), documentPath = document?.Path,
            presentedAssetId = PresentedHierarchyDocument?.Asset.Id.ToString(), sceneViewportVisible,
            settingsCategory,
            hotReloadEnabled = System.Reflection.Metadata.MetadataUpdater.IsSupported,
            hotReloadRevision = EditorHotReload.Revision,
            nativeDialogs = "external-process-owned-hwnd-capture"
        };
        Controls.CaptureRecorder = id => RegisterVisualCaptureItem("imgui.item." + id.Replace('/', '.'));
        Controls.Enabled = true;
        RegisterVisualCaptureTargetDefinition("editor.menu", "toolbar", "Main menu", "##MainMenuBar");
        RegisterVisualCaptureTargetDefinition("editor.document", "window", "Document", "Scene");
        RegisterVisualCaptureTargetDefinition("scene.hierarchy", "panel", "Hierarchy", "Hierarchy");
        RegisterVisualCaptureTargetDefinition("editor.properties", "panel", "Property grid", "Property grid###Inspector");
        RegisterVisualCaptureTargetDefinition("assets.preview", "panel", "Asset preview", "Asset preview");
        RegisterVisualCaptureTargetDefinition("assets.references", "panel", "References", "References");
        RegisterVisualCaptureTargetDefinition("editor.history", "panel", "Edit history", "Edit history");
        RegisterVisualCaptureTargetDefinition("assets.errors", "panel", "Asset errors", "Asset errors");
        RegisterVisualCaptureTargetDefinition("sprites.editor", "window", "Sprite Editor", "Sprite Editor");
        RegisterVisualCaptureTargetDefinition("dialogs.settings", "modal", "Settings", "Settings");
        RegisterVisualCaptureTargetDefinition("dialogs.about", "modal", "About Stride", "About Stride");
        RegisterVisualCaptureTargetDefinition("dialogs.open-project", "modal", "Open project", "Open project");
        RegisterVisualCaptureTargetDefinition("dialogs.close-document", "modal", "Close document", "Close document");
        RegisterVisualCaptureTargetDefinition("dialogs.close-editor", "modal", "Close editor", "Close editor");
        RegisterVisualCaptureTargetDefinition("dialogs.unsaved-changes", "modal", "Unsaved changes", "Unsaved changes");
        RegisterVisualCaptureTargetDefinition("dialogs.delete-assets", "modal", "Delete assets", "Delete assets");
        RegisterVisualCaptureTargetDefinition("dialogs.delete-entity", "modal", "Delete entity", "Delete entity?");
        RegisterVisualCaptureTargetDefinition("dialogs.rename-folder", "modal", "Rename folder", "Rename folder");
        RegisterVisualCaptureTargetDefinition("dialogs.new-folder", "modal", "New folder", "New folder");
        RegisterVisualCaptureTargetDefinition("dialogs.reload-assemblies", "modal", "Reload assemblies", "Reload assemblies");
        var catalogIndex = Array.IndexOf(args, "--capture-catalog");
        var catalog = catalogIndex >= 0 && catalogIndex + 1 < args.Length ? args[catalogIndex + 1]
            : Path.Combine(AppContext.BaseDirectory, "visual-capture-catalog.json");
        if (File.Exists(catalog)) LoadVisualCaptureCatalog(catalog);
        RegisterVisualCaptureTargetDefinition("editor.workspace", "window", "Workspace", null, null, false);
        RegisterVisualCaptureTargetDefinition("assets.browser", "window", "Asset view", "Asset view");
        RegisterVisualCaptureTargetDefinition("editor.output", "window", "Output", "Output");
        DefineVisualCaptureDraftDialogs();
    }

    private void LoadVisualCaptureCatalog(string path)
    {
        using var catalog = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        if (catalog.RootElement.GetProperty("schemaVersion").GetInt32() != 1) throw new InvalidDataException("Unsupported visual capture catalog version.");
        foreach (var target in catalog.RootElement.GetProperty("targets").EnumerateArray())
        {
            var id = target.GetProperty("id").GetString()!;
            var kind = target.GetProperty("kind").GetString()!;
            var parent = target.TryGetProperty("parentId", out var parentProperty) ? parentProperty.GetString() : null;
            var backend = target.GetProperty("imgui");
            var present = backend.GetProperty("status").GetString() == "source-declared";
            string? window = null;
            if (backend.TryGetProperty("lookup", out var lookup) && lookup.TryGetProperty("windowNames", out var names) && names.GetArrayLength() == 1)
                window = names[0].GetString();
            RegisterVisualCaptureTargetDefinition(id, kind, id, window, parent, present);
        }
    }

    private bool RevealVisualCaptureTarget(string id)
    {
        if (RevealVisualCaptureDraftDialog(id)) return true;
        if (TryResolveDocumentCapture(id, out var entry, out _))
        { selectDocumentTab = entry; focusDocumentPanel = true; return true; }
        string? window = null;
        switch (id)
        {
            case "dialogs.about": openAbout = true; return true;
            case "dialogs.settings": OpenEditorSettings(); return true;
            case "scene.hierarchy": showHierarchy = true; window = "Hierarchy"; break;
            case "editor.properties": showInspector = true; window = "Property grid###Inspector"; break;
            case "editor.history": showEditHistory = true; window = "Edit history"; break;
            case "assets.references": showAssetReferences = true; window = "References"; break;
            case "assets.errors": showAssetErrors = true; window = "###Asset errors"; break;
            case "assets.browser": showAssets = true; focusAssetsPanel = true; window = "Asset view"; break;
            case "assets.folders": showSolutionExplorer = true; SolutionExplorerController.FocusRequested = true; window = "Solution explorer"; break;
            case "editor.output": showOutput = true; focusOutput = true; window = "Output"; break;
            case "assets.preview": showAssetPreview = true; focusAssetPreview = true; window = "Asset preview"; break;
            case "editor.document": window = "Scene"; break;
            default: return false;
        }
        Hexa.NET.ImGui.ImGui.SetWindowFocus(window == "Scene" ? window : EditorLocalization.Widget(window!, "View"));
        return true;
    }
    private void BeginVisualCaptureFrame() => visualCapture?.BeginFrame();
    private void InventoryVisualCaptureWindows()
    {
        visualCapture?.HideInactiveDocumentAliases(documents.Active?.Id.ToString());
        visualCapture?.InventoryWindows();
    }
    private void CompleteVisualCaptureFrame(Texture backBuffer)
    {
        if (visualCapture == null) return;
        try { visualCapture.CompleteFrame(backBuffer, GraphicsContext, id => platformViewports?.GetRenderedCaptureBuffer(id)); }
        catch (Exception error)
        {
            EditorGraphicsFailures.ThrowIfDeviceLost(GraphicsDevice, error);
            Console.Error.WriteLine("Visual capture endpoint failed: " + error.Message); ShutdownVisualCapture();
        }
    }
    private void ShutdownVisualCapture()
    {
        Controls.CaptureRecorder = null;
        try { visualCapture?.Dispose(); }
        catch (Exception error) { Console.Error.WriteLine("Visual capture shutdown failed: " + error.Message); }
        finally { visualCapture = null; }
    }
    private void RegisterVisualCaptureTargetDefinition(string id, string kind, string name, string? windowName = null, string? parentId = null, bool implemented = true)
        => visualCapture?.DefineTarget(id, kind, name, windowName, parentId, implemented);
    private void RegisterVisualCaptureItem(string id, string kind = "control", string? name = null)
    {
        if (visualCapture != null) RegisterOwnedCaptureRegion(id, kind, name ?? id,
            Hexa.NET.ImGui.ImGui.GetItemRectMin(), Hexa.NET.ImGui.ImGui.GetItemRectMax(), null, Hexa.NET.ImGui.ImGui.IsItemVisible());
    }
    private void RegisterVisualCaptureRegion(string id, string kind, string name, Vector2 min, Vector2 max, string? parentId = null)
        => RegisterOwnedCaptureRegion(id, kind, name, min, max, parentId, true);
}
