using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using AvalonDock.Layout;
using Stride.Core.Presentation.Controls;
using Stride.GameStudio.View;

namespace Stride.GameStudio.Diagnostics.VisualCapture;

internal sealed class CaptureDiscovery
{
    private sealed class Identity { public string Id { get; } = Guid.NewGuid().ToString("N"); }
    private readonly ConditionalWeakTable<object, Identity> identities = new();
    private readonly Dictionary<object, CaptureTarget> objects = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<IntPtr, string> handles = [];
    private readonly List<CaptureTarget> targets = [];
    private readonly HashSet<DependencyObject> visited = new(ReferenceEqualityComparer.Instance);
    internal bool Truncated { get; private set; }
    private int maxDepth, maxNodes;
    private string Id(object value) => "wpf." + identities.GetValue(value, _ => new()).Id;
    internal IReadOnlyList<CaptureTarget> Read(int depth, int nodes)
    {
        targets.Clear(); objects.Clear(); handles.Clear(); visited.Clear(); Truncated = false;
        maxDepth = Math.Clamp(depth, 1, 512); maxNodes = Math.Clamp(nodes, 1, 250000);
        foreach (Window window in Application.Current.Windows) Visit(window, null, 0);
        foreach (var source in PresentationSource.CurrentSources.OfType<HwndSource>().ToArray())
        {
            if (source.RootVisual is not DependencyObject root) continue;
            var parent = CaptureNativeBounds.GetWindow(source.Handle, 4);
            Visit(root, handles.GetValueOrDefault(parent), 0);
        }
        foreach (var window in Application.Current.Windows.OfType<GameStudioWindow>())
        {
            var layout = window.Docking?.Layout; if (layout == null) continue;
            foreach (var content in layout.Descendents().OfType<LayoutContent>().Concat(layout.Hidden).Distinct())
            {
                var target = content.Content is DependencyObject dependency && objects.TryGetValue(dependency, out var visible) ? visible : null;
                if (target == null)
                {
                    target = new CaptureTarget { Id = Id(content), Source = content, Kind = "panel", Name = content.Title ?? content.ContentId ?? "", Type = content.GetType().FullName,
                        ParentId = objects.GetValueOrDefault(window)?.Id, State = "hidden", Reason = "The dock content has no currently presented visual." };
                    Add(target);
                    if (content.Content is DependencyObject child) Visit(child, target.Id, 0);
                }
                target.ContentId = content.ContentId; target.Source = new DockSource(content, target.Source);
            }
        }
        foreach (var hwnd in CaptureNativeBounds.ProcessWindows())
        {
            if (targets.Count >= maxNodes) { Truncated = true; break; }
            var bounds = CaptureNativeBounds.WindowRect(hwnd, true);
            var visible = CaptureNativeBounds.IsVisible(hwnd) && !bounds.IsEmpty && bounds.Width > 0 && bounds.Height > 0;
            var parent = CaptureNativeBounds.GetAncestor(hwnd, 1);
            var target = new CaptureTarget { Id = "native." + hwnd.ToInt64().ToString("X"), Source = hwnd, Kind = "native-window", Name = CaptureNativeBounds.Title(hwnd),
                Type = "Win32." + CaptureNativeBounds.ClassName(hwnd), ParentId = handles.GetValueOrDefault(parent), Hwnd = CaptureNativeBounds.Handle(hwnd),
                Visible = visible, State = visible ? "visible" : "hidden", Rect = CaptureNativeBounds.Rectangle(bounds), Dpi = new(CaptureNativeBounds.Dpi(hwnd), CaptureNativeBounds.Dpi(hwnd)),
                Foreground = CaptureNativeBounds.IsForeground(hwnd), BoundsSource = "native-client" };
            if (visible && parent != IntPtr.Zero)
            {
                var clipped = Rect.Intersect(bounds, CaptureNativeBounds.WindowRect(CaptureNativeBounds.GetAncestor(hwnd, 2), true));
                target.UnclippedRect = target.Rect; target.Rect = CaptureNativeBounds.Rectangle(clipped); target.Clipped = target.Rect != target.UnclippedRect;
                target.Visible = target.Rect.Width > 0 && target.Rect.Height > 0; target.State = target.Visible ? "visible" : "hidden";
            }
            Add(target);
        }
        foreach (var target in targets) target.BoundsGeneration = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            target.Id + "|" + target.Hwnd + "|" + target.Rect + "|" + target.State + "|" + target.Clipped)));
        return targets.ToArray();
    }
    internal sealed record DockSource(LayoutContent Dock, object Visual);
    private void Add(CaptureTarget target)
    {
        if (targets.Count >= maxNodes) { Truncated = true; return; }
        targets.Add(target);
        if (target.Source != null) objects.TryAdd(target.Source, target);
        if (target.Hwnd != "0x0") handles.TryAdd(CaptureNativeBounds.ParseHandle(target.Hwnd), target.Id);
    }
    private void Visit(DependencyObject node, string parent, int depth)
    {
        if (depth > maxDepth || targets.Count >= maxNodes) { Truncated = true; return; }
        if (!visited.Add(node)) return;
        var target = new CaptureTarget { Id = Id(node), Source = node, ParentId = parent, Type = node.GetType().FullName, FrameworkName = (node as FrameworkElement)?.Name };
        target.Kind = node switch { ModalWindow => "dialog", Window => "window", ContextMenu => "context-menu", Popup => "popup", ToolBar or ToolBarTray or Menu => "toolbar", Expander or GroupBox => "section", UserControl => "panel", _ => "element" };
        target.Name = (node as Window)?.Title ?? (node as FrameworkElement)?.Name ?? "";
        if (node is UIElement element)
        {
            target.AutomationId = AutomationProperties.GetAutomationId(element);
            try
            {
                var peer = UIElementAutomationPeer.CreatePeerForElement(element);
                if (peer != null) { target.Name = Nonempty(peer.GetName(), target.Name); target.AutomationId = Nonempty(peer.GetAutomationId(), target.AutomationId); }
            }
            catch (Exception error) { target.Reason = "Automation peer metadata unavailable: " + error.GetType().Name; }
        }
        if (node is FrameworkElement framework)
        {
            try { Measure(framework, target); }
            catch (Exception error) { target.State = "unavailable"; target.Reason = "Bounds unavailable: " + error.GetType().Name; }
        }
        else { target.State = "unavailable"; target.Reason = "This logical node has no realized visual bounds."; }
        if (node is Window owned && owned.Owner != null && objects.TryGetValue(owned.Owner, out var ownerWindow)) target.ParentId = ownerWindow.Id;
        Add(target);
        if (node is ContextMenu context && context.PlacementTarget != null && objects.TryGetValue(context.PlacementTarget, out var placement)) target.ParentId = placement.Id;
        if (node is Visual or System.Windows.Media.Media3D.Visual3D)
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(node); index++) Visit(VisualTreeHelper.GetChild(node, index), target.Id, depth + 1);
        foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>()) Visit(child, target.Id, depth + 1);
        if (node is FrameworkElement owner && owner.ContextMenu != null) Visit(owner.ContextMenu, target.Id, depth + 1);
        if (node is Popup popup && popup.Child != null) Visit(popup.Child, target.Id, depth + 1);
    }
    private static string Nonempty(string first, string fallback) => string.IsNullOrEmpty(first) ? fallback : first;
    private static void Measure(FrameworkElement element, CaptureTarget target)
    {
        if (PresentationSource.FromVisual(element) is not HwndSource source || source.RootVisual is not Visual root)
        { target.State = "hidden"; target.Reason = "No active presentation source."; return; }
        var native = element is GameEngineHost engine ? engine.Handle : source.Handle;
        target.Hwnd = CaptureNativeBounds.Handle(native);
        target.NativeChildHwnd = element is GameEngineHost ? target.Hwnd : null;
        target.Dpi = new(CaptureNativeBounds.Dpi(source.Handle), CaptureNativeBounds.Dpi(source.Handle));
        target.Foreground = CaptureNativeBounds.IsForeground(native);
        var client = CaptureNativeBounds.WindowRect(source.Handle, true);
        if (element is Window)
        {
            var bounds = CaptureNativeBounds.WindowRect(native, false);
            target.Rect = CaptureNativeBounds.Rectangle(bounds); target.BoundsSource = "native-window";
            target.Visible = element.IsVisible && CaptureNativeBounds.IsVisible(native) && target.Rect.Width > 0 && target.Rect.Height > 0;
            target.State = target.Visible ? "visible" : "hidden"; return;
        }
        var transform = source.CompositionTarget.TransformToDevice;
        Rect ToScreen(Visual visual, Rect local)
        {
            var relative = ReferenceEquals(visual, root) ? local : visual.TransformToAncestor(root).TransformBounds(local);
            var device = MatrixRect(relative, transform); device.Offset(client.Left, client.Top); return device;
        }
        var unclipped = element is GameEngineHost ? CaptureNativeBounds.WindowRect(native, true) : ToScreen(element, new Rect(element.RenderSize));
        var clipped = Rect.Intersect(unclipped, client);
        for (DependencyObject ancestor = element; ancestor is Visual visual; ancestor = VisualTreeHelper.GetParent(ancestor))
        {
            if (ancestor is UIElement ui)
            {
                if (ui.ClipToBounds) clipped.Intersect(ToScreen(visual, new Rect(ui.RenderSize)));
                if (ui.Clip != null) clipped.Intersect(ToScreen(visual, ui.Clip.Bounds));
            }
            var layoutClip = VisualTreeHelper.GetClip(visual); if (layoutClip != null) clipped.Intersect(ToScreen(visual, layoutClip.Bounds));
            if (ReferenceEquals(ancestor, root)) break;
        }
        if (element is GameEngineHost) clipped.Intersect(CaptureNativeBounds.WindowRect(native, true));
        target.UnclippedRect = CaptureNativeBounds.Rectangle(unclipped); target.Rect = CaptureNativeBounds.Rectangle(clipped);
        target.Clipped = target.Rect != target.UnclippedRect;
        target.BoundsSource = element is GameEngineHost ? "engine-native-client/wpf-ancestor-clip" : "wpf-visual/device-transform/ancestor-clip";
        target.Visible = element.IsVisible && CaptureNativeBounds.IsVisible(native) && target.Rect.Width > 0 && target.Rect.Height > 0;
        target.State = target.Visible ? "visible" : "hidden";
    }
    private static Rect MatrixRect(Rect rect, Matrix matrix) { rect.Transform(matrix); return rect; }
}
