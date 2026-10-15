using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Stride.Assets.Presentation.AssetEditors.AssetCompositeGameEditor.ViewModels;
using Stride.Assets.Presentation.AssetEditors.EntityHierarchyEditor.ViewModels;
using Stride.Core.Assets.Editor.View;
using Stride.GameStudio.View;
using Stride.GameStudio.ViewModels;

namespace Stride.GameStudio.Diagnostics.VisualCapture;

internal sealed partial class VisualCaptureEndpoint
{
    private async Task<object> HandleAction(CaptureRequest request)
    {
        if (request.Target == "scene.viewport" && request.Action == "set-viewport-size")
            return await HandleMatchedBenchmarkViewportAsync(request);
        var target = Resolve(List(request), request.Target);
        if (discovery.Truncated) throw new CaptureEndpointException("partial_inventory", "Action target discovery reached its configured bound.");
        if (request.Parameters.ValueKind is not (JsonValueKind.Object or JsonValueKind.Undefined or JsonValueKind.Null))
            throw new CaptureEndpointException("invalid_parameters", "Action parameters must be an object.");
        var source = target.Source is CaptureDiscovery.DockSource dock ? dock.Visual : target.Source;
        var window = source as Window ?? (source is DependencyObject dependency ? Window.GetWindow(dependency) : null);
        var before = frame; var watch = Stopwatch.StartNew();
        Window openedModal = null;
        object result;
        Func<bool> completed;
        switch (request.Action)
        {
            case "open-asset-editor":
                (result, completed) = await OpenCaptureAssetEditor(request, target, window);
                break;
            case "open-modal":
                RequireParameters(request, "name", "templateId");
                RequireMainNavigation(window);
                if (request.Target != "editor.shell" && !target.SemanticIds.Contains("editor.shell"))
                    throw new CaptureEndpointException("action_not_allowed", "Modal capture navigation requires the actual editor shell target.");
                openedModal = await CreateCaptureModal(request);
                window = openedModal;
                var openState = QueueCaptureModal(openedModal);
                completed = () =>
                {
                    if (openState.Error != null) throw new CaptureEndpointException("modal_open_failed", openState.Error.Message);
                    return openedModal.IsVisible;
                };
                result = new { modal = StringParameter(request, "name", true), dialog = openedModal.GetType().FullName, generationAttached = false };
                break;
            case "select-template":
                RequireParameters(request, "name", "id");
                result = SelectCaptureTemplate(window, request);
                completed = () => window is { IsVisible: true, IsEnabled: true };
                break;
            case "select-group":
                RequireParameters(request, "name");
                result = SelectCaptureTemplateGroup(window, StringParameter(request, "name", true));
                completed = () => window is { IsVisible: true, IsEnabled: true };
                break;
            case "set-template-option":
                RequireParameters(request, "value");
                result = SetCaptureTemplateOption(window, source, request);
                completed = () => window is { IsVisible: true, IsEnabled: true };
                break;
            case "cancel":
            case "close":
                RequireParameters(request);
                if (window == null || !window.IsEnabled) throw new CaptureEndpointException("modal_blocked", "The target dialog is absent or blocked by another modal.");
                if (window is SettingsWindow settings)
                {
                    var cancel = Descendants(settings).OfType<Button>().SingleOrDefault(button => button.IsCancel && ReferenceEquals(button.DataContext, settings.DataContext));
                    if (cancel == null || !cancel.IsEnabled) throw new CaptureEndpointException("action_unavailable", "The original Settings Cancel action is unavailable.");
                    cancel.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, cancel));
                }
                else if (window is AboutPage about) about.Close(); // Exact original ButtonCloseClick behavior.
                else if (!TryCancelCaptureModal(window)) throw new CaptureEndpointException("action_not_allowed", "This dialog has no source-proven safe cancellation action.");
                completed = () => !window.IsVisible;
                result = new { closed = true, dialog = window.GetType().FullName, saved = false };
                break;
            case "select-category":
                RequireParameters(request, "name");
                if (window is not SettingsWindow categoryWindow || !categoryWindow.IsEnabled) throw new CaptureEndpointException("modal_blocked", "The Settings dialog is absent or blocked.");
                var name = StringParameter(request, "name", true);
                var model = categoryWindow.DataContext;
                var categories = Categories(model).Where(candidate => string.Equals(ReadProperty(candidate, "Name") as string, name, StringComparison.OrdinalIgnoreCase)
                    || string.Equals((ReadProperty(candidate, "Path") as string)?.Trim('/'), name.Trim('/'), StringComparison.OrdinalIgnoreCase)).ToArray();
                if (categories.Length != 1) throw new CaptureEndpointException(categories.Length == 0 ? "category_not_found" : "ambiguous_category", "Specify one actual Settings category name or path.");
                var category = categories[0];
                model.GetType().GetProperty("SelectedCategory", BindingFlags.Public | BindingFlags.Instance)!.SetValue(model, category);
                RevealSettingsCategory(categoryWindow, category);
                categoryWindow.UpdateLayout();
                foreach (var item in Descendants(categoryWindow).OfType<FrameworkElement>().Where(element => ReferenceEquals(element.DataContext, category)))
                    item.BringIntoView();
                completed = () => ReferenceEquals(ReadProperty(model, "SelectedCategory"), category);
                result = new { name = ReadProperty(category, "Name"), path = ReadProperty(category, "Path") };
                break;
            case "select-entity":
                RequireParameters(request, "id", "name", "assetId");
                RequireMainNavigation(window);
                if (request.Target != "scene.hierarchy" && !target.SemanticIds.Contains("scene.hierarchy"))
                    throw new CaptureEndpointException("action_not_allowed", "Entity selection requires the actual hierarchy target.");
                var editor = FindHierarchyEditor(source);
                var entityId = StringParameter(request, "id"); var entityName = StringParameter(request, "name"); var assetId = StringParameter(request, "assetId");
                if (string.IsNullOrWhiteSpace(entityId) && string.IsNullOrWhiteSpace(entityName)) throw new CaptureEndpointException("invalid_parameters", "Entity selection requires id or name.");
                var entities = HierarchyParts(editor.RootPart).OfType<EntityViewModel>().Where(candidate =>
                    (entityId == null || string.Equals(candidate.AssetSideEntity.Id.ToString(), entityId, StringComparison.OrdinalIgnoreCase))
                    && (entityName == null || string.Equals(candidate.Name, entityName, StringComparison.Ordinal))
                    && (assetId == null || string.Equals(candidate.Asset.Id.ToString(), assetId, StringComparison.OrdinalIgnoreCase))).ToArray();
                if (entities.Length != 1) throw new CaptureEndpointException(entities.Length == 0 ? "entity_not_found" : "ambiguous_entity", "Specify one actual entity id/name and assetId when necessary.");
                var entity = entities[0];
                if (!entity.IsLoaded || entity.IsLocked) throw new CaptureEndpointException("entity_unavailable", "The entity is unloaded or locked.");
                editor.SelectedItems.Clear(); editor.SelectedItems.Add(entity);
                completed = () => editor.SelectedItems.Count == 1 && editor.SelectedItems.Contains(entity);
                result = new { id = entity.AssetSideEntity.Id, entity.Name, assetId = entity.Asset.Id.ToString() };
                break;
            case "select-asset":
                RequireParameters(request, "id", "name");
                RequireMainNavigation(window);
                if (request.Target != "assets.browser" && !target.SemanticIds.Contains("assets.browser"))
                    throw new CaptureEndpointException("action_not_allowed", "Asset selection requires the actual asset browser target.");
                var session = GameStudioViewModel.GameStudio?.Session;
                var browser = session?.ActiveAssetView ?? throw new CaptureEndpointException("action_unavailable", "The active asset browser is unavailable.");
                var selectedId = StringParameter(request, "id"); var selectedName = StringParameter(request, "name");
                if (string.IsNullOrWhiteSpace(selectedId) && string.IsNullOrWhiteSpace(selectedName)) throw new CaptureEndpointException("invalid_parameters", "Asset selection requires id or name.");
                var assets = session.AllAssets.Where(candidate => (selectedId == null || string.Equals(candidate.Id.ToString(), selectedId, StringComparison.OrdinalIgnoreCase))
                    && (selectedName == null || string.Equals(candidate.Name, selectedName, StringComparison.Ordinal) || string.Equals(candidate.Url, selectedName, StringComparison.Ordinal))).ToArray();
                if (assets.Length != 1) throw new CaptureEndpointException(assets.Length == 0 ? "asset_not_found" : "ambiguous_asset", "Specify one actual asset id, name or URL.");
                var asset = assets[0];
                if (!browser.SelectAssetCommand.CanExecute(asset)) throw new CaptureEndpointException("action_unavailable", "The original asset selection command is disabled.");
                browser.SelectAssetCommand.Execute(asset);
                completed = () => browser.SelectedAssets.Count == 1 && ReferenceEquals(browser.SelectedAssets.Single(), asset);
                result = new { id = asset.Id.ToString(), asset.Name, asset.Url };
                break;
            case "set-window":
                RequireParameters(request, "width", "height", "left", "top", "state");
                var main = Application.Current.MainWindow;
                if (main == null || !main.IsEnabled) throw new CaptureEndpointException("modal_blocked", "The main window is absent or blocked by a modal.");
                if (!ReferenceEquals(window, main) || request.Target != "editor.shell" && !target.SemanticIds.Contains("editor.shell"))
                    throw new CaptureEndpointException("action_not_allowed", "Window geometry navigation is restricted to the actual main window.");
                var state = StringParameter(request, "state") ?? "normal";
                if (state is not ("normal" or "maximized")) throw new CaptureEndpointException("invalid_parameters", "Window state must be normal or maximized.");
                var width = NumberParameter(request, "width", main.RestoreBounds.IsEmpty ? main.ActualWidth : main.RestoreBounds.Width);
                var height = NumberParameter(request, "height", main.RestoreBounds.IsEmpty ? main.ActualHeight : main.RestoreBounds.Height);
                var left = NumberParameter(request, "left", main.RestoreBounds.IsEmpty ? double.IsFinite(main.Left) ? main.Left : 0 : main.RestoreBounds.Left);
                var top = NumberParameter(request, "top", main.RestoreBounds.IsEmpty ? double.IsFinite(main.Top) ? main.Top : 0 : main.RestoreBounds.Top);
                if (width < Math.Max(1, main.MinWidth) || height < Math.Max(1, main.MinHeight) || width > main.MaxWidth || height > main.MaxHeight)
                    throw new CaptureEndpointException("invalid_parameters", "Requested DIP dimensions violate the original main window size limits.");
                if (Environment.GetCommandLineArgs().Contains("--background-launch"))
                {
                    result = SetBackgroundCaptureWindow(main, state, width, height, left, top);
                    completed = () => main.WindowState == WindowState.Normal;
                    break;
                }
                main.WindowState = WindowState.Normal; main.Width = width; main.Height = height; main.Left = left; main.Top = top;
                if (state == "maximized") main.WindowState = WindowState.Maximized;
                main.UpdateLayout();
                completed = () => main.WindowState == (state == "maximized" ? WindowState.Maximized : WindowState.Normal);
                result = new { state, width, height, left, top, coordinateSpace = "screen-dip" };
                break;
            default: throw new CaptureEndpointException("action_not_allowed", "This action is outside capture navigation's explicit allowlist.");
        }
        var desired = Math.Clamp(request.SettleFrames, 0, 120); var timeout = Math.Clamp(request.TimeoutMs, 100, 120000);
        while (!completed() || frame - before < desired)
        {
            if (watch.ElapsedMilliseconds >= timeout) throw new CaptureEndpointException("action_timeout", "The actual navigation state/presentation did not settle before the timeout.");
            await Task.Delay(16, stop.Token);
        }
        var current = List(request);
        if (discovery.Truncated) throw new CaptureEndpointException("partial_inventory", "The settled action inventory reached its configured bound.");
        var after = openedModal != null ? current.FirstOrDefault(candidate => ReferenceEquals(candidate.Source, openedModal))
            : current.FirstOrDefault(candidate => candidate.Id == target.Id)
            ?? current.FirstOrDefault(candidate => candidate.SemanticIds.Contains(request.Target));
        return new { protocolVersion = 1, id = request.Id, status = "ok", processId = Environment.ProcessId, backend = "wpf", frame,
            action = new { name = request.Action, requestedTarget = request.Target, result, target = after, state = after?.State ?? "closed", settledWpfFrames = frame - before,
                geometry = window is { IsVisible: true } ? new { width = window.ActualWidth, height = window.ActualHeight, left = window.Left, top = window.Top,
                    state = window.WindowState.ToString().ToLowerInvariant(), coordinateSpace = "screen-dip" } : null } };
    }

    private static void RequireMainNavigation(Window window)
    {
        if (Application.Current.MainWindow is not { IsEnabled: true } || window != null && !window.IsEnabled)
            throw new CaptureEndpointException("modal_blocked", "A modal dialog blocks main editor navigation.");
    }
    private static void RequireParameters(CaptureRequest request, params string[] allowed)
    {
        if (request.Parameters.ValueKind != JsonValueKind.Object) return;
        if (request.Parameters.EnumerateObject().Any(property => !allowed.Contains(property.Name)))
            throw new CaptureEndpointException("invalid_parameters", "An action parameter is outside the explicit navigation allowlist.");
    }
    private static string StringParameter(CaptureRequest request, string name, bool required = false)
    {
        if (request.Parameters.ValueKind != JsonValueKind.Object || !request.Parameters.TryGetProperty(name, out var value))
        {
            if (required) throw new CaptureEndpointException("invalid_parameters", "Missing parameter: " + name);
            return null;
        }
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString())) throw new CaptureEndpointException("invalid_parameters", "Parameter must be a nonempty string: " + name);
        return value.GetString();
    }
    private static double NumberParameter(CaptureRequest request, string name, double fallback)
    {
        if (request.Parameters.ValueKind != JsonValueKind.Object || !request.Parameters.TryGetProperty(name, out var value)) return fallback;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number)) throw new CaptureEndpointException("invalid_parameters", "Parameter must be a finite DIP number: " + name);
        return number;
    }
    private static object ReadProperty(object model, string name) => model?.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(model);
    private static IEnumerable<object> Categories(object model)
    {
        var pending = new Stack<object>(((IEnumerable)ReadProperty(model, "Categories")).Cast<object>().Reverse());
        while (pending.TryPop(out var category))
        {
            yield return category;
            if (ReadProperty(category, "SubCategories") is IEnumerable children)
                foreach (var child in children.Cast<object>().Reverse()) pending.Push(child);
        }
    }
    private static void RevealSettingsCategory(SettingsWindow window, object category)
    {
        var tree = Descendants(window).OfType<Stride.Core.Presentation.Controls.TreeView>().SingleOrDefault();
        if (tree == null) throw new CaptureEndpointException("action_unavailable", "The actual Settings category tree is unavailable.");
        var path = new Stack<object>();
        for (var ancestor = category; ancestor != null; ancestor = ReadProperty(ancestor, "Parent")) path.Push(ancestor);
        ItemsControl parent = tree;
        while (path.TryPop(out var current))
        {
            parent.UpdateLayout();
            if (parent.ItemContainerGenerator.ContainerFromItem(current) is not Stride.Core.Presentation.Controls.TreeViewItem item)
                throw new CaptureEndpointException("category_unavailable", "The actual Settings category tree item is not realized.");
            if (path.Count != 0) item.IsExpanded = true;
            else { item.IsSelected = true; item.BringIntoView(); }
            parent = item;
        }
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var pending = new Stack<DependencyObject>(); pending.Push(root);
        var visited = new HashSet<DependencyObject>(ReferenceEqualityComparer.Instance);
        while (pending.TryPop(out var current))
        {
            if (!visited.Add(current)) continue;
            yield return current;
            if (current is Visual)
                for (var index = VisualTreeHelper.GetChildrenCount(current) - 1; index >= 0; index--) pending.Push(VisualTreeHelper.GetChild(current, index));
        }
    }
    private static EntityHierarchyEditorViewModel FindHierarchyEditor(object source)
    {
        if (source is not DependencyObject root) throw new CaptureEndpointException("action_unavailable", "The target has no actual hierarchy editor view.");
        foreach (var visual in Descendants(root).OfType<FrameworkElement>())
        {
            if (visual.DataContext is EntityHierarchyEditorViewModel editor) return editor;
            if (visual.DataContext is EntityHierarchyItemViewModel item) return item.Editor;
        }
        for (var current = root; current != null; current = current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
            if (current is FrameworkElement visual && visual.DataContext is EntityHierarchyEditorViewModel editor) return editor;
        throw new CaptureEndpointException("action_unavailable", "No actual entity hierarchy editor owns this target.");
    }
    private static IEnumerable<AssetCompositeItemViewModel> HierarchyParts(AssetCompositeItemViewModel root)
    {
        var pending = new Stack<AssetCompositeItemViewModel>(); pending.Push(root);
        while (pending.TryPop(out var current))
        {
            yield return current;
            foreach (var child in current.EnumerateChildren().Reverse()) pending.Push(child);
        }
    }
}
