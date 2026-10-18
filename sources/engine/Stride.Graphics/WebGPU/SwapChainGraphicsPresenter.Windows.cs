// Copyright (c) .NET Foundation and Contributors.
// Distributed under the MIT license. See LICENSE.md in the project root.
#if STRIDE_GRAPHICS_API_WEBGPU && !STRIDE_PLATFORM_BROWSER
using System;
using System.Runtime.InteropServices;
using Stride.Graphics.WebGPU;
using Api = Stride.Graphics.WebGPU.WebGPU;

namespace Stride.Graphics;

public sealed unsafe partial class SwapChainGraphicsPresenter
{
    private nint window;

    private partial void ValidateSurfaceWindow()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Native WebGPU window presentation currently supports Windows HWND surfaces.");
        if (Description.DeviceWindowHandle == null || Description.DeviceWindowHandle.Handle == 0) throw new ArgumentException("A native window handle is required.");
    }

    private partial WGPUSurface CreateWindowSurface()
    {
        window = Description.DeviceWindowHandle.Handle;
        var source = WGPUSurfaceSourceWindowsHWND.CreateDefault();
        source.hwnd = (void*)window;
        source.hinstance = (void*)GetModuleHandleW(null);
        var descriptor = WGPUSurfaceDescriptor.CreateDefault(); descriptor.nextInChain = &source.chain;
        return Api.wgpuInstanceCreateSurface(GraphicsDevice.WebGpuContext.Instance, &descriptor);
    }

    private partial bool WindowCanPresent() => !IsIconic(window) && GetClientRect(window, out var rect) && rect.Right > rect.Left && rect.Bottom > rect.Top;
    private partial void FinishWindowFrame()
    {
        if (Api.wgpuSurfacePresent(surface) != WGPUStatus.Success) throw new InvalidOperationException("WebGPU surface presentation failed.");
    }
    private partial void PrepareSurfaceChange() => GraphicsDevice.WaitForGpuIdle();

    [StructLayout(LayoutKind.Sequential)] private struct ClientRect { public int Left, Top, Right, Bottom; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandleW(string module);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetClientRect(nint window, out ClientRect rect);
}
#endif
