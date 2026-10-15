using Stride.Engine;
using Stride.Games;
using Stride.Graphics;
using Stride.Core.Mathematics;
using Stride.Core.Diagnostics;
using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;
using NVector2 = System.Numerics.Vector2;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private NVector2 initialLayoutFramebufferSize;
    private int initialLayoutStableFrames;
    private long nextInitialLayoutTrace;

    private bool CanCreateInitialLayout()
    {
        var buffer = GraphicsDevice.Presenter.BackBuffer;
        var size = new NVector2(buffer.Width, buffer.Height);
        var native = WindowsWindowPlacement.ClientSize(Window.NativeWindow.Handle);
        if (Environment.GetEnvironmentVariable("STRIDE_EDITOR_TRACE_LAYOUT") == "1" && Environment.TickCount64 >= nextInitialLayoutTrace)
        {
            nextInitialLayoutTrace = Environment.TickCount64 + 1000;
            var viewport = Gui.GetMainViewport();
            var client = Window.ClientBounds;
            Console.Error.WriteLine($"LAYOUT READY frame={Gui.GetFrameCount()} pendingPlacement={mainWindowPlacementPending} native={native.X}x{native.Y} strideClient={client.Width}x{client.Height} backBuffer={buffer.Width}x{buffer.Height} preferred={GraphicsDeviceManager.PreferredBackBufferWidth}x{GraphicsDeviceManager.PreferredBackBufferHeight} display={Gui.GetIO().DisplaySize} framebufferScale={Gui.GetIO().DisplayFramebufferScale} mainViewport={viewport.Size} work={viewport.WorkSize} dpi={WindowsDpi.GetScale(Window.NativeWindow.Handle)} stableFrames={initialLayoutStableFrames}");
            Console.Error.Flush();
        }
        if (mainWindowPlacementPending || size.X <= 0 || size.Y <= 0
            || native.X > 0 && native.Y > 0 && (native.X != buffer.Width || native.Y != buffer.Height)
            || Gui.GetIO().DisplaySize != size)
        { initialLayoutStableFrames = 0; return false; }
        if (size != initialLayoutFramebufferSize)
        { initialLayoutFramebufferSize = size; initialLayoutStableFrames = 1; return false; }
        return ++initialLayoutStableFrames >= 2;
    }

    private static void SetInitialWindow(NVector2 position, NVector2 size)
    {
        Gui.SetNextWindowPos(UiScale(position), ImGuiCond.FirstUseEver);
        Gui.SetNextWindowSize(UiScale(size), ImGuiCond.FirstUseEver);
    }

    private unsafe bool CreateInitialLayout(uint dockspace)
    {
        if (!TryResetDocumentGroupLayout()) return false;
        ImGuiP.DockBuilderRemoveNode(dockspace);
        ImGuiP.DockBuilderAddNode(dockspace, (ImGuiDockNodeFlags)ImGuiDockNodeFlagsPrivate.Space);
        var viewport = Gui.GetMainViewport();
        ImGuiP.DockBuilderSetNodeSize(dockspace, viewport.WorkSize);
        uint center = dockspace, left, right, preview, bottom;
        // Keep WPF's side pane widths; allow the bottom tools one complete
        // reference-thumbnail row, including its name and asset-kind caption.
        var leftWidth = Math.Min(UiScale(300), viewport.WorkSize.X * 0.25f);
        var rightWidth = Math.Min(UiScale(400), (viewport.WorkSize.X - leftWidth) * 0.45f);
        var bottomHeight = Math.Min(UiScale(240), viewport.WorkSize.Y * 0.35f);
        ImGuiP.DockBuilderSplitNode(center, ImGuiDir.Left, leftWidth / Math.Max(1, viewport.WorkSize.X), &left, &center);
        ImGuiP.DockBuilderSplitNode(center, ImGuiDir.Right, rightWidth / Math.Max(1, viewport.WorkSize.X - leftWidth), &right, &center);
        // WPF's property/history pane uses 2* height above the 1* asset preview.
        ImGuiP.DockBuilderSplitNode(right, ImGuiDir.Down, 1f / 3f, &preview, &right);
        ImGuiP.DockBuilderSplitNode(center, ImGuiDir.Down, bottomHeight / Math.Max(1, viewport.WorkSize.Y), &bottom, &center);
        ImGuiP.DockBuilderDockWindow(EditorLocalization.Widget("Hierarchy", "View"), left);
        ImGuiP.DockBuilderDockWindow(EditorLocalization.Widget("Solution explorer", "View"), left);
        ImGuiP.DockBuilderDockWindow(EditorLocalization.Widget("Property grid###Inspector", "View"), right);
        ImGuiP.DockBuilderDockWindow(EditorLocalization.Widget("Edit history", "View"), right);
        ImGuiP.DockBuilderDockWindow(EditorLocalization.Widget("References", "View"), bottom);
        ImGuiP.DockBuilderDockWindow("###Asset errors", bottom);
        ImGuiP.DockBuilderDockWindow(EditorLocalization.Widget("Asset view", "View"), bottom);
        ImGuiP.DockBuilderDockWindow(EditorLocalization.Widget("Output", "View"), bottom);
        ImGuiP.DockBuilderDockWindow("Scene", center);
        ImGuiP.DockBuilderDockWindow(PreviewWindowCaption.Text, preview);
        ImGuiP.DockBuilderFinish(dockspace);
        SetInitialDocumentGroupArea(center);
        return true;
    }
}
