using Hexa.NET.ImGui;
using Stride.Graphics;
using Stride.Games;

namespace Stride.Editor.ImGui;

/// <summary>A secondary swap chain on the editor's device, presented after engine frame submission.</summary>
internal sealed class EditorViewportPresenter : IDisposable
{
    private readonly SwapChainGraphicsPresenter presenter;
    private bool pending;
    // Borrow only between this frame's rendering and PresentAfterFrame. Readback
    // completes synchronously on the actor; the caller never owns this texture.
    internal Texture? PendingBackBuffer => pending ? presenter.BackBuffer : null;
    public EditorViewportPresenter(GraphicsDevice device, nint window, int width, int height, PixelFormat mainBackBufferFormat)
    {
        if (mainBackBufferFormat == PixelFormat.None)
            throw new ArgumentException("A secondary viewport requires the actual main back-buffer format.", nameof(mainBackBufferFormat));
        var parameters = new PresentationParameters(Math.Max(1, width), Math.Max(1, height),
            new WindowHandle(AppContextType.DesktopWinForms, window, window), mainBackBufferFormat)
        {
            DepthStencilFormat = PixelFormat.None,
            ColorSpace = device.ColorSpace,
            PresentationInterval = PresentInterval.Immediate,
            IsFullScreen = false,
        };
        presenter = new SwapChainGraphicsPresenter(device, parameters);
    }
    public void Resize(int width, int height)
    {
        if (width <= 0 || height <= 0 || pending) return;
        if (presenter.BackBuffer.Width != width || presenter.BackBuffer.Height != height)
            presenter.Resize(width, height, presenter.Description.BackBufferFormat);
    }
    public bool Render(GraphicsContext context, ImGuiRenderer renderer, ImDrawDataPtr data, bool clear)
    {
        if (data.IsNull || data.DisplaySize.X <= 0 || data.DisplaySize.Y <= 0) return false;
        presenter.BeginDraw(context.CommandList);
        context.CommandList.SetRenderTargetAndViewport(null, presenter.BackBuffer);
        if (clear) context.CommandList.Clear(presenter.BackBuffer, new Stride.Core.Mathematics.Color4(0.08f, 0.09f, 0.11f, 1));
        renderer.Render(context, data);
        presenter.EndDraw(context.CommandList, true);
        pending = true;
        return true;
    }
    public void PresentAfterFrame(bool present)
    {
        if (!pending) return;
        pending = false;
        // The engine must have flushed its command list and called device.End.
        if (present) presenter.Present();
    }
    public void Dispose() => presenter.Dispose();
}
