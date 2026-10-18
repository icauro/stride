// Copyright (c) .NET Foundation and Contributors.
// Distributed under the MIT license. See LICENSE.md in the project root.
using System.Runtime.InteropServices;
using Stride.Core.Mathematics;
using Stride.Games;
using Stride.Graphics;

internal static class WindowValidation
{
    internal static void Run(GraphicsDevice device)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        // A hidden real HWND exercises the surface/swapchain APIs without opening
        // an interactive test window on the user's desktop.
        var host = CreateWindowExW(0, "STATIC", "Stride WebGPU validation host", 0x00CF0000, 0, 0, 240, 180, 0, 0, GetModuleHandleW(null), 0);
        if (host == 0) throw new InvalidOperationException($"Could not create the hidden validation host: {Marshal.GetLastWin32Error()}.");
        // The hidden parent keeps the child invisible even during actual OS
        // minimize/restore. This also exercises an embedded/control HWND surface.
        var window = CreateWindowExW(0, "STATIC", "Stride WebGPU validation", 0x40CF0000, 0, 0, 160, 120, host, 0, GetModuleHandleW(null), 0);
        if (window == 0) { DestroyWindow(host); throw new InvalidOperationException($"Could not create the validation window: {Marshal.GetLastWin32Error()}."); }
        try
        {
            var handle = new WindowHandle(AppContextType.DesktopWinForms, null, window);
            var parameters = new PresentationParameters(64, 48, handle) { DepthStencilFormat = PixelFormat.D24_UNorm_S8_UInt, PresentationInterval = PresentInterval.One };
            using var presenter = new SwapChainGraphicsPresenter(device, parameters);
            using var commands = CommandList.New(device);
            var context = new GraphicsContext(device, commandList: commands);
            using var image = Texture.New2D(device, 2, 2, PixelFormat.R8G8B8A8_UNorm, new uint[] { 0xff0000ff, 0xff00ff00, 0xffff0000, uint.MaxValue });
            DrawAndPresent(3);
            var backBuffer = presenter.BackBuffer;
            using var view = backBuffer.ToTextureView(new TextureViewDescription { Flags = TextureFlags.RenderTarget, Type = ViewType.Single });
            presenter.Resize(83, 51, presenter.Description.BackBufferFormat);
            if (!ReferenceEquals(backBuffer, presenter.BackBuffer) || presenter.BackBuffer.Width != 83 || presenter.DepthStencilBuffer.Width != 83) throw new Exception("Presenter resize did not preserve the back-buffer identity and update buffer sizes.");
            commands.Clear(view, new Color4(0, 0, 1, 1));
            DrawAndPresent(2);
            presenter.PresentInterval = PresentInterval.Immediate;
            DrawAndPresent(2);
            presenter.Resize(0, 0, presenter.Description.BackBufferFormat);
            presenter.Present();
            presenter.Resize(71, 39, presenter.Description.BackBufferFormat);
            DrawAndPresent(3);
            ShowWindow(window, 6); // SW_MINIMIZE
            if (!IsIconic(window) || IsWindowVisible(window)) throw new Exception("The child HWND was not minimized while its host stayed hidden.");
            presenter.Present();
            ShowWindow(window, 9); // SW_RESTORE
            if (IsIconic(window) || IsWindowVisible(window)) throw new Exception("The child HWND did not restore under its hidden host.");
            DrawAndPresent(2);
            device.WaitForGpuIdle();
            Console.WriteLine("PASS: Native HWND presentation, repeated frames, resize/view recreation, interval changes, zero-size and actual OS minimize/restore under a hidden host.");

            void DrawAndPresent(int count)
            {
                for (var i = 0; i < count; i++)
                {
                    presenter.BeginDraw(commands);
                    commands.SetRenderTargetAndViewport(presenter.DepthStencilBuffer, presenter.BackBuffer);
                    commands.Clear(presenter.DepthStencilBuffer, DepthStencilClearOptions.DepthBuffer | DepthStencilClearOptions.Stencil);
                    context.DrawTexture(image, device.SamplerStates.PointClamp);
                    presenter.EndDraw(commands, true);
                    commands.Flush();
                    presenter.Present();
                    commands.Reset();
                }
            }
        }
        finally { DestroyWindow(window); DestroyWindow(host); }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateWindowExW(uint extendedStyle, string className, string name, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint window);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandleW(string? module);
}
