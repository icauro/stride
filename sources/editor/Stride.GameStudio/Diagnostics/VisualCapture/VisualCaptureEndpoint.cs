using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AvalonDock.Layout;
using Stride.GameStudio.ViewModels;

namespace Stride.GameStudio.Diagnostics.VisualCapture;

/// <summary>Opt-in actual-presentation discovery and in-process GPU window capture IPC.</summary>
internal sealed partial class VisualCaptureEndpoint : IDisposable
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, WriteIndented = true };
    private readonly Dispatcher dispatcher;
    private readonly DispatcherTimer timer;
    private readonly CaptureDiscovery discovery = new();
    private readonly CaptureCatalog catalog;
    private readonly WindowGpuCaptureBridge windowCapture = new();
    private readonly HashSet<string> processed = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource stop = new();
    private readonly string directory, requests, responses, sessionId = Guid.NewGuid().ToString("N");
    private readonly DateTime startedAt = DateTime.UtcNow;
    private readonly string processStartFileTime;
    private bool busy, disposed;
    private long frame;
    private TimeSpan lastFrame;
    internal VisualCaptureEndpoint(Dispatcher dispatcher, string captureDirectory)
    {
        this.dispatcher = dispatcher;
        processStartFileTime = Process.GetCurrentProcess().StartTime.ToUniversalTime().ToFileTimeUtc().ToString(System.Globalization.CultureInfo.InvariantCulture);
        directory = Path.Combine(Path.GetFullPath(captureDirectory), "wpf-" + Environment.ProcessId + "-" + processStartFileTime);
        requests = Path.Combine(directory, "requests"); responses = Path.Combine(directory, "responses");
        Directory.CreateDirectory(requests); Directory.CreateDirectory(responses);
        catalog = new CaptureCatalog(Path.Combine(AppContext.BaseDirectory, "VisualCapture", "catalog.json"));
        WriteSession("running");
        StartMatchedActivationDiagnostics();
        CompositionTarget.Rendering += OnRendering;
        timer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, OnTick, dispatcher);
        timer.Start();
    }
    private void OnRendering(object sender, EventArgs args)
    {
        if (args is RenderingEventArgs render && render.RenderingTime != lastFrame) { lastFrame = render.RenderingTime; frame++; }
    }
    private void WriteSession(string status) => WriteAtomic(Path.Combine(directory, "session.json"), new
    {
        protocolVersion = 1, sessionId, processId = Environment.ProcessId, processStartFileTime, backend = "wpf", editorName = "Stride.GameStudio",
        captureDirectory = directory, startedAt, status, capabilities = new[] { "list", "capture", "show", "action" }, transport = "windows-graphics-capture",
        pixelCapture = "in-process DWM window GPU texture crop/readback"
    });
    private async void OnTick(object sender, EventArgs args)
    {
        if (busy || disposed) return;
        busy = true;
        try
        {
            var files = await Task.Run(() => Directory.EnumerateFiles(requests, "*.json").OrderBy(path => path, StringComparer.Ordinal).ToArray(), stop.Token);
            foreach (var path in files)
            {
                var name = Path.GetFileNameWithoutExtension(path);
                if (!processed.Add(name)) continue;
                if (name.Length > 128 || name.Length == 0 || name.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-' && character != '_')) continue;
                object response;
                try
                {
                    var request = JsonSerializer.Deserialize<CaptureRequest>(await File.ReadAllTextAsync(path, stop.Token), Json)
                        ?? throw new CaptureEndpointException("invalid_request", "The request is empty.");
                    if (request.ProtocolVersion != 1 || request.Id != name || request.SessionId != sessionId)
                        throw new CaptureEndpointException("invalid_request", "The protocol, request id, or session identity does not match this endpoint.");
                    response = await Handle(request);
                }
                catch (Exception error)
                {
                    response = new { protocolVersion = 1, id = name, status = "error", processId = Environment.ProcessId, backend = "wpf", frame,
                        error = new { code = error is CaptureEndpointException capture ? capture.Code : error is JsonException ? "invalid_json" : "endpoint_error", message = error.Message } };
                }
                await Task.Run(() => WriteAtomic(Path.Combine(responses, name + ".json"), response), stop.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* Diagnostics must not terminate the editor if its capture directory disappears. */ }
        finally { busy = false; }
    }
    private IReadOnlyList<CaptureTarget> List(CaptureRequest request) => ApplyModalAliases(catalog.Apply(discovery.Read(request.MaxDepth, request.MaxNodes)));
    private static CaptureTarget Resolve(IReadOnlyList<CaptureTarget> targets, string id)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new CaptureEndpointException("missing_target", "Specify an actual instance id or semantic target id.");
        var exact = targets.FirstOrDefault(target => target.Id == id);
        if (exact != null) return exact;
        var matches = targets.Where(target => target.SemanticIds.Contains(id)).ToArray();
        var visible = matches.Where(target => target.Visible).ToArray();
        if (visible.Length == 1) return visible[0];
        if (visible.Length > 1 || matches.Length > 1) throw new CaptureEndpointException("ambiguous_target", "More than one actual target matches. Choose an instance id returned by list.");
        return matches.SingleOrDefault() ?? throw new CaptureEndpointException("unknown_target", "No runtime or source-declared target has that id.");
    }
    private async Task<object> Handle(CaptureRequest request)
    {
        if (request.Operation == "action") return await HandleAction(request);
        if (request.Operation == "list")
        {
            var targets = List(request);
            return new { protocolVersion = 1, id = request.Id, status = "ok", processId = Environment.ProcessId, backend = "wpf", frame, targets,
                metadata = CaptureSelectionMetadata(),
                truncated = discovery.Truncated, inventoryComplete = !discovery.Truncated, boundsGeneration = frame.ToString(), discovery = new[] { "wpf-visual-tree", "wpf-logical-tree", "automation-peer-metadata", "presentation-sources", "avalondock-content", "process-native-windows" } };
        }
        if (request.Operation is not ("capture" or "show")) throw new CaptureEndpointException("unknown_operation", "Supported operations are list, capture, and show.");
        var target = Resolve(List(request), request.Target);
        if (discovery.Truncated) throw new CaptureEndpointException("partial_inventory", "Discovery reached its depth/node bound. Increase the limits before capturing.");
        if (request.Operation == "show" || request.Reveal) Reveal(target, request.Target);
        else if (!target.Visible) throw new CaptureEndpointException("target_" + target.State, target.Reason ?? "The target is not currently visible.");
        if (request.Focus) Focus(target);
        var before = frame; var desired = Math.Clamp(request.SettleFrames, 0, 120); var timeout = Math.Clamp(request.TimeoutMs, 100, 120000);
        var watch = Stopwatch.StartNew();
        while (frame - before < desired)
        {
            if (watch.ElapsedMilliseconds >= timeout) throw new CaptureEndpointException("settle_timeout", "The requested WPF presentation frames did not arrive.");
            await Task.Delay(16, stop.Token);
        }
        target = Resolve(List(request), request.Target);
        if (discovery.Truncated) throw new CaptureEndpointException("partial_inventory", "The settled inventory reached its depth/node bound.");
        if (!target.Visible || target.Rect.Width <= 0 || target.Rect.Height <= 0) throw new CaptureEndpointException("target_" + target.State, target.Reason ?? "The target has no visible physical pixel rectangle.");
        if (target.Clipped) throw new CaptureEndpointException("target_clipped", "The target is partially clipped. Reveal/scroll it fully before capturing.");
        if (request.Operation == "capture")
        {
            if (string.IsNullOrWhiteSpace(request.Output) || !Path.IsPathFullyQualified(request.Output) || !string.Equals(Path.GetExtension(request.Output), ".png", StringComparison.OrdinalIgnoreCase))
                throw new CaptureEndpointException("invalid_output", "Capture requires an absolute PNG output path.");
            Directory.CreateDirectory(Path.GetDirectoryName(request.Output));
            var metadata = await windowCapture.Capture(target, request.Output, Math.Max(100, timeout - (int)watch.ElapsedMilliseconds), stop.Token);
            var after = Resolve(List(request), target.Id);
            if (discovery.Truncated || !after.Visible || after.Clipped || after.Rect != target.Rect || after.Hwnd != target.Hwnd)
            {
                File.Delete(request.Output);
                throw new CaptureEndpointException("target_changed", "The target bounds or visibility changed across GPU window capture.");
            }
            return new { protocolVersion = 1, id = request.Id, status = "ok", processId = Environment.ProcessId, backend = "wpf", frame,
                capture = new { target, rect = target.Rect, coordinateSpace = "screen-physical", dpi = target.Dpi, transport = "windows-graphics-capture",
                    output = request.Output, hwnd = target.Hwnd, state = target.State, boundsGeneration = target.BoundsGeneration,
                    settledWpfFrames = frame - before, engineFramesVerified = false, nativeChildHwnd = target.NativeChildHwnd,
                    composition = "dwm-window-gpu-texture-readback", windowCapture = metadata } };
        }
        return new { protocolVersion = 1, id = request.Id, status = request.Operation == "capture" ? "ready" : "ok", processId = Environment.ProcessId, backend = "wpf", frame,
            capture = new { target, rect = target.Rect, coordinateSpace = "screen-physical", dpi = target.Dpi, transport = "screen", hwnd = target.Hwnd, state = target.State,
                boundsGeneration = target.BoundsGeneration, settledWpfFrames = frame - before, engineFramesVerified = false } };
    }
    private void Reveal(CaptureTarget target, string id)
    {
        if (target.Source == null)
        {
            ICommand command = id switch { "dialogs.about" => GameStudioViewModel.GameStudio?.OpenAboutPageCommand, "dialogs.settings" => GameStudioViewModel.GameStudio?.OpenSettingsWindowCommand, _ => null };
            if (command == null || !command.CanExecute(null)) throw new CaptureEndpointException("reveal_unavailable", "This dormant target has no permitted read-only navigation command.");
            if (Application.Current.MainWindow is { IsEnabled: false }) throw new CaptureEndpointException("modal_blocked", "An existing modal window blocks this navigation.");
            // Original modal commands enter a nested dispatcher. Queue them so
            // this request can settle/discover their real window while it is open.
            dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => { try { command.Execute(null); } catch (Exception) { } }));
            return;
        }
        var source = target.Source;
        if (source is CaptureDiscovery.DockSource dock)
        {
            if (dock.Dock is LayoutAnchorable anchorable && !anchorable.IsVisible) anchorable.Show();
            dock.Dock.IsSelected = true; dock.Dock.IsActive = true; source = dock.Visual;
        }
        if (source is DependencyObject dependency)
        {
            for (var current = dependency; current != null; current = current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
            {
                if (current is Expander expander) expander.IsExpanded = true;
                if (current is TreeViewItem tree) tree.IsExpanded = true;
                if (current is TabItem tab) tab.IsSelected = true;
            }
            if (source is MenuItem menu) menu.IsSubmenuOpen = true;
            if (source is Popup popup) popup.IsOpen = true;
            if (source is ContextMenu context)
            {
                if (context.PlacementTarget == null) throw new CaptureEndpointException("reveal_unavailable", "The context menu has no actual placement target yet.");
                context.IsOpen = true;
            }
            if (source is FrameworkElement visual) { visual.BringIntoView(); visual.UpdateLayout(); }
        }
    }
    private static void Focus(CaptureTarget target)
    {
        var source = target.Source is CaptureDiscovery.DockSource dock ? dock.Visual : target.Source;
        if (source is IntPtr native)
        {
            if (!CaptureNativeBounds.Activate(native)) throw new CaptureEndpointException("focus_failed", "Windows did not activate the requested native window.");
            return;
        }
        var window = source is Window owner ? owner : source is DependencyObject dependency ? Window.GetWindow(dependency) : null;
        if (window != null)
        {
            if (!window.IsEnabled) throw new CaptureEndpointException("modal_blocked", "The target window is blocked by a modal.");
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            if (!window.Activate()) throw new CaptureEndpointException("focus_failed", "Windows did not activate the requested window.");
        }
        if (source is UIElement element && element.Focusable) element.Focus();
    }
    private static void WriteAtomic(string path, object value)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(value, Json)); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true; timer.Stop(); stop.Cancel(); CompositionTarget.Rendering -= OnRendering;
        StopMatchedActivationDiagnostics();
        try { WriteSession("stopped"); } catch (Exception) { }
        stop.Dispose();
        windowCapture.Dispose();
    }
}
