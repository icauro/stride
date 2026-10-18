// Copyright (c) .NET Foundation and Contributors.
// Distributed under the MIT license. See LICENSE.md in the project root.
#if STRIDE_GRAPHICS_API_WEBGPU && STRIDE_PLATFORM_BROWSER
using System;
using System.Runtime.InteropServices;
using System.Text;
using Stride.Games;
using Stride.Graphics.WebGPU;
using Api = Stride.Graphics.WebGPU.WebGPU;

namespace Stride.Graphics;

public sealed unsafe partial class SwapChainGraphicsPresenter
{
    private partial void ValidateSurfaceWindow()
    {
        if (Description.DeviceWindowHandle?.Context != AppContextType.Browser ||
            Description.DeviceWindowHandle.NativeWindow is not string selector || string.IsNullOrWhiteSpace(selector) || selector.Contains('\0'))
            throw new ArgumentException("Browser presentation requires the CSS selector of an existing canvas.");
    }

    private partial WGPUSurface CreateWindowSurface()
    {
        // The pinned Emdawn selector lookup reads a C string despite StringView.
        var selector = Encoding.UTF8.GetBytes((string)Description.DeviceWindowHandle.NativeWindow + "\0");
        fixed (byte* text = selector)
        {
            // Emdawn v20260423.175430's browser-only header extension.
            var canvas = new CanvasSource
            {
                chain = new WGPUChainedStruct { sType = (WGPUSType)262144 },
                selector = new WGPUStringView { data = text, length = (nuint)(selector.Length - 1) },
            };
            var descriptor = WGPUSurfaceDescriptor.CreateDefault(); descriptor.nextInChain = &canvas.chain;
            return Api.wgpuInstanceCreateSurface(GraphicsDevice.WebGpuContext.Instance, &descriptor);
        }
    }

    private partial bool WindowCanPresent() => true;

    private partial void FinishWindowFrame()
    {
        // Canvas textures are presented by the browser when the host frame yields.
        // Emdawn explicitly forbids wgpuSurfacePresent; the host uses animation frames.
    }

    private partial void PrepareSurfaceChange()
    {
        // The host changes size between submitted frames. WebGPU retains resources
        // used by submitted work through completion, so retiring old textures here
        // does not require blocking the browser or claiming that the GPU is idle.
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CanvasSource { public WGPUChainedStruct chain; public WGPUStringView selector; }
}
#endif
