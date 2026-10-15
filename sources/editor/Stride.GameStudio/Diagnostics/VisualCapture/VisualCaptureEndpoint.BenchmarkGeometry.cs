using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AvalonDock.Controls;
using AvalonDock.Layout;
using Stride.Assets.Presentation.AssetEditors.GameEditor.Services;
using Stride.Core.Presentation.Controls;
using Stride.Core.Assets.Editor.Services;
using Stride.Assets.Entities;
using Stride.Assets.Presentation.AssetEditors.GameEditor.ViewModels;
using Stride.Assets.Presentation.AssetEditors.SceneEditor.Services;
using Stride.Assets.Presentation.AssetEditors.SceneEditor.ViewModels;
using Stride.GameStudio.AssetsEditors;
using Stride.GameStudio.ViewModels;

namespace Stride.GameStudio.Diagnostics.VisualCapture;

internal sealed partial class VisualCaptureEndpoint
{
    private static object matchedBenchmarkGeometryEvidence;
    private async Task<object> HandleMatchedBenchmarkViewportAsync(CaptureRequest request)
    {
        RequireParameters(request, "width", "height", "sceneAssetId");
        RequireMainNavigation(Application.Current.MainWindow);
        var id = StringParameter(request, "sceneAssetId", required: true);
        var session = GameStudioViewModel.GameStudio?.Session ?? throw new InvalidOperationException("The actual scene session is unavailable.");
        var manager = session.ServiceProvider.TryGet<IAssetEditorsManager>() as AssetEditorsManager
            ?? throw new InvalidOperationException("The actual asset-editor registry is unavailable.");
        var selected = session.AllAssets.Where(asset => asset.Asset is SceneAsset)
            .Select(asset => new { Asset = asset, State = manager.InspectCaptureEditor(asset) })
            .Where(item => item.State is { Visible: true, Selected: true, InitializationCompleted: true }).ToArray();
        if (selected.Length != 1 || selected[0].Asset.Id.ToString() != id || selected[0].State.Visual == null)
            throw new InvalidOperationException("Benchmark requires the exact canonical selected visible scene with no concurrent scene editors.");
        var visual = selected[0].State.Visual;
        if (FindHierarchyEditor(visual) is not SceneEditorViewModel editor)
            throw new InvalidOperationException("The selected native scene editor does not own its actual controller.");
        // The product-owned protected Controller is read only after canonical
        // registry identity checks; native telemetry executes on its game queue.
        var controllerProperty = typeof(GameEditorViewModel).GetProperty("Controller", BindingFlags.Instance | BindingFlags.NonPublic);
        if (controllerProperty?.GetValue(editor) is not SceneEditorController controller)
            throw new InvalidOperationException("The selected scene's actual native controller is unavailable.");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        cancellation.CancelAfter(Math.Clamp(request.TimeoutMs, 100, 120000));
        var result = await SetMatchedBenchmarkViewportAsync(visual, request.Parameters.GetProperty("width").GetInt32(),
            request.Parameters.GetProperty("height").GetInt32(), controller.CaptureMatchedBenchmarkStateAsync, cancellation.Token);
        return new { protocolVersion = 1, id = request.Id, status = "ok", processId = Environment.ProcessId,
            backend = "wpf", frame, action = new { name = request.Action, requestedTarget = request.Target, result } };
    }
    // Dispatcher-owned native layout mutation only. The callback observes its
    // actual game on that owner's queue, including actual presenter dimensions.
    private Task<object> SetMatchedBenchmarkViewportAsync(FrameworkElement sceneVisual, int width, int height,
        Func<Task<MatchedBenchmarkGameState>> inspectGame, CancellationToken cancellation)
        => sceneVisual.Dispatcher.InvokeAsync(() => SetMatchedBenchmarkViewportOnDispatcherAsync(sceneVisual,
            width, height, inspectGame, cancellation), DispatcherPriority.Normal, cancellation).Task.Unwrap();

    private async Task<object> SetMatchedBenchmarkViewportOnDispatcherAsync(FrameworkElement sceneVisual, int width, int height,
        Func<Task<MatchedBenchmarkGameState>> inspectGame, CancellationToken cancellation)
    {
        if (!Environment.GetCommandLineArgs().Contains("--matched-benchmark") || width is < 64 or > 4096 || height is < 64 or > 4096)
            throw new InvalidOperationException("A bounded explicit benchmark capture is required.");
        var window = Window.GetWindow(sceneVisual);
        if (window == null || !ReferenceEquals(window, Application.Current.MainWindow) || !window.IsEnabled)
            throw new InvalidOperationException("The actual scene must remain in its enabled original main window.");
        var hosts = Descendants(sceneVisual).OfType<GameEngineHost>().Where(host => host.IsVisible).ToArray();
        if (hosts.Length != 1) throw new InvalidOperationException("Select one actual native scene viewport.");
        var host = hosts[0];
        LayoutDocumentControl document = null;
        for (DependencyObject current = host; current != null; current = VisualTreeHelper.GetParent(current))
            if (current is LayoutDocumentControl candidate) { document = candidate; break; }
        if (document?.Model == null) throw new InvalidOperationException("Actual scene document dock ancestry is unavailable.");
        var original = new Dictionary<(object Owner, string Name), GridLength>();
        var originalPanes = new Dictionary<object, (double Width, double Height, ILayoutContainer Parent)>();
        var evidence = new List<object>();
        var originalHostWidth = host.ActualWidth; var originalHostHeight = host.ActualHeight;
        try
        {
            var stable = 0;
            for (var pass = 0; pass < 12; pass++)
            {
                cancellation.ThrowIfCancellationRequested();
                if (!host.IsVisible || !window.IsEnabled || !document.Model.IsSelected)
                    throw new InvalidOperationException("The actual benchmark scene owner became hidden or blocked.");
                window.UpdateLayout();
                await Dispatcher.Yield(DispatcherPriority.Render);
                var dpi = VisualTreeHelper.GetDpi(host);
                var actualWidth = (int)Math.Round(host.ActualWidth * dpi.DpiScaleX);
                var actualHeight = (int)Math.Round(host.ActualHeight * dpi.DpiScaleY);
                var native = await inspectGame().WaitAsync(TimeSpan.FromSeconds(5), cancellation);
                evidence.Add(new { stage = "observed", pass, actualWidth, actualHeight, native,
                    dpiScaleX = dpi.DpiScaleX, dpiScaleY = dpi.DpiScaleY });
                matchedBenchmarkGeometryEvidence = new { outcome = "adjusting", passes = evidence.ToArray() };
                if (Math.Abs(actualWidth - width) <= 1 && Math.Abs(actualHeight - height) <= 1
                    && Math.Abs(native.TargetWidth - width) <= 1 && Math.Abs(native.TargetHeight - height) <= 1)
                {
                    if (++stable < 2) continue;
                    var origin = host.PointToScreen(new Point());
                    matchedBenchmarkGeometryEvidence = new { outcome = "matched", passes = evidence.ToArray() };
                    return new { requestedWidth = width, requestedHeight = height, actualWidth, actualHeight,
                        physicalRect = new { x = origin.X, y = origin.Y, width = actualWidth, height = actualHeight },
                        passes = pass + 1, actualAuthority = native, evidence = evidence.ToArray(),
                        control = "native existing splitter sizes; actual child HWND and presenter, no clamp or stretch" };
                }
                stable = 0;
                Adjust(Orientation.Horizontal, (width - actualWidth) / dpi.DpiScaleX);
                Adjust(Orientation.Vertical, (height - actualHeight) / dpi.DpiScaleY);
            }
            throw new InvalidOperationException("Actual dock constraints prevented matching the requested viewport in 12 passes.");
        }
        catch
        {
            foreach (var pair in original) Property(pair.Key.Owner, pair.Key.Name).SetValue(pair.Key.Owner, pair.Value);
            window.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.Render);
            await Dispatcher.Yield(DispatcherPriority.Render);
            var panes = originalPanes.Select(pair =>
            {
                var control = FindPane(pair.Key);
                return new { modelType = pair.Key.GetType().FullName, originalWidth = pair.Value.Width, originalHeight = pair.Value.Height,
                    actualWidth = control.ActualWidth, actualHeight = control.ActualHeight,
                    topologyPreserved = pair.Key is ILayoutElement model && ReferenceEquals(model.Parent, pair.Value.Parent) };
            }).ToArray();
            var nativeRollback = (MatchedBenchmarkGameState)null;
            string nativeRollbackError = null;
            try { nativeRollback = await inspectGame().WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (Exception error) { nativeRollbackError = error.Message; }
            matchedBenchmarkGeometryEvidence = new { outcome = "rollback-observed", passes = evidence.ToArray(), panes,
                originalHostWidth, originalHostHeight, actualHostWidth = host.ActualWidth, actualHostHeight = host.ActualHeight,
                hostRestored = Math.Abs(host.ActualWidth - originalHostWidth) < .75 && Math.Abs(host.ActualHeight - originalHostHeight) < .75,
                restoredGridLengths = original.Select(pair => new { modelType = pair.Key.Owner.GetType().FullName, property = pair.Key.Name,
                    originalValue = pair.Value.Value, originalUnit = pair.Value.GridUnitType.ToString(),
                    exactRestored = Equals(Property(pair.Key.Owner, pair.Key.Name).GetValue(pair.Key.Owner), pair.Value) }).ToArray(),
                nativeRollback, nativeRollbackError };
            throw;
        }
        void Adjust(Orientation axis, double deficit)
        {
            if (Math.Abs(deficit) <= .5) return;
            ILayoutElement branch = document.Model;
            for (var depth = 0; branch.Parent != null && depth < 64; depth++)
            {
                var parent = branch.Parent;
                var orientation = parent switch
                {
                    LayoutPanel panel => (Orientation?)panel.Orientation,
                    LayoutDocumentPaneGroup group => group.Orientation,
                    LayoutAnchorablePaneGroup group => group.Orientation,
                    _ => null,
                };
                if (orientation == axis)
                {
                    var siblings = parent.Children.Where(child => !ReferenceEquals(child, branch)).ToArray();
                    if (siblings.Length != 1) throw new InvalidOperationException("Benchmark resizing requires one existing sibling on its selected split.");
                    var sibling = siblings[0];
                    var dockName = axis == Orientation.Horizontal ? "DockWidth" : "DockHeight";
                    var ownControl = FindPane(branch); var siblingControl = FindPane(sibling);
                    originalPanes.TryAdd(branch, (ownControl.ActualWidth, ownControl.ActualHeight, branch.Parent));
                    originalPanes.TryAdd(sibling, (siblingControl.ActualWidth, siblingControl.ActualHeight, sibling.Parent));
                    var ownSize = axis == Orientation.Horizontal ? ownControl.ActualWidth : ownControl.ActualHeight;
                    var siblingSize = axis == Orientation.Horizontal ? siblingControl.ActualWidth : siblingControl.ActualHeight;
                    var delta = deficit * .75;
                    if (!double.IsFinite(ownSize + siblingSize) || ownSize + delta < 48 || siblingSize - delta < 48)
                        throw new InvalidOperationException("Requested viewport violates existing visible pane constraints.");
                    evidence.Add(new { stage = "splitter-write", axis = axis.ToString(), deficit, delta,
                        branchType = branch.GetType().FullName, siblingType = sibling.GetType().FullName,
                        actualBranch = ownSize, actualSibling = siblingSize, requestedBranch = ownSize + delta, requestedSibling = siblingSize - delta,
                        branchControl = ownControl.GetType().FullName, siblingControl = siblingControl.GetType().FullName,
                        originalBranchGrid = Property(branch, dockName).GetValue(branch).ToString(), originalSiblingGrid = Property(sibling, dockName).GetValue(sibling).ToString() });
                    Set(branch, ownSize + delta); Set(sibling, siblingSize - delta); return;
                    void Set(object owner, double value)
                    {
                        var property = Property(owner, dockName);
                        original.TryAdd((owner, dockName), (GridLength)property.GetValue(owner));
                        property.SetValue(owner, new GridLength(value, GridUnitType.Star));
                    }
                }
                branch = parent;
            }
            throw new InvalidOperationException("The scene layout has no adjustable splitter on the required axis.");
        }
        FrameworkElement FindPane(object model)
        {
            var controls = Descendants(window).OfType<FrameworkElement>().Where(element => element.IsVisible
                && element is ILayoutControl layout && ReferenceEquals(layout.Model, model)).Distinct().ToArray();
            if (controls.Length != 1 || controls[0].ActualWidth <= 0 || controls[0].ActualHeight <= 0)
                throw new InvalidOperationException("The exact native layout model has no unique visible measured pane control.");
            return controls[0];
        }
        static PropertyInfo Property(object nativeLayout, string name)
            => nativeLayout.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)
                ?? throw new InvalidOperationException("The native AvalonDock layout does not expose " + name + ".");
    }
}
