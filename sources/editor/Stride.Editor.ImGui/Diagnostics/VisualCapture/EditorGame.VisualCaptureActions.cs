using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;
using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private unsafe object PerformVisualCaptureAction(string target, string action, JsonElement parameters)
    {
        if (target.StartsWith("documents.", StringComparison.Ordinal))
        {
            if (!TryResolveDocumentCapture(target, out var entry, out var semantic) || !ReferenceEquals(documents.Active, entry)
                || visualCapture?.HasDocumentTarget(target, entry!.Id.ToString(), entry.State!.Generation.ToString()) != true)
                throw new InvalidOperationException("Reveal the current canonical document before requesting its capture action.");
            target = semantic;
        }
        string? Text(string key) => parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty(key, out var value) ? value.GetString() : null;
        if (target == "scene.viewport" && action == "set-viewport-size")
        {
            if (parameters.ValueKind != JsonValueKind.Object || parameters.EnumerateObject().Any(item => item.Name is not ("width" or "height" or "sceneAssetId")))
                throw new InvalidOperationException("Only physical width, height and sceneAssetId are accepted.");
            if (Gui.GetCurrentContext().OpenPopupStack.Size != 0) throw new InvalidOperationException("A popup blocks benchmark layout navigation.");
            var entry = documents.Active ?? throw new InvalidOperationException("The actual scene document is unavailable.");
            if (entry.Document is not EntityHierarchyDocument scene || scene.Asset is not Stride.Assets.Entities.SceneAsset || Text("sceneAssetId") != scene.Asset.Id.ToString()
                || !sceneDocumentViews.TryGetValue(entry.Id, out var view) || !IsCurrentSceneDocumentView(view)
                || sceneDocumentViews.Values.Count(candidate => candidate.Viewport.Visible) != 1)
                throw new InvalidOperationException("Benchmark requires the exact canonical active visible scene owner, with no concurrent scene views.");
            var group = DocumentGroupPanel.Model.Find(entry) ?? throw new InvalidOperationException("The actual scene document group is unavailable.");
            if (!ReferenceEquals(group.Active, entry)) throw new InvalidOperationException("The canonical scene must be the group's active document.");
            return BeginMatchedBenchmarkViewport(group.WindowName, view.Viewport,
                parameters.GetProperty("width").GetInt32(), parameters.GetProperty("height").GetInt32());
        }
        if (target == "editor.shell" && action == "restart-frame-measurement") return RestartFrameStageMeasurements();
        if (TryVisualCaptureDraftDialogAction(target, action, parameters, out var draftResult)) return draftResult!;
        if ((action is "close" or "cancel") && (target is "dialogs.about" or "dialogs.settings"))
        {
            var name = target == "dialogs.about" ? EditorLocalization.Widget("About Stride", "About") : EditorLocalization.Widget("Settings", "Title");
            var stack = Gui.GetCurrentContext().OpenPopupStack;
            for (var index = 0; index < stack.Size; index++)
            {
                var window = stack[index].Window;
                if (window == null || Marshal.PtrToStringUTF8((nint)window->Name) != name) continue;
                ImGuiP.ClosePopupToLevel(index, true);
                return new { closed = true, saved = false };
            }
            throw new InvalidOperationException("The requested modal is not open.");
        }
        if (target == "dialogs.settings" && action == "select-category")
        {
            var settingsOpen = false;
            var popups = Gui.GetCurrentContext().OpenPopupStack;
            for (var i = 0; i < popups.Size; i++)
                if (popups[i].Window != null && Marshal.PtrToStringUTF8((nint)popups[i].Window->Name) == EditorLocalization.Widget("Settings", "Title")) settingsOpen = true;
            if (!settingsOpen) throw new InvalidOperationException("Open Settings before selecting a category.");
            settingsCategory = Text("name") switch
            {
                "Interface" => 0, "Scene editor" => 1, "Themes" => 2, "External tools" => 3,
                "Logging" => 5, "Environment" => 6, "Tools" => 7, "Script editor" => 8, "UI editor" => 10, "Remote" => 11,
                "Key bindings" or "Scene editor/Key bindings" => 9,
                "Viewport settings" or "Scene editor/Viewport settings" => 4,
                _ => throw new InvalidOperationException("This settings category is not implemented.")
            };
            return new { category = Text("name"), settingsCategory };
        }
        if (target == "editor.shell" && action == "set-window")
        {
            var form = Control.FromHandle(Window.NativeWindow.Handle)?.FindForm() ?? throw new InvalidOperationException("This host does not support native window sizing.");
            var state = Text("state") ?? "normal";
            if (state is not ("normal" or "maximized")) throw new InvalidOperationException("Supported window states are normal and maximized.");
            if (WindowsBackgroundLaunch.Enabled)
            {
                if (state != "normal" || form.WindowState != FormWindowState.Normal)
                    throw new InvalidOperationException("Background capture supports normal window bounds only.");
                var dpiScale = WindowsDpi.GetScale(Window.NativeWindow.Handle);
                int Dimension(string key, int fallback) => parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty(key, out var value) ? value.GetInt32() : fallback;
                var backgroundWidth = Dimension("width", (int)(form.Width / dpiScale));
                var backgroundHeight = Dimension("height", (int)(form.Height / dpiScale));
                if (backgroundWidth < 320 || backgroundHeight < 240 || backgroundWidth > 8192 || backgroundHeight > 8192)
                    throw new ArgumentOutOfRangeException(nameof(parameters), "Window dimensions must be valid outer DIP dimensions.");
                var backgroundLeft = form.Left; var backgroundTop = form.Top;
                if (parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("left", out var leftValue)) backgroundLeft = (int)Math.Round(leftValue.GetDouble() * dpiScale);
                if (parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("top", out var topValue)) backgroundTop = (int)Math.Round(topValue.GetDouble() * dpiScale);
                WindowsBackgroundLaunch.SetBounds(form.Handle, backgroundLeft, backgroundTop,
                    (int)Math.Round(backgroundWidth * dpiScale), (int)Math.Round(backgroundHeight * dpiScale));
                return new { state = form.WindowState.ToString(), physicalBounds = new { x = form.Left, y = form.Top, width = form.Width, height = form.Height }, dpi = form.DeviceDpi };
            }
            if (state == "maximized") form.WindowState = FormWindowState.Maximized;
            else
            {
                form.WindowState = FormWindowState.Normal;
                var scale = WindowsDpi.GetScale(Window.NativeWindow.Handle);
                int Number(string key, int fallback) => parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty(key, out var value) ? value.GetInt32() : fallback;
                var width = Number("width", (int)(form.Width / scale)); var height = Number("height", (int)(form.Height / scale));
                if (width < 320 || height < 240 || width > 8192 || height > 8192) throw new ArgumentOutOfRangeException(nameof(parameters), "Window dimensions must be valid outer DIP dimensions.");
                form.Size = new System.Drawing.Size((int)Math.Round(width * scale), (int)Math.Round(height * scale));
                if (parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("left", out var left)) form.Left = (int)Math.Round(left.GetDouble() * scale);
                if (parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("top", out var top)) form.Top = (int)Math.Round(top.GetDouble() * scale);
            }
            return new { state = form.WindowState.ToString(), physicalBounds = new { x = form.Left, y = form.Top, width = form.Width, height = form.Height }, dpi = form.DeviceDpi };
        }
        if (Gui.GetCurrentContext().OpenPopupStack.Size > 0) throw new InvalidOperationException("Close the current modal before selecting scene or asset content.");
        if (target == "editor.properties" && action == "scroll")
        {
            if (!showInspector || !PropertyGrid.Visible || !IsCurrentAssetPropertyContext
                || assetBrowser.SelectedCount != 1 || assetBrowser.PrimaryEntry is not { } propertyAsset)
                throw new InvalidOperationException("Reveal a single current asset's Property grid before scrolling it.");
            if (Text("assetId") is { } certificate && propertyAsset.Id.ToString() != certificate)
                throw new InvalidOperationException("The requested property asset is not active.");
            var propertyWindow = ImGuiP.FindWindowByName(EditorPanels.PropertyGrid.DisplayWindowName);
            if (propertyWindow.IsNull || !propertyWindow.Active || propertyWindow.Hidden || propertyWindow.SkipItems
                || Gui.GetFrameCount() - propertyWindow.LastFrameActive is not (0 or 1)
                || propertyWindow.DockIsActive && (!propertyWindow.DockTabIsVisible
                    || !propertyWindow.DockNode.IsNull && !propertyWindow.DockNode.VisibleWindow.IsNull
                        && propertyWindow.DockNode.VisibleWindow.ID != propertyWindow.ID))
                throw new InvalidOperationException("The actual Property grid body is not visible.");
            if (parameters.ValueKind != JsonValueKind.Object || !parameters.TryGetProperty("fraction", out var fractionValue)
                || !fractionValue.TryGetSingle(out var fraction) || !float.IsFinite(fraction) || fraction < 0 || fraction > 1)
                throw new ArgumentOutOfRangeException(nameof(parameters), "Scroll fraction must be finite and between zero and one.");
            var maximum = propertyWindow.ScrollMax.Y;
            if (!float.IsFinite(maximum) || maximum < 0) throw new InvalidOperationException("Property grid scroll bounds are unavailable.");
            var requested = fraction * maximum;
            ImGuiP.SetScrollY(propertyWindow, requested);
            return new { assetId = propertyAsset.Id.ToString(), windowId = propertyWindow.ID,
                fraction, requestedScrollY = requested, scrollMaxY = maximum, pending = true };
        }
        if (target == "scene.hierarchy" && action == "create-component-catalog")
            return CreateVisualCaptureComponentCatalog(parameters);
        if (target == "scene.hierarchy" && action == "expand-entity")
            return ExpandVisualCaptureHierarchyEntity(parameters);
        if (action == "close")
        {
            switch (target)
            {
                case "assets.references": showAssetReferences = false; break;
                case "assets.errors": showAssetErrors = false; break;
                case "editor.history": showEditHistory = false; break;
                case "editor.properties": showInspector = false; break;
                case "scene.hierarchy": showHierarchy = false; break;
                case "assets.preview": showAssetPreview = false; break;
                case "assets.browser": showAssets = false; break;
                case "assets.folders": showSolutionExplorer = false; break;
                case "editor.output": showOutput = false; focusOutput = false; break;
                default: throw new InvalidOperationException("Only non-document panels can be closed by this navigation action.");
            }
            return new { closed = true, saved = false };
        }
        if (target == "scene.hierarchy" && action == "select-entity")
        {
            var owner = document ?? throw new InvalidOperationException("No hierarchy document is loaded.");
            if (Text("assetId") is { } assetId && owner.Asset.Id.ToString() != assetId)
                throw new InvalidOperationException("The requested hierarchy asset is not active.");
            var name = Text("name"); var id = Text("id");
            var matches = owner.Asset.Hierarchy.Parts.Values.Select(part => part.Entity)
                .Where(entity => id != null ? entity.Id.ToString() == id : entity.Name == name).ToArray();
            if (matches.Length != 1) throw new InvalidOperationException("Select a unique actual entity by name or ID.");
            SelectCompositeHierarchyEntity(owner, matches[0], false);
            return new { entityId = matches[0].Id.ToString(), name = matches[0].Name, assetId = owner.Asset.Id.ToString() };
        }
        if (target == "assets.browser" && action is "select-asset" or "open-asset-editor" or "preview-asset")
        {
            if ((action is "open-asset-editor" or "preview-asset") && !CanStartAssetEdit)
                throw new InvalidOperationException("Wait for project, preview and authoring operations before opening an asset view.");
            var name = Text("name"); var id = Text("id");
            var matches = workspace?.Assets.Where(asset => id != null ? asset.Id.ToString() == id
                : asset.Name == name || Path.GetFileName(asset.Name) == name).ToArray() ?? [];
            if (matches.Length != 1) throw new InvalidOperationException("Select a unique actual asset by name or ID.");
            assetBrowser.RestoreSelection([matches[0].Id], matches[0].Id);
            selectedAssetPath = matches[0].Path;
            ActivateAssetPropertyContext();
            if (action is "open-asset-editor" or "preview-asset")
            {
                // Use the same canonical editor/preview dispatch as the browser;
                // imported assets without a separate editor retain their normal
                // preview and selected-asset Property grid presentation.
                var hasEditor = action == "open-asset-editor" && CanEditAssetContent(matches[0]);
                var before = ReadVisualCaptureFailures().LastOrDefault()?.Sequence ?? 0;
                var accepted = Services.Commands.TryExecute(() =>
                {
                    if (hasEditor) OpenBrowserAsset(matches[0]);
                    else PreviewBrowserAsset(matches[0]);
                });
                var failure = ReadVisualCaptureFailures().LastOrDefault(entry => entry.Sequence > before && entry.Module == "Editor");
                if (!accepted || failure != null)
                    throw new InvalidOperationException("Native asset opening failed: " + (failure?.Text ?? statusMessage));
                showInspector = true;
                return new { assetId = matches[0].Id.ToString(), name = matches[0].Name,
                    presentation = hasEditor ? "editor" : "preview-and-properties", accepted, pending = true,
                    statusMessage, canonicalDocument = documents.Find(matches[0].Path)?.Id.ToString() };
            }
            return new { assetId = matches[0].Id.ToString(), name = matches[0].Name };
        }
        throw new InvalidOperationException("This target/action pair is not an allowlisted navigation operation.");
    }
}


