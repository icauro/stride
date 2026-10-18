// Copyright (c) .NET Foundation and Contributors.
// Distributed under the MIT license. See LICENSE.md in the project root.
#if STRIDE_GRAPHICS_API_WEBGPU
using System;
using Stride.Graphics.WebGPU;
using Api = Stride.Graphics.WebGPU.WebGPU;
namespace Stride.Graphics;

public sealed unsafe partial class SwapChainGraphicsPresenter : GraphicsPresenter
{
    private WGPUSurface surface;
    private WGPUSurfaceConfiguration configuration;
    private Texture backBuffer;
    private bool configured;
    private WGPURenderPipeline blitPipeline;
    private WGPUBindGroupLayout blitLayout;
    private WGPUTextureFormat blitFormat;
    private PresentInterval configuredInterval;

    public SwapChainGraphicsPresenter(GraphicsDevice device, PresentationParameters parameters) : base(device, parameters)
    {
        try { OnRecreated(); }
        catch { OnDestroyed(); DepthStencilBuffer?.Dispose(); throw; }
    }

    public override Texture BackBuffer => backBuffer;
    public override object NativePresenter => surface.Handle;
    public override bool IsFullScreen
    {
        get => false;
        set { if (value) throw new NotSupportedException("WebGPU exclusive full screen is not supported. Use a borderless window."); }
    }

    protected override void ProcessPresentationParameters()
    {
        ValidateSurfaceWindow();
        if (Description.IsFullScreen || Description.MultisampleCount != MultisampleCount.None) throw new NotSupportedException("WebGPU presentation requires a windowed, single-sample back buffer.");
        if (Description.BackBufferWidth <= 0 || Description.BackBufferHeight <= 0) throw new ArgumentOutOfRangeException(nameof(Description), "Initial back-buffer dimensions must be positive.");
    }

    public override void OnRecreated()
    {
        if (surface.Handle != 0) throw new InvalidOperationException("The WebGPU surface is already initialized.");
        surface = CreateWindowSurface();
        if (surface.Handle == 0) throw new InvalidOperationException("WebGPU could not create the window surface.");
        Configure();
        ReplaceBackBuffer(Description.BackBufferWidth, Description.BackBufferHeight, Description.BackBufferFormat);
    }

    private void Configure()
    {
        if (Description.BackBufferWidth <= 0 || Description.BackBufferHeight <= 0) return;
        var capabilities = WGPUSurfaceCapabilities.CreateDefault();
        try
        {
            var context = GraphicsDevice.WebGpuContext;
            if (Api.wgpuSurfaceGetCapabilities(surface, context.Adapter, &capabilities) != WGPUStatus.Success)
                throw new NotSupportedException("The selected WebGPU adapter cannot present to this window.");
            var formats = new ReadOnlySpan<WGPUTextureFormat>(capabilities.formats, checked((int)capabilities.formatCount));
            var selectedFormat = Description.BackBufferFormat;
            var surfaceFormat = WebGpuConvert.TextureFormat(selectedFormat);
            if (!formats.Contains(WebGpuConvert.TextureFormat(selectedFormat)))
            {
                selectedFormat = Description.BackBufferFormat.IsSRgb ? PixelFormat.B8G8R8A8_UNorm_SRgb : PixelFormat.B8G8R8A8_UNorm;
                if (!formats.Contains(WebGpuConvert.TextureFormat(selectedFormat)))
                {
                    // Canvas surfaces expose UNorm formats, with an explicit
                    // sRGB view for correct decode/encode during the final blit.
                    if (!Description.BackBufferFormat.IsSRgb) throw new NotSupportedException("The WebGPU surface does not support the requested color format.");
                    if (formats.Contains(WGPUTextureFormat.BGRA8Unorm)) selectedFormat = PixelFormat.B8G8R8A8_UNorm_SRgb;
                    else if (formats.Contains(WGPUTextureFormat.RGBA8Unorm)) selectedFormat = PixelFormat.R8G8B8A8_UNorm_SRgb;
                    else throw new NotSupportedException("The WebGPU surface does not support an sRGB-compatible color view.");
                }
                surfaceFormat = WebGpuConvert.TextureFormat(selectedFormat);
                if (!formats.Contains(surfaceFormat))
                    surfaceFormat = selectedFormat == PixelFormat.B8G8R8A8_UNorm_SRgb ? WGPUTextureFormat.BGRA8Unorm : WGPUTextureFormat.RGBA8Unorm;
            }
            var modes = new ReadOnlySpan<WGPUPresentMode>(capabilities.presentModes, checked((int)capabilities.presentModeCount));
            var mode = Description.PresentationInterval == PresentInterval.Immediate && modes.Contains(WGPUPresentMode.Immediate) ? WGPUPresentMode.Immediate : WGPUPresentMode.Fifo;
            if (Description.PresentationInterval > PresentInterval.One) throw new NotSupportedException("WebGPU presentation intervals above one are not supported.");
            configuration = WGPUSurfaceConfiguration.CreateDefault();
            configuration.device = context.Device;
            configuration.format = surfaceFormat;
            configuration.usage = WGPUTextureUsage.RenderAttachment;
            configuration.width = (uint)Description.BackBufferWidth;
            configuration.height = (uint)Description.BackBufferHeight;
            configuration.presentMode = mode;
            if (capabilities.alphaModeCount > 0) configuration.alphaMode = capabilities.alphaModes[0];
            var config = configuration;
            var viewFormat = WebGpuConvert.TextureFormat(selectedFormat);
            // The descriptor pointer is valid only for this call, never cached.
            if (viewFormat != surfaceFormat) { config.viewFormatCount = 1; config.viewFormats = &viewFormat; }
            Api.wgpuSurfaceConfigure(surface, &config);
            context.ThrowIfFailed();
            configured = true;
            configuredInterval = Description.PresentationInterval;
            Description.BackBufferFormat = selectedFormat;
            CreateBlitPipeline(viewFormat);
        }
        finally { Api.wgpuSurfaceCapabilitiesFreeMembers(capabilities); }
    }

    internal override bool CanPresentFrame()
    {
        return surface.Handle != 0 && Description.BackBufferWidth > 0 && Description.BackBufferHeight > 0 && WindowCanPresent();
    }

    public override void Present()
    {
        if (!CanPresentFrame()) return;
        var context = GraphicsDevice.WebGpuContext;
        context.ThrowIfFailed();
        // Changing the interval takes effect on the next frame.
        if (!configured || Description.PresentationInterval != configuredInterval)
            Configure();
        var acquired = default(WGPUSurfaceTexture);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            Api.wgpuSurfaceGetCurrentTexture(surface, &acquired);
            if (acquired.status is WGPUSurfaceGetCurrentTextureStatus.SuccessOptimal or WGPUSurfaceGetCurrentTextureStatus.SuccessSuboptimal) break;
            if (acquired.texture.Handle != 0) Api.wgpuTextureRelease(acquired.texture);
            acquired.texture = default;
            if (acquired.status == WGPUSurfaceGetCurrentTextureStatus.Timeout) return;
            if (attempt != 0 || acquired.status is not (WGPUSurfaceGetCurrentTextureStatus.Outdated or WGPUSurfaceGetCurrentTextureStatus.Lost))
                throw new InvalidOperationException($"WebGPU could not acquire the surface image: {acquired.status}.");
            // Reconfigure once after a resize or surface invalidation; never spin
            // indefinitely while the window is minimized or changing size.
            PrepareSurfaceChange();
            Configure();
        }
        WGPUCommandEncoder encoder = default;
        WGPUCommandBuffer buffer = default;
        WGPUTextureView surfaceView = default;
        WGPUBindGroup group = default;
        try
        {
            if (acquired.texture.Handle == 0) throw new InvalidOperationException("WebGPU acquired an empty surface image.");
            var encoderDescriptor = WGPUCommandEncoderDescriptor.CreateDefault();
            encoder = Api.wgpuDeviceCreateCommandEncoder(context.Device, &encoderDescriptor);
            var view = WGPUTextureViewDescriptor.CreateDefault(); view.format = blitFormat;
            surfaceView = Api.wgpuTextureCreateView(acquired.texture, &view);
            var binding = WGPUBindGroupEntry.CreateDefault(); binding.binding = 0; binding.textureView = backBuffer.NativeView;
            var groupDescriptor = WGPUBindGroupDescriptor.CreateDefault(); groupDescriptor.layout = blitLayout; groupDescriptor.entryCount = 1; groupDescriptor.entries = &binding;
            group = Api.wgpuDeviceCreateBindGroup(context.Device, &groupDescriptor);
            var attachment = WGPURenderPassColorAttachment.CreateDefault();
            attachment.view = surfaceView; attachment.loadOp = WGPULoadOp.Clear; attachment.storeOp = WGPUStoreOp.Store;
            var passDescriptor = WGPURenderPassDescriptor.CreateDefault(); passDescriptor.colorAttachmentCount = 1; passDescriptor.colorAttachments = &attachment;
            var pass = Api.wgpuCommandEncoderBeginRenderPass(encoder, &passDescriptor);
            Api.wgpuRenderPassEncoderSetPipeline(pass, blitPipeline);
            Api.wgpuRenderPassEncoderSetBindGroup(pass, 0, group, 0, null);
            Api.wgpuRenderPassEncoderDraw(pass, 3, 1, 0, 0);
            Api.wgpuRenderPassEncoderEnd(pass); Api.wgpuRenderPassEncoderRelease(pass);
            var bufferDescriptor = WGPUCommandBufferDescriptor.CreateDefault();
            buffer = Api.wgpuCommandEncoderFinish(encoder, &bufferDescriptor);
            context.ThrowIfFailed();
            Api.wgpuQueueSubmit(context.Queue, 1, &buffer);
            FinishWindowFrame();
            context.ThrowIfFailed();
        }
        finally
        {
            if (buffer.Handle != 0) Api.wgpuCommandBufferRelease(buffer);
            if (encoder.Handle != 0) Api.wgpuCommandEncoderRelease(encoder);
            if (group.Handle != 0) Api.wgpuBindGroupRelease(group);
            if (surfaceView.Handle != 0) Api.wgpuTextureViewRelease(surfaceView);
            if (acquired.texture.Handle != 0) Api.wgpuTextureRelease(acquired.texture);
        }
        if (acquired.status == WGPUSurfaceGetCurrentTextureStatus.SuccessSuboptimal) configured = false;
    }

    protected override void ResizeBackBuffer(int width, int height, PixelFormat format)
    {
        PrepareSurfaceChange();
        if (configured) Api.wgpuSurfaceUnconfigure(surface);
        configured = false;
        if (width <= 0 || height <= 0) return;
        Configure();
        ReplaceBackBuffer(width, height, Description.BackBufferFormat);
    }

    private void CreateBlitPipeline(WGPUTextureFormat format)
    {
        if (blitPipeline.Handle != 0 && blitFormat == format) return;
        ReleaseBlitPipeline();
        // TextureLoad preserves exact texel locations. The texture and target
        // formats apply the sRGB decode/encode when a linear back buffer is used.
        ReadOnlySpan<byte> source = """
            @group(0) @binding(0) var image: texture_2d<f32>;
            @vertex fn vs(@builtin(vertex_index) index: u32) -> @builtin(position) vec4f {
                let p = array<vec2f, 3>(vec2f(-1, -1), vec2f(3, -1), vec2f(-1, 3));
                return vec4f(p[index], 0, 1);
            }
            @fragment fn fs(@builtin(position) position: vec4f) -> @location(0) vec4f {
                return textureLoad(image, vec2i(position.xy), 0);
            }
            """u8;
        WGPUShaderModule module = default;
        try
        {
            fixed (byte* text = source)
            {
                var wgsl = WGPUShaderSourceWGSL.CreateDefault(); wgsl.code = new WGPUStringView { data = text, length = (nuint)source.Length };
                var moduleDescriptor = WGPUShaderModuleDescriptor.CreateDefault(); moduleDescriptor.nextInChain = &wgsl.chain;
                module = Api.wgpuDeviceCreateShaderModule(GraphicsDevice.WebGpuContext.Device, &moduleDescriptor);
            }
            GraphicsDevice.WebGpuContext.ThrowIfFailed();
            var descriptor = WGPURenderPipelineDescriptor.CreateDefault();
            descriptor.vertex.module = module;
            var fragment = WGPUFragmentState.CreateDefault(); fragment.module = module;
            var target = WGPUColorTargetState.CreateDefault(); target.format = format;
            fragment.targetCount = 1; fragment.targets = &target; descriptor.fragment = &fragment;
            // There is one entry point per stage, so the native API can infer them.
            blitPipeline = Api.wgpuDeviceCreateRenderPipeline(GraphicsDevice.WebGpuContext.Device, &descriptor);
            GraphicsDevice.WebGpuContext.ThrowIfFailed();
            blitLayout = Api.wgpuRenderPipelineGetBindGroupLayout(blitPipeline, 0);
            GraphicsDevice.WebGpuContext.ThrowIfFailed();
            blitFormat = format;
        }
        catch { ReleaseBlitPipeline(); throw; }
        finally { if (module.Handle != 0) Api.wgpuShaderModuleRelease(module); }
    }

    private void ReleaseBlitPipeline()
    {
        if (blitLayout.Handle != 0) Api.wgpuBindGroupLayoutRelease(blitLayout);
        if (blitPipeline.Handle != 0) Api.wgpuRenderPipelineRelease(blitPipeline);
        blitLayout = default; blitPipeline = default;
    }

    private void ReplaceBackBuffer(int width, int height, PixelFormat format)
    {
        var description = TextureDescription.New2D(width, height, format, TextureFlags.RenderTarget | TextureFlags.ShaderResource);
        if (backBuffer == null) backBuffer = Texture.New(GraphicsDevice, description);
        else
        {
            var children = DestroyChildrenTextures(backBuffer);
            backBuffer.OnDestroyed();
            backBuffer.InitializeFrom(description);
            foreach (var child in children) child.RecreateAfterParentResize();
        }
    }

    protected override void ResizeDepthStencilBuffer(int width, int height, PixelFormat format)
    {
        if (width <= 0 || height <= 0 || DepthStencilBuffer == null) return;
        var children = DestroyChildrenTextures(DepthStencilBuffer);
        DepthStencilBuffer.OnDestroyed();
        DepthStencilBuffer.InitializeFrom(TextureDescription.New2D(width, height, format, DepthStencilBuffer.Flags));
        foreach (var child in children) child.RecreateAfterParentResize();
    }

    protected internal override void OnDestroyed(bool immediately = false)
    {
        ReleaseBlitPipeline();
        backBuffer?.Dispose(); backBuffer = null;
        if (surface.Handle != 0)
        {
            if (configured) Api.wgpuSurfaceUnconfigure(surface);
            Api.wgpuSurfaceRelease(surface);
        }
        surface = default; configured = false;
        base.OnDestroyed(immediately);
    }

    private partial void ValidateSurfaceWindow();
    private partial WGPUSurface CreateWindowSurface();
    private partial bool WindowCanPresent();
    private partial void FinishWindowFrame();
    private partial void PrepareSurfaceChange();
}
#endif
