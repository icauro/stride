using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Stride.Graphics.WebGPU;
using Api = Stride.Graphics.WebGPU.WebGPU;

// This fixture deliberately owns one frame/device at a time. It exercises the
// generated imports directly; it is not Stride's production device context.
internal sealed class ManagedFrame : IDisposable
{
    private const int Width = 65, Height = 63, Stride = 512;
    public static int LiveFrames { get; private set; }
    public static int PendingCallbacks { get; private set; }
    public bool InvalidMapRejected { get; private set; }
    private bool mapped, disposed;
    private WGPUInstance instance;
    private WGPUSurface surface;
    private WGPUAdapter adapter;
    private WGPUDevice device;
    private WGPUQueue queue;
    private WGPUTexture texture, canvasTexture;
    private WGPUTextureView view;
    private WGPUBuffer buffer;
    private WGPUShaderModule shader;
    private WGPURenderPipeline pipeline;

    public static async Task<ManagedFrame> CreateAsync(bool noAdapter)
    {
        var frame = new ManagedFrame();
        LiveFrames++;
        try
        {
            if (noAdapter) throw new InvalidOperationException("Injected adapter unavailable");
            frame.CreateSurface();
            frame.adapter = await frame.RequestAdapter();
            frame.device = await frame.RequestDevice();
            frame.CreateResources();
            frame.pipeline = await frame.RequestPipeline();
            return frame;
        }
        catch
        {
            frame.Dispose();
            throw;
        }
    }

    private unsafe void CreateSurface()
    {
        instance = Api.wgpuCreateInstance(null);
        if (instance.Handle == 0) throw new InvalidOperationException("No WebGPU instance.");
        // Only this canvas extension is provider-specific; core types/imports are generated.
        byte[] selector = Encoding.UTF8.GetBytes("#canvas\0");
        fixed (byte* data = selector)
        {
            var canvas = new CanvasSource {
                chain = new WGPUChainedStruct { sType = (WGPUSType)262144 },
                selector = new WGPUStringView { data = data, length = (nuint)(selector.Length - 1) }
            };
            var descriptor = WGPUSurfaceDescriptor.CreateDefault();
            descriptor.nextInChain = &canvas.chain;
            surface = Api.wgpuInstanceCreateSurface(instance, &descriptor);
        }
        if (surface.Handle == 0) throw new InvalidOperationException("No canvas surface.");
    }

    private unsafe Task<WGPUAdapter> RequestAdapter()
    {
        var (task, user) = NewCallback<WGPUAdapter>();
        var options = WGPURequestAdapterOptions.CreateDefault();
        options.compatibleSurface = surface;
        options.powerPreference = WGPUPowerPreference.HighPerformance;
        var callback = WGPURequestAdapterCallbackInfo.CreateDefault();
        callback.mode = WGPUCallbackMode.AllowSpontaneous;
        callback.callback = &AdapterReady;
        callback.userdata1 = (void*)user;
        Api.wgpuInstanceRequestAdapter(instance, &options, callback);
        return task;
    }

    private unsafe Task<WGPUDevice> RequestDevice()
    {
        var (task, user) = NewCallback<WGPUDevice>();
        var descriptor = WGPUDeviceDescriptor.CreateDefault();
        var callback = WGPURequestDeviceCallbackInfo.CreateDefault();
        callback.mode = WGPUCallbackMode.AllowSpontaneous;
        callback.callback = &DeviceReady;
        callback.userdata1 = (void*)user;
        Api.wgpuAdapterRequestDevice(adapter, &descriptor, callback);
        return task;
    }

    private unsafe void CreateResources()
    {
        queue = Api.wgpuDeviceGetQueue(device);
        var config = WGPUSurfaceConfiguration.CreateDefault();
        config.device = device;
        config.format = WGPUTextureFormat.RGBA8Unorm;
        config.usage = WGPUTextureUsage.RenderAttachment | WGPUTextureUsage.CopyDst;
        config.width = Width; config.height = Height;
        config.alphaMode = WGPUCompositeAlphaMode.Opaque;
        Api.wgpuSurfaceConfigure(surface, &config);
        var target = WGPUTextureDescriptor.CreateDefault();
        target.dimension = WGPUTextureDimension._2D;
        target.size = new WGPUExtent3D { width = Width, height = Height, depthOrArrayLayers = 1 };
        target.format = WGPUTextureFormat.RGBA8Unorm;
        target.usage = WGPUTextureUsage.RenderAttachment | WGPUTextureUsage.CopySrc;
        texture = Api.wgpuDeviceCreateTexture(device, &target);
        var readback = WGPUBufferDescriptor.CreateDefault();
        readback.size = Stride * Height;
        readback.usage = WGPUBufferUsage.CopyDst | WGPUBufferUsage.MapRead;
        buffer = Api.wgpuDeviceCreateBuffer(device, &readback);
        byte[] code = Encoding.UTF8.GetBytes(
            "@vertex fn vs(@builtin(vertex_index) i:u32)->@builtin(position) vec4f {" +
            "let p=array<vec2f,3>(vec2f(-0.8,-0.8),vec2f(0.8,-0.8),vec2f(0.0,0.8)); return vec4f(p[i],0.0,1.0);}" +
            "@fragment fn fs()->@location(0) vec4f {return vec4f(1.0,0.0,0.0,1.0);}");
        fixed (byte* data = code)
        {
            var source = WGPUShaderSourceWGSL.CreateDefault();
            source.code = new WGPUStringView { data = data, length = (nuint)code.Length };
            var descriptor = WGPUShaderModuleDescriptor.CreateDefault();
            descriptor.nextInChain = &source.chain;
            shader = Api.wgpuDeviceCreateShaderModule(device, &descriptor);
        }
    }

    private unsafe Task<WGPURenderPipeline> RequestPipeline()
    {
        var (task, user) = NewCallback<WGPURenderPipeline>();
        byte[] vertexName = Encoding.UTF8.GetBytes("vs"), fragmentName = Encoding.UTF8.GetBytes("fs");
        fixed (byte* vs = vertexName, fs = fragmentName)
        {
            var target = WGPUColorTargetState.CreateDefault();
            target.format = WGPUTextureFormat.RGBA8Unorm;
            var fragment = WGPUFragmentState.CreateDefault();
            fragment.module = shader;
            fragment.entryPoint = new WGPUStringView { data = fs, length = 2 };
            fragment.targetCount = 1; fragment.targets = &target;
            var descriptor = WGPURenderPipelineDescriptor.CreateDefault();
            descriptor.vertex.module = shader;
            descriptor.vertex.entryPoint = new WGPUStringView { data = vs, length = 2 };
            descriptor.fragment = &fragment;
            var callback = WGPUCreateRenderPipelineAsyncCallbackInfo.CreateDefault();
            callback.mode = WGPUCallbackMode.AllowSpontaneous;
            callback.callback = &PipelineReady;
            callback.userdata1 = (void*)user;
            Api.wgpuDeviceCreateRenderPipelineAsync(device, &descriptor, callback);
        }
        return task;
    }

    public async Task<byte[]> ReadPixelsAsync(bool checkInvalidMap)
    {
        Draw();
        if (checkInvalidMap)
        {
            Api.wgpuDevicePushErrorScope(device, WGPUErrorFilter.Validation);
            try { await Map(1, 4); }
            catch (InvalidOperationException) { InvalidMapRejected = true; }
            var type = await PopErrorScope();
            if (!InvalidMapRejected || type != WGPUErrorType.Validation)
                throw new InvalidOperationException("Invalid map was not rejected.");
        }
        await Map(0, Stride * Height);
        mapped = true;
        return CopyPixels();
    }

    private unsafe void Draw()
    {
        var current = WGPUSurfaceTexture.CreateDefault();
        Api.wgpuSurfaceGetCurrentTexture(surface, &current);
        canvasTexture = current.texture;
        if (canvasTexture.Handle == 0) throw new InvalidOperationException("No canvas texture.");
        view = Api.wgpuTextureCreateView(texture, null);
        var color = WGPURenderPassColorAttachment.CreateDefault();
        color.view = view; color.loadOp = WGPULoadOp.Clear; color.storeOp = WGPUStoreOp.Store;
        color.clearValue = new WGPUColor { b = 1, a = 1 };
        var descriptor = WGPURenderPassDescriptor.CreateDefault();
        descriptor.colorAttachmentCount = 1; descriptor.colorAttachments = &color;
        var encoder = Api.wgpuDeviceCreateCommandEncoder(device, null);
        try
        {
            var pass = Api.wgpuCommandEncoderBeginRenderPass(encoder, &descriptor);
            try
            {
                Api.wgpuRenderPassEncoderSetPipeline(pass, pipeline);
                Api.wgpuRenderPassEncoderDraw(pass, 3, 1, 0, 0);
                Api.wgpuRenderPassEncoderEnd(pass);
            }
            finally { Api.wgpuRenderPassEncoderRelease(pass); }
            var source = WGPUTexelCopyTextureInfo.CreateDefault(); source.texture = texture;
            var destination = WGPUTexelCopyBufferInfo.CreateDefault(); destination.buffer = buffer;
            destination.layout.bytesPerRow = Stride; destination.layout.rowsPerImage = Height;
            var extent = new WGPUExtent3D { width = Width, height = Height, depthOrArrayLayers = 1 };
            Api.wgpuCommandEncoderCopyTextureToBuffer(encoder, &source, &destination, &extent);
            var canvas = WGPUTexelCopyTextureInfo.CreateDefault(); canvas.texture = canvasTexture;
            Api.wgpuCommandEncoderCopyTextureToTexture(encoder, &source, &canvas, &extent);
            var commands = Api.wgpuCommandEncoderFinish(encoder, null);
            try { Api.wgpuQueueSubmit(queue, 1, &commands); }
            finally { Api.wgpuCommandBufferRelease(commands); }
        }
        finally { Api.wgpuCommandEncoderRelease(encoder); }
    }

    private unsafe Task<bool> Map(nuint offset, nuint size)
    {
        var (task, user) = NewCallback<bool>();
        var callback = WGPUBufferMapCallbackInfo.CreateDefault();
        callback.mode = WGPUCallbackMode.AllowSpontaneous; callback.callback = &Mapped; callback.userdata1 = (void*)user;
        Api.wgpuBufferMapAsync(buffer, WGPUMapMode.Read, offset, size, callback);
        return task;
    }

    private unsafe Task<WGPUErrorType> PopErrorScope()
    {
        var (task, user) = NewCallback<WGPUErrorType>();
        var callback = WGPUPopErrorScopeCallbackInfo.CreateDefault();
        callback.mode = WGPUCallbackMode.AllowSpontaneous; callback.callback = &ErrorScopeReady; callback.userdata1 = (void*)user;
        Api.wgpuDevicePopErrorScope(device, callback);
        return task;
    }

    private unsafe byte[] CopyPixels()
    {
        var data = (nint)Api.wgpuBufferGetConstMappedRange(buffer, 0, Stride * Height);
        if (data == 0) throw new InvalidOperationException("No mapped bytes.");
        var pixels = new byte[Width * Height * 4];
        for (int row = 0; row < Height; row++)
            Marshal.Copy(data + row * Stride, pixels, row * Width * 4, Width * 4);
        return pixels;
    }

    // Function pointers cross into C only at these leaf methods. No exception may
    // unwind through them; error text is copied before the native message expires.
    private static (Task<T> Task, nint User) NewCallback<T>()
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var user = GCHandle.ToIntPtr(GCHandle.Alloc(completion));
        PendingCallbacks++;
        return (completion.Task, user);
    }

    private static unsafe void Complete<T>(void* user, T value, bool success, WGPUStringView message)
    {
        var handle = GCHandle.FromIntPtr((nint)user);
        var completion = (TaskCompletionSource<T>)handle.Target!;
        handle.Free(); PendingCallbacks--;
        try
        {
            if (success) completion.TrySetResult(value);
            else completion.TrySetException(new InvalidOperationException(message.data == null ? "WebGPU callback failed." :
                message.length == nuint.MaxValue ? Marshal.PtrToStringUTF8((nint)message.data) : Marshal.PtrToStringUTF8((nint)message.data, checked((int)message.length))));
        }
        catch (Exception error) { completion.TrySetException(error); }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe void AdapterReady(WGPURequestAdapterStatus status, WGPUAdapter value, WGPUStringView message, void* user, void* unused) => Complete(user, value, status == WGPURequestAdapterStatus.Success, message);
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe void DeviceReady(WGPURequestDeviceStatus status, WGPUDevice value, WGPUStringView message, void* user, void* unused) => Complete(user, value, status == WGPURequestDeviceStatus.Success, message);
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe void PipelineReady(WGPUCreatePipelineAsyncStatus status, WGPURenderPipeline value, WGPUStringView message, void* user, void* unused) => Complete(user, value, status == WGPUCreatePipelineAsyncStatus.Success, message);
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe void Mapped(WGPUMapAsyncStatus status, WGPUStringView message, void* user, void* unused) => Complete(user, true, status == WGPUMapAsyncStatus.Success, message);
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe void ErrorScopeReady(WGPUPopErrorScopeStatus status, WGPUErrorType type, WGPUStringView message, void* user, void* unused) => Complete(user, type, status == WGPUPopErrorScopeStatus.Success, message);

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (buffer.Handle != 0) { if (mapped) Api.wgpuBufferUnmap(buffer); Api.wgpuBufferDestroy(buffer); Api.wgpuBufferRelease(buffer); }
        if (view.Handle != 0) Api.wgpuTextureViewRelease(view);
        if (canvasTexture.Handle != 0) Api.wgpuTextureRelease(canvasTexture);
        if (texture.Handle != 0) { Api.wgpuTextureDestroy(texture); Api.wgpuTextureRelease(texture); }
        if (pipeline.Handle != 0) Api.wgpuRenderPipelineRelease(pipeline);
        if (shader.Handle != 0) Api.wgpuShaderModuleRelease(shader);
        if (queue.Handle != 0) Api.wgpuQueueRelease(queue);
        if (surface.Handle != 0) { if (device.Handle != 0) Api.wgpuSurfaceUnconfigure(surface); Api.wgpuSurfaceRelease(surface); }
        if (device.Handle != 0) { Api.wgpuDeviceDestroy(device); Api.wgpuDeviceRelease(device); }
        if (adapter.Handle != 0) Api.wgpuAdapterRelease(adapter);
        if (instance.Handle != 0) Api.wgpuInstanceRelease(instance);
        LiveFrames--;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CanvasSource { public WGPUChainedStruct chain; public WGPUStringView selector; }
}
