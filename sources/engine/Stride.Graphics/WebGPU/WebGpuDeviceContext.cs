// Copyright (c) .NET Foundation and Contributors.
// Distributed under the MIT license. See LICENSE.md in the project root.
#if STRIDE_GRAPHICS_API_WEBGPU
#nullable enable annotations
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Stride.Graphics.WebGPU;
using Api = Stride.Graphics.WebGPU.WebGPU;

namespace Stride.Graphics;

/// <summary>Owns the native objects and completion/error handling for one WebGPU device.</summary>
internal sealed unsafe partial class WebGpuDeviceContext : IDisposable
{
    private static long nextToken;
    private static readonly ConcurrentDictionary<nint, Pending<WGPUAdapter>> AdapterRequests = new();
    private static readonly ConcurrentDictionary<nint, Pending<WGPUDevice>> DeviceRequests = new();
    private static readonly ConcurrentDictionary<nint, Pending<bool>> QueueRequests = new();
    private static readonly ConcurrentDictionary<nint, Pending<bool>> MapRequests = new();
    private static readonly ConcurrentDictionary<nint, Pending<bool>> FormatRequests = new();
    private static readonly ConcurrentDictionary<nint, DeviceState> Devices = new();
    private readonly nint token;
    private readonly DeviceState state;
    private readonly Func<string, Exception> deviceLostException;
    private readonly Dictionary<uint, WGPUBuffer> nullReadBuffers = new();
    private bool disposed;
    private readonly CancellationTokenSource lifetime = new();
    public WGPUInstance Instance { get; private set; }
    public WGPUAdapter Adapter { get; private set; }
    public WGPUDevice Device { get; private set; }
    public WGPUQueue Queue { get; private set; }
    public WGPULimits Limits { get; private set; }
    public string AdapterName { get; private set; } = string.Empty;
    public WGPUBackendType BackendType { get; private set; }
    public WGPUAdapterType AdapterType { get; private set; }
    public uint VendorId { get; private set; }
    public uint DeviceId { get; private set; }
    public bool HasDepthClipControl { get; private set; }
    public bool HasFloat32Filterable { get; private set; }
    public bool HasTextureFormatsTier1 { get; private set; }
    public bool HasTextureCompressionBC { get; private set; }
    public bool HasR8Storage { get; private set; }
    public bool HasClampToBorder { get; private set; }
    public bool HasEncoderTimestamps { get; private set; }
    public bool IsLost => Volatile.Read(ref state.Lost) != 0;
    public string DeviceLostDetails => state.LostMessage;

    private WebGpuDeviceContext(Func<string, Exception>? deviceLostException)
    {
        this.deviceLostException = deviceLostException ?? (message => new InvalidOperationException(message));
        token = NewToken();
        state = new DeviceState();
        if (!Devices.TryAdd(token, state)) throw new InvalidOperationException("Duplicate device token.");
    }

    public static WebGpuDeviceContext Create(string nativeLibrary, WGPUBackendType backend = WGPUBackendType.Undefined, Func<string, Exception>? deviceLostException = null, bool debug = false)
    {
        Api.LoadNativeLibrary(nativeLibrary);
        var context = new WebGpuDeviceContext(deviceLostException);
        try
        {
            var instanceDescriptor = WGPUInstanceDescriptor.CreateDefault();
            var extras = new WGPUInstanceExtras
            {
                chain = new WGPUChainedStruct { sType = (WGPUSType)WGPUNativeSType.WGPUSType_InstanceExtras },
                backends = WGPUInstanceBackend.All,
                flags = WGPUInstanceFlag.Default | WGPUInstanceFlag.Debugging,
            };
            if (debug) instanceDescriptor.nextInChain = &extras.chain;
            context.Instance = Api.wgpuCreateInstance(&instanceDescriptor);
            if (context.Instance.Handle == 0) throw new InvalidOperationException("WebGPU instance creation failed.");
            var adapterRequest = new Pending<WGPUAdapter>(a => Api.wgpuAdapterRelease(a));
            var adapterToken = NewToken();
            AdapterRequests[adapterToken] = adapterRequest;
            try
            {
                var options = WGPURequestAdapterOptions.CreateDefault();
                options.backendType = backend;
                options.forceFallbackAdapter = Environment.GetEnvironmentVariable("STRIDE_GRAPHICS_SOFTWARE_RENDERING") == "1" ? Api.WGPU_TRUE : Api.WGPU_FALSE;
                var callback = WGPURequestAdapterCallbackInfo.CreateDefault();
                callback.mode = WGPUCallbackMode.AllowProcessEvents;
                callback.callback = &OnAdapter;
                callback.userdata1 = (void*)adapterToken;
                Api.wgpuInstanceRequestAdapter(context.Instance, &options, callback);
                context.Adapter = adapterRequest.Wait(context.Poll);
            }
            finally { AdapterRequests.TryRemove(adapterToken, out _); }

            var info = WGPUAdapterInfo.CreateDefault();
            if (Api.wgpuAdapterGetInfo(context.Adapter, &info) != WGPUStatus.Success)
                throw new InvalidOperationException("WebGPU adapter information is unavailable.");
            try
            {
                context.AdapterName = Text(info.device); context.BackendType = info.backendType;
                context.AdapterType = info.adapterType; context.VendorId = info.vendorID; context.DeviceId = info.deviceID;
            }
            finally { Api.wgpuAdapterInfoFreeMembers(info); }

            var deviceRequest = new Pending<WGPUDevice>(d => Api.wgpuDeviceRelease(d));
            var deviceToken = NewToken();
            DeviceRequests[deviceToken] = deviceRequest;
            try
            {
                var supportedLimits = WGPULimits.CreateDefault();
                if (Api.wgpuAdapterGetLimits(context.Adapter, &supportedLimits) != WGPUStatus.Success)
                    throw new InvalidOperationException("WebGPU adapter limits are unavailable.");
                var requiredLimits = WGPULimits.CreateDefault();
                // Stride's SH reduction uses three RGBA32F attachments (48 bytes).
                // Request the adapter's actual budget instead of the 32-byte core default.
                requiredLimits.maxColorAttachmentBytesPerSample = supportedLimits.maxColorAttachmentBytesPerSample;
                requiredLimits.maxComputeInvocationsPerWorkgroup = supportedLimits.maxComputeInvocationsPerWorkgroup;
                requiredLimits.maxComputeWorkgroupSizeX = supportedLimits.maxComputeWorkgroupSizeX;
                requiredLimits.maxComputeWorkgroupSizeY = supportedLimits.maxComputeWorkgroupSizeY;
                requiredLimits.maxComputeWorkgroupSizeZ = supportedLimits.maxComputeWorkgroupSizeZ;
                requiredLimits.maxComputeWorkgroupStorageSize = supportedLimits.maxComputeWorkgroupStorageSize;
                var descriptor = WGPUDeviceDescriptor.CreateDefault();
                descriptor.requiredLimits = &requiredLimits;
                var optionalFeatures = stackalloc WGPUFeatureName[10];
                foreach (var feature in new[] { WGPUFeatureName.DepthClipControl, (WGPUFeatureName)WGPUNativeFeature.AddressModeClampToBorder,
                    WGPUFeatureName.TimestampQuery, (WGPUFeatureName)WGPUNativeFeature.TimestampQueryInsideEncoders,
                    WGPUFeatureName.Float32Filterable, WGPUFeatureName.TextureFormatsTier1,
                    WGPUFeatureName.ShaderF16, WGPUFeatureName.IndirectFirstInstance, WGPUFeatureName.TextureCompressionBC,
                    (WGPUFeatureName)WGPUNativeFeature.TextureAdapterSpecificFormatFeatures })
                {
                    if (Api.wgpuAdapterHasFeature(context.Adapter, feature) != 0)
                        optionalFeatures[descriptor.requiredFeatureCount++] = feature;
                }
                descriptor.requiredFeatures = optionalFeatures;
                descriptor.deviceLostCallbackInfo.mode = WGPUCallbackMode.AllowProcessEvents;
                descriptor.deviceLostCallbackInfo.callback = &OnDeviceLost;
                descriptor.deviceLostCallbackInfo.userdata1 = (void*)context.token;
                descriptor.uncapturedErrorCallbackInfo.callback = &OnError;
                descriptor.uncapturedErrorCallbackInfo.userdata1 = (void*)context.token;
                var callback = WGPURequestDeviceCallbackInfo.CreateDefault();
                callback.mode = WGPUCallbackMode.AllowProcessEvents;
                callback.callback = &OnDevice;
                callback.userdata1 = (void*)deviceToken;
                Api.wgpuAdapterRequestDevice(context.Adapter, &descriptor, callback);
                context.Device = deviceRequest.Wait(context.Poll);
                context.HasDepthClipControl = Api.wgpuDeviceHasFeature(context.Device, WGPUFeatureName.DepthClipControl) != 0;
                context.HasFloat32Filterable = Api.wgpuDeviceHasFeature(context.Device, WGPUFeatureName.Float32Filterable) != 0;
                context.HasTextureFormatsTier1 = Api.wgpuDeviceHasFeature(context.Device, WGPUFeatureName.TextureFormatsTier1) != 0;
                context.HasTextureCompressionBC = Api.wgpuDeviceHasFeature(context.Device, WGPUFeatureName.TextureCompressionBC) != 0;
                context.HasR8Storage = context.HasTextureFormatsTier1 ||
                    (Api.wgpuDeviceHasFeature(context.Device, (WGPUFeatureName)WGPUNativeFeature.TextureAdapterSpecificFormatFeatures) != 0 && context.ProbeR8Storage());
                context.HasClampToBorder = Api.wgpuDeviceHasFeature(context.Device, (WGPUFeatureName)WGPUNativeFeature.AddressModeClampToBorder) != 0;
                context.HasEncoderTimestamps = Api.wgpuDeviceHasFeature(context.Device, WGPUFeatureName.TimestampQuery) != 0
                    && Api.wgpuDeviceHasFeature(context.Device, (WGPUFeatureName)WGPUNativeFeature.TimestampQueryInsideEncoders) != 0;
            }
            finally { DeviceRequests.TryRemove(deviceToken, out _); }
            context.Queue = Api.wgpuDeviceGetQueue(context.Device);
            if (context.Queue.Handle == 0) throw new InvalidOperationException("WebGPU queue creation failed.");
            var limits = WGPULimits.CreateDefault();
            if (Api.wgpuDeviceGetLimits(context.Device, &limits) != WGPUStatus.Success)
                throw new InvalidOperationException("WebGPU device limits are unavailable.");
            context.Limits = limits;
            context.ThrowIfFailed();
            return context;
        }
        catch { context.Dispose(); throw; }
    }

    private void Poll()
    {
        if (OperatingSystem.IsBrowser()) return;
        Api.wgpuInstanceProcessEvents(Instance);
        if (Device.Handle != 0) Api.wgpuDevicePoll(Device, 0, null);
    }

    private bool ProbeR8Storage()
    {
        // The pinned native API exposes adapter-specific formats without a
        // format-capability query. Probe under a validation error scope rather
        // than assuming that enabling the extension guarantees R8 storage.
        var requestToken = NewToken();
        var request = new Pending<bool>(_ => { });
        FormatRequests[requestToken] = request;
        Api.wgpuDevicePushErrorScope(Device, WGPUErrorFilter.Validation);
        var descriptor = WGPUTextureDescriptor.CreateDefault();
        descriptor.dimension = WGPUTextureDimension._2D;
        descriptor.size = new WGPUExtent3D { width = 1, height = 1, depthOrArrayLayers = 1 };
        descriptor.format = WGPUTextureFormat.R8Unorm;
        descriptor.usage = WGPUTextureUsage.StorageBinding | WGPUTextureUsage.TextureBinding | WGPUTextureUsage.CopySrc | WGPUTextureUsage.CopyDst;
        var texture = Api.wgpuDeviceCreateTexture(Device, &descriptor);
        var callback = WGPUPopErrorScopeCallbackInfo.CreateDefault();
        callback.mode = WGPUCallbackMode.AllowProcessEvents;
        callback.callback = &OnFormatProbe;
        callback.userdata1 = (void*)requestToken;
        Api.wgpuDevicePopErrorScope(Device, callback);
        try { return request.Wait(Poll); }
        finally
        {
            FormatRequests.TryRemove(requestToken, out _);
            if (texture.Handle != 0) Api.wgpuTextureRelease(texture);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnFormatProbe(WGPUPopErrorScopeStatus status, WGPUErrorType type, WGPUStringView message, void* user1, void* user2)
    {
        if (FormatRequests.TryRemove((nint)user1, out var request))
            request.Complete(type == WGPUErrorType.NoError, status == WGPUPopErrorScopeStatus.Success ? null : $"WebGPU format probe failed: {Text(message)}");
    }

    public void ProcessEvents()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        Poll();
        ThrowIfFailed();
    }

    public void WaitForIdle()
    {
        if (OperatingSystem.IsBrowser()) throw new PlatformNotSupportedException("Use asynchronous WebGPU queue completion in a browser.");
        ObjectDisposedException.ThrowIf(disposed, this);
        ThrowIfFailed();
        var request = new Pending<bool>(_ => { });
        var requestToken = NewToken();
        QueueRequests[requestToken] = request;
        try
        {
            var callback = WGPUQueueWorkDoneCallbackInfo.CreateDefault();
            callback.mode = WGPUCallbackMode.AllowProcessEvents;
            callback.callback = &OnQueueDone;
            callback.userdata1 = (void*)requestToken;
            Api.wgpuQueueOnSubmittedWorkDone(Queue, callback);
            request.Wait(Poll);
            ThrowIfFailed();
        }
        finally { QueueRequests.TryRemove(requestToken, out _); }
    }

    public void ThrowIfFailed()
    {
        if (IsLost) throw deviceLostException("WebGPU device lost: " + state.LostMessage);
        if (state.Errors.TryDequeue(out var error)) throw new InvalidOperationException(error);
    }

    internal WGPUBuffer NullReadBuffer(uint elementStride)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var size = Math.Max(16u, (elementStride + 3) & ~3u);
        lock (nullReadBuffers)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (nullReadBuffers.TryGetValue(size, out var buffer)) return buffer;
            var descriptor = WGPUBufferDescriptor.CreateDefault();
            descriptor.size = size;
            descriptor.usage = WGPUBufferUsage.Storage;
            buffer = Api.wgpuDeviceCreateBuffer(Device, &descriptor);
            try { ThrowIfFailed(); nullReadBuffers.Add(size, buffer); return buffer; }
            catch { if (buffer.Handle != 0) Api.wgpuBufferRelease(buffer); throw; }
        }
    }

    public byte[] ReadBuffer(WGPUBuffer buffer, int length)
    {
        var pointer = MapBufferRead(buffer, checked((ulong)length));
        try { return new ReadOnlySpan<byte>((void*)pointer, length).ToArray(); }
        finally { Api.wgpuBufferUnmap(buffer); }
    }

    public nint MapBufferRead(WGPUBuffer buffer, ulong length)
    {
        if (OperatingSystem.IsBrowser()) throw new PlatformNotSupportedException("Use asynchronous WebGPU readback in a browser.");
        CancelPendingRead(buffer);
        ObjectDisposedException.ThrowIf(disposed, this);
        if (length <= 0 || length % 4 != 0) throw new ArgumentOutOfRangeException(nameof(length));
        ThrowIfFailed();
        var request = new Pending<bool>(_ => { });
        var requestToken = NewToken();
        MapRequests[requestToken] = request;
        var mapped = false;
        try
        {
            var callback = WGPUBufferMapCallbackInfo.CreateDefault();
            callback.mode = WGPUCallbackMode.AllowProcessEvents;
            callback.callback = &OnMap;
            callback.userdata1 = (void*)requestToken;
            Api.wgpuBufferMapAsync(buffer, WGPUMapMode.Read, 0, (nuint)length, callback);
            request.Wait(Poll);
            var pointer = Api.wgpuBufferGetConstMappedRange(buffer, 0, (nuint)length);
            if (pointer == null) throw new InvalidOperationException("WebGPU returned a null mapped range.");
            mapped = true;
            return (nint)pointer;
        }
        finally
        {
            // Unmap also cancels a pending map on failure/timeout. Its late callback
            // can only observe an expired token, never a stack or managed pointer.
            if (!mapped) Api.wgpuBufferUnmap(buffer);
            MapRequests.TryRemove(requestToken, out _);
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        lifetime.Cancel();
        ReleaseNullReadTexture();
        lock (pendingReadBuffers)
        {
            foreach (var buffer in new List<WGPUBuffer>(pendingReadBuffers.Keys)) CancelPendingRead(buffer);
        }
        // Callback userdata are integer tokens, never pointers to managed or freed memory.
        // A late callback can safely find that its context has already been removed.
        lock (nullReadBuffers)
        {
            foreach (var buffer in nullReadBuffers.Values) Api.wgpuBufferRelease(buffer);
            nullReadBuffers.Clear();
        }
        if (Device.Handle != 0) Api.wgpuDeviceDestroy(Device);
        if (Queue.Handle != 0) Api.wgpuQueueRelease(Queue);
        if (Device.Handle != 0) Api.wgpuDeviceRelease(Device);
        if (Adapter.Handle != 0) Api.wgpuAdapterRelease(Adapter);
        if (Instance.Handle != 0) Api.wgpuInstanceRelease(Instance);
        Devices.TryRemove(token, out _);
        Queue = default; Device = default; Adapter = default; Instance = default;
    }

    private static nint NewToken() => checked((nint)Interlocked.Increment(ref nextToken));
    private static string Text(WGPUStringView text) => text.data == null ? string.Empty :
        text.length == nuint.MaxValue ? Marshal.PtrToStringUTF8((nint)text.data) ?? string.Empty :
        Marshal.PtrToStringUTF8((nint)text.data, checked((int)text.length)) ?? string.Empty;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnAdapter(WGPURequestAdapterStatus status, WGPUAdapter adapter, WGPUStringView message, void* user1, void* user2)
    {
        if (AdapterRequests.TryRemove((nint)user1, out var request))
            request.Complete(adapter, status == WGPURequestAdapterStatus.Success && adapter.Handle != 0 ? null : $"WebGPU adapter request failed ({status}): {Text(message)}");
        else if (adapter.Handle != 0) Api.wgpuAdapterRelease(adapter);
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnDevice(WGPURequestDeviceStatus status, WGPUDevice device, WGPUStringView message, void* user1, void* user2)
    {
        if (DeviceRequests.TryRemove((nint)user1, out var request))
            request.Complete(device, status == WGPURequestDeviceStatus.Success && device.Handle != 0 ? null : $"WebGPU device request failed ({status}): {Text(message)}");
        else if (device.Handle != 0) Api.wgpuDeviceRelease(device);
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnQueueDone(WGPUQueueWorkDoneStatus status, WGPUStringView message, void* user1, void* user2)
    {
        if (QueueRequests.TryRemove((nint)user1, out var request))
            request.Complete(true, status == WGPUQueueWorkDoneStatus.Success ? null : $"WebGPU queue completion failed ({status}): {Text(message)}");
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnMap(WGPUMapAsyncStatus status, WGPUStringView message, void* user1, void* user2)
    {
        if (MapRequests.TryRemove((nint)user1, out var request))
            request.Complete(true, status == WGPUMapAsyncStatus.Success ? null : $"WebGPU mapping failed ({status}): {Text(message)}");
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnError(WGPUDevice* device, WGPUErrorType type, WGPUStringView message, void* user1, void* user2) => HandleError(device, type, message, user1, user2);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnErrorBrowser(WGPUDevice* device, WGPUErrorType type, WGPUStringView* message, void* user1, void* user2) => HandleError(device, type, *message, user1, user2);

    private static void HandleError(WGPUDevice* device, WGPUErrorType type, WGPUStringView message, void* user1, void* user2)
    {
        if (Devices.TryGetValue((nint)user1, out var state)) state.Errors.Enqueue($"WebGPU {type}: {Text(message)}");
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnDeviceLost(WGPUDevice* device, WGPUDeviceLostReason reason, WGPUStringView message, void* user1, void* user2) => HandleDeviceLost(device, reason, message, user1, user2);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnDeviceLostBrowser(WGPUDevice* device, WGPUDeviceLostReason reason, WGPUStringView* message, void* user1, void* user2) => HandleDeviceLost(device, reason, *message, user1, user2);

    private static void HandleDeviceLost(WGPUDevice* device, WGPUDeviceLostReason reason, WGPUStringView message, void* user1, void* user2)
    {
        if (Devices.TryGetValue((nint)user1, out var state))
        { state.LostMessage = $"{reason}: {Text(message)}"; Volatile.Write(ref state.Lost, 1); }
    }

    private sealed class DeviceState
    {
        public readonly ConcurrentQueue<string> Errors = new();
        public int Lost;
        public string LostMessage = string.Empty;
    }
    private sealed class Pending<T>(Action<T> release) where T : unmanaged
    {
        private readonly object gate = new();
        private bool done, abandoned;
        private T value;
        private string? error;
        public bool IsComplete { get { lock (gate) return done; } }
        public void Complete(T result, string? failure)
        {
            lock (gate)
            {
                if (abandoned || failure is not null)
                {
                    if (!result.Equals(default(T))) release(result);
                }
                else value = result;
                error = failure; done = true;
            }
        }
        public T Wait(Action poll)
        {
            var start = Environment.TickCount64;
            while (true)
            {
                lock (gate)
                {
                    if (done)
                    {
                        if (error is not null) throw new InvalidOperationException(error);
                        return value;
                    }
                    if (Environment.TickCount64 - start >= 30_000)
                    { abandoned = true; throw new TimeoutException("WebGPU request did not complete in 30 seconds."); }
                }
                try { poll(); }
                catch
                {
                    lock (gate)
                    {
                        abandoned = true;
                        if (done && error is null && !value.Equals(default(T)))
                        { release(value); value = default; }
                    }
                    throw;
                }
                Thread.Sleep(1);
            }
        }
    }
}
#endif
