// Copyright (c) .NET Foundation and Contributors.
// Distributed under the MIT license. See LICENSE.md in the project root.
#if STRIDE_GRAPHICS_API_WEBGPU
#nullable enable
using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Stride.Graphics.WebGPU;
using Api = Stride.Graphics.WebGPU.WebGPU;

namespace Stride.Graphics;

internal sealed partial class WebGpuDeviceContext
{
    private static readonly ConcurrentDictionary<nint, WebGpuAsyncRequest<WGPUAdapter>> AsyncAdapters = new();
    private static readonly ConcurrentDictionary<nint, WebGpuAsyncRequest<WGPUDevice>> AsyncDevices = new();
    private static readonly ConcurrentDictionary<nint, WebGpuAsyncRequest<bool>> AsyncQueues = new();
    private static readonly ConcurrentDictionary<nint, WebGpuAsyncRequest<bool>> AsyncMaps = new();
    private readonly ConcurrentDictionary<WGPUBuffer, byte> asyncReadBuffers = new();

    /// <summary>Creates a device using core WebGPU features only. The native provider, if any, must already be loaded.</summary>
    public static async Task<WebGpuDeviceContext> CreatePortableAsync(CancellationToken cancellationToken = default,
        WGPUBackendType backend = WGPUBackendType.Undefined, Func<string, Exception>? deviceLostException = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var context = new WebGpuDeviceContext(deviceLostException);
        try
        {
            context.CreatePortableInstance();
            var adapterToken = NewToken();
            var adapter = new WebGpuAsyncRequest<WGPUAdapter>(ReleaseAdapter);
            AsyncAdapters[adapterToken] = adapter;
            try
            {
                context.RequestPortableAdapter(adapterToken, backend);
                context.Adapter = await adapter.WaitAsync(context.PollForAsync, cancellationToken);
            }
            finally { AsyncAdapters.TryRemove(adapterToken, out _); }
            context.ReadPortableAdapterInfo();
            var deviceToken = NewToken();
            var device = new WebGpuAsyncRequest<WGPUDevice>(ReleaseDevice);
            AsyncDevices[deviceToken] = device;
            try
            {
                context.RequestPortableDevice(deviceToken);
                context.Device = await device.WaitAsync(context.PollForAsync, cancellationToken);
            }
            finally { AsyncDevices.TryRemove(deviceToken, out _); }
            cancellationToken.ThrowIfCancellationRequested();
            context.FinishPortableDevice();
            return context;
        }
        catch { context.Dispose(); throw; }
    }

    public async Task WaitForIdleAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ThrowIfFailed();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        cancellation.Token.ThrowIfCancellationRequested();
        var requestToken = NewToken();
        var request = new WebGpuAsyncRequest<bool>(_ => { });
        AsyncQueues[requestToken] = request;
        try
        {
            RequestQueueCompletion(requestToken);
            await request.WaitAsync(PollForAsync, cancellation.Token);
            ObjectDisposedException.ThrowIf(disposed, this);
            ThrowIfFailed();
        }
        finally { AsyncQueues.TryRemove(requestToken, out _); }
    }

    /// <summary>Copies a complete mapped range and unmaps it before returning. The caller retains its buffer reference.</summary>
    public async Task<byte[]> ReadBufferAsync(WGPUBuffer buffer, int length, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (length <= 0 || length % 4 != 0) throw new ArgumentOutOfRangeException(nameof(length));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        cancellation.Token.ThrowIfCancellationRequested();
        ThrowIfFailed();
        CancelPendingRead(buffer);
        if (!asyncReadBuffers.TryAdd(buffer, 0)) throw new InvalidOperationException("This buffer already has an asynchronous readback.");
        var requestToken = NewToken();
        var request = new WebGpuAsyncRequest<bool>(_ => { });
        AsyncMaps[requestToken] = request;
        AddBufferReference(buffer);
        try
        {
            RequestReadMap(buffer, length, requestToken);
            await request.WaitAsync(PollForAsync, cancellation.Token);
            ObjectDisposedException.ThrowIf(disposed, this);
            ThrowIfFailed();
            return CopyMappedBytes(buffer, length);
        }
        finally
        {
            AsyncMaps.TryRemove(requestToken, out _);
            UnmapAndRelease(buffer);
            asyncReadBuffers.TryRemove(buffer, out _);
        }
    }

    // Browser callbacks progress on the host event loop. Keep native-only imports
    // behind the platform guard so the WASM linker can remove them.
    private void PollForAsync()
    {
        if (!OperatingSystem.IsBrowser()) Poll();
    }

    private unsafe void CreatePortableInstance()
    {
        var descriptor = WGPUInstanceDescriptor.CreateDefault();
        Instance = Api.wgpuCreateInstance(&descriptor);
        if (Instance.Handle == 0) throw new InvalidOperationException("WebGPU instance creation failed.");
    }

    private unsafe void RequestPortableAdapter(nint requestToken, WGPUBackendType backend)
    {
        // wasm32 C lowers a by-value WGPUStringView to a pointer. Mono's reverse
        // P/Invoke wrapper otherwise treats the pointer's stack slot as the
        // struct itself. Explicit browser entry points dereference it once;
        // the generated header signatures and native entry points stay intact.
        var options = WGPURequestAdapterOptions.CreateDefault();
        options.backendType = backend;
        var callback = WGPURequestAdapterCallbackInfo.CreateDefault();
        callback.mode = WGPUCallbackMode.AllowSpontaneous;
        callback.callback = OperatingSystem.IsBrowser()
            ? (delegate* unmanaged[Cdecl]<WGPURequestAdapterStatus, WGPUAdapter, WGPUStringView, void*, void*, void>)(delegate* unmanaged[Cdecl]<WGPURequestAdapterStatus, WGPUAdapter, WGPUStringView*, void*, void*, void>)&OnAsyncAdapterBrowser
            : &OnAsyncAdapter;
        callback.userdata1 = (void*)requestToken;
        Api.wgpuInstanceRequestAdapter(Instance, &options, callback);
    }

    private unsafe void ReadPortableAdapterInfo()
    {
        var info = WGPUAdapterInfo.CreateDefault();
        if (Api.wgpuAdapterGetInfo(Adapter, &info) != WGPUStatus.Success)
            throw new InvalidOperationException("WebGPU adapter information is unavailable.");
        try
        {
            AdapterName = Text(info.device); BackendType = info.backendType;
            AdapterType = info.adapterType; VendorId = info.vendorID; DeviceId = info.deviceID;
        }
        finally { Api.wgpuAdapterInfoFreeMembers(info); }
    }

    private unsafe void RequestPortableDevice(nint requestToken)
    {
        var supported = WGPULimits.CreateDefault();
        if (Api.wgpuAdapterGetLimits(Adapter, &supported) != WGPUStatus.Success)
            throw new InvalidOperationException("WebGPU adapter limits are unavailable.");
        var limits = WGPULimits.CreateDefault();
        limits.maxColorAttachmentBytesPerSample = supported.maxColorAttachmentBytesPerSample;
        limits.maxComputeInvocationsPerWorkgroup = supported.maxComputeInvocationsPerWorkgroup;
        limits.maxComputeWorkgroupSizeX = supported.maxComputeWorkgroupSizeX;
        limits.maxComputeWorkgroupSizeY = supported.maxComputeWorkgroupSizeY;
        limits.maxComputeWorkgroupSizeZ = supported.maxComputeWorkgroupSizeZ;
        limits.maxComputeWorkgroupStorageSize = supported.maxComputeWorkgroupStorageSize;
        var descriptor = WGPUDeviceDescriptor.CreateDefault();
        descriptor.requiredLimits = &limits;
        // This list intentionally contains no wgpu-native extension values.
        var features = stackalloc WGPUFeatureName[7];
        foreach (var feature in new[] { WGPUFeatureName.DepthClipControl, WGPUFeatureName.TimestampQuery,
            WGPUFeatureName.Float32Filterable, WGPUFeatureName.TextureFormatsTier1, WGPUFeatureName.ShaderF16,
            WGPUFeatureName.IndirectFirstInstance, WGPUFeatureName.TextureCompressionBC })
        {
            if (Api.wgpuAdapterHasFeature(Adapter, feature) != 0)
                features[descriptor.requiredFeatureCount++] = feature;
        }
        descriptor.requiredFeatures = features;
        descriptor.deviceLostCallbackInfo.mode = WGPUCallbackMode.AllowSpontaneous;
        descriptor.deviceLostCallbackInfo.callback = OperatingSystem.IsBrowser()
            ? (delegate* unmanaged[Cdecl]<WGPUDevice*, WGPUDeviceLostReason, WGPUStringView, void*, void*, void>)(delegate* unmanaged[Cdecl]<WGPUDevice*, WGPUDeviceLostReason, WGPUStringView*, void*, void*, void>)&OnDeviceLostBrowser
            : &OnDeviceLost;
        descriptor.deviceLostCallbackInfo.userdata1 = (void*)token;
        descriptor.uncapturedErrorCallbackInfo.callback = OperatingSystem.IsBrowser()
            ? (delegate* unmanaged[Cdecl]<WGPUDevice*, WGPUErrorType, WGPUStringView, void*, void*, void>)(delegate* unmanaged[Cdecl]<WGPUDevice*, WGPUErrorType, WGPUStringView*, void*, void*, void>)&OnErrorBrowser
            : &OnError;
        descriptor.uncapturedErrorCallbackInfo.userdata1 = (void*)token;
        var callback = WGPURequestDeviceCallbackInfo.CreateDefault();
        callback.mode = WGPUCallbackMode.AllowSpontaneous;
        callback.callback = OperatingSystem.IsBrowser()
            ? (delegate* unmanaged[Cdecl]<WGPURequestDeviceStatus, WGPUDevice, WGPUStringView, void*, void*, void>)(delegate* unmanaged[Cdecl]<WGPURequestDeviceStatus, WGPUDevice, WGPUStringView*, void*, void*, void>)&OnAsyncDeviceBrowser
            : &OnAsyncDevice;
        callback.userdata1 = (void*)requestToken;
        Api.wgpuAdapterRequestDevice(Adapter, &descriptor, callback);
    }

    private unsafe void FinishPortableDevice()
    {
        HasDepthClipControl = Api.wgpuDeviceHasFeature(Device, WGPUFeatureName.DepthClipControl) != 0;
        HasFloat32Filterable = Api.wgpuDeviceHasFeature(Device, WGPUFeatureName.Float32Filterable) != 0;
        HasTextureFormatsTier1 = Api.wgpuDeviceHasFeature(Device, WGPUFeatureName.TextureFormatsTier1) != 0;
        HasTextureCompressionBC = Api.wgpuDeviceHasFeature(Device, WGPUFeatureName.TextureCompressionBC) != 0;
        HasR8Storage = HasTextureFormatsTier1;
        Queue = Api.wgpuDeviceGetQueue(Device);
        if (Queue.Handle == 0) throw new InvalidOperationException("WebGPU queue creation failed.");
        var limits = WGPULimits.CreateDefault();
        if (Api.wgpuDeviceGetLimits(Device, &limits) != WGPUStatus.Success)
            throw new InvalidOperationException("WebGPU device limits are unavailable.");
        Limits = limits;
        ThrowIfFailed();
    }

    private unsafe void RequestQueueCompletion(nint requestToken)
    {
        var callback = WGPUQueueWorkDoneCallbackInfo.CreateDefault();
        callback.mode = WGPUCallbackMode.AllowSpontaneous;
        callback.callback = OperatingSystem.IsBrowser()
            ? (delegate* unmanaged[Cdecl]<WGPUQueueWorkDoneStatus, WGPUStringView, void*, void*, void>)(delegate* unmanaged[Cdecl]<WGPUQueueWorkDoneStatus, WGPUStringView*, void*, void*, void>)&OnAsyncQueueBrowser
            : &OnAsyncQueue;
        callback.userdata1 = (void*)requestToken;
        Api.wgpuQueueOnSubmittedWorkDone(Queue, callback);
    }

    private unsafe void RequestReadMap(WGPUBuffer buffer, int length, nint requestToken)
    {
        var callback = WGPUBufferMapCallbackInfo.CreateDefault();
        callback.mode = WGPUCallbackMode.AllowSpontaneous;
        callback.callback = OperatingSystem.IsBrowser()
            ? (delegate* unmanaged[Cdecl]<WGPUMapAsyncStatus, WGPUStringView, void*, void*, void>)(delegate* unmanaged[Cdecl]<WGPUMapAsyncStatus, WGPUStringView*, void*, void*, void>)&OnAsyncMapBrowser
            : &OnAsyncMap;
        callback.userdata1 = (void*)requestToken;
        Api.wgpuBufferMapAsync(buffer, WGPUMapMode.Read, 0, (nuint)length, callback);
    }

    private static unsafe byte[] CopyMappedBytes(WGPUBuffer buffer, int length)
    {
        var pointer = Api.wgpuBufferGetConstMappedRange(buffer, 0, (nuint)length);
        if (pointer == null) throw new InvalidOperationException("WebGPU returned a null mapped range.");
        return new ReadOnlySpan<byte>(pointer, length).ToArray();
    }
    private static unsafe void ReleaseAdapter(WGPUAdapter adapter) => Api.wgpuAdapterRelease(adapter);
    private static unsafe void ReleaseDevice(WGPUDevice device) => Api.wgpuDeviceRelease(device);
    private static unsafe void AddBufferReference(WGPUBuffer buffer) => Api.wgpuBufferAddRef(buffer);
    private static unsafe void UnmapAndRelease(WGPUBuffer buffer)
    {
        Api.wgpuBufferUnmap(buffer);
        Api.wgpuBufferRelease(buffer);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnAsyncAdapter(WGPURequestAdapterStatus status, WGPUAdapter adapter, WGPUStringView message, void* user1, void* user2) => HandleAsyncAdapter(status, adapter, message, user1, user2);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnAsyncAdapterBrowser(WGPURequestAdapterStatus status, WGPUAdapter adapter, WGPUStringView* message, void* user1, void* user2) => HandleAsyncAdapter(status, adapter, *message, user1, user2);

    private static unsafe void HandleAsyncAdapter(WGPURequestAdapterStatus status, WGPUAdapter adapter, WGPUStringView message, void* user1, void* user2)
    {
        if (AsyncAdapters.TryRemove((nint)user1, out var request))
            request.Complete(adapter, status == WGPURequestAdapterStatus.Success && adapter.Handle != 0 ? null : $"WebGPU adapter request failed ({status}): {Text(message)}");
        else if (adapter.Handle != 0) ReleaseAdapter(adapter);
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnAsyncDevice(WGPURequestDeviceStatus status, WGPUDevice device, WGPUStringView message, void* user1, void* user2) => HandleAsyncDevice(status, device, message, user1, user2);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnAsyncDeviceBrowser(WGPURequestDeviceStatus status, WGPUDevice device, WGPUStringView* message, void* user1, void* user2) => HandleAsyncDevice(status, device, *message, user1, user2);

    private static unsafe void HandleAsyncDevice(WGPURequestDeviceStatus status, WGPUDevice device, WGPUStringView message, void* user1, void* user2)
    {
        if (AsyncDevices.TryRemove((nint)user1, out var request))
            request.Complete(device, status == WGPURequestDeviceStatus.Success && device.Handle != 0 ? null : $"WebGPU device request failed ({status}): {Text(message)}");
        else if (device.Handle != 0) ReleaseDevice(device);
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnAsyncQueue(WGPUQueueWorkDoneStatus status, WGPUStringView message, void* user1, void* user2) => HandleAsyncQueue(status, message, user1, user2);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnAsyncQueueBrowser(WGPUQueueWorkDoneStatus status, WGPUStringView* message, void* user1, void* user2) => HandleAsyncQueue(status, *message, user1, user2);

    private static unsafe void HandleAsyncQueue(WGPUQueueWorkDoneStatus status, WGPUStringView message, void* user1, void* user2)
    {
        if (AsyncQueues.TryRemove((nint)user1, out var request))
            request.Complete(true, status == WGPUQueueWorkDoneStatus.Success ? null : $"WebGPU queue completion failed ({status}): {Text(message)}");
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnAsyncMap(WGPUMapAsyncStatus status, WGPUStringView message, void* user1, void* user2) => HandleAsyncMap(status, message, user1, user2);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnAsyncMapBrowser(WGPUMapAsyncStatus status, WGPUStringView* message, void* user1, void* user2) => HandleAsyncMap(status, *message, user1, user2);

    private static unsafe void HandleAsyncMap(WGPUMapAsyncStatus status, WGPUStringView message, void* user1, void* user2)
    {
        if (AsyncMaps.TryRemove((nint)user1, out var request))
            request.Complete(true, status == WGPUMapAsyncStatus.Success ? null : $"WebGPU mapping failed ({status}): {Text(message)}");
    }
}

/// <summary>Owns an unconsumed callback result, including cancellation racing completion.</summary>
internal sealed class WebGpuAsyncRequest<T>(Action<T> release) where T : unmanaged
{
    private readonly object gate = new();
    private readonly TaskCompletionSource<bool> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool finished, abandoned;
    private T value;

    public void Complete(T result, string? error)
    {
        lock (gate)
        {
            if (finished || abandoned || error is not null)
            {
                if (!result.Equals(default(T))) release(result);
            }
            else value = result;
            if (finished || abandoned) return;
            finished = true;
            if (error is null) completion.TrySetResult(true);
            else completion.TrySetException(new InvalidOperationException(error));
        }
    }

    public async Task<T> WaitAsync(Action poll, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            if (OperatingSystem.IsBrowser()) await completion.Task.WaitAsync(cancellation.Token);
            else
            {
                while (!completion.Task.IsCompleted)
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    poll();
                    if (!completion.Task.IsCompleted) await Task.Delay(1, cancellation.Token);
                }
                await completion.Task;
            }
            cancellation.Token.ThrowIfCancellationRequested();
            lock (gate)
            {
                var result = value;
                value = default;
                return result;
            }
        }
        catch
        {
            lock (gate)
            {
                abandoned = true;
                if (!value.Equals(default(T))) { release(value); value = default; }
            }
            if (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                throw new TimeoutException("WebGPU request did not complete in 30 seconds.");
            throw;
        }
    }
}
#endif
