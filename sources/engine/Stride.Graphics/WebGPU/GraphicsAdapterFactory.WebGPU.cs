// Copyright (c) .NET Foundation and Contributors.
// Distributed under the MIT license. See LICENSE.md in the project root.
#if STRIDE_GRAPHICS_API_WEBGPU
using System;
using System.Threading;
using System.Threading.Tasks;
using Stride.Graphics.WebGPU;

namespace Stride.Graphics;

public static partial class GraphicsAdapterFactory
{
    private static readonly SemaphoreSlim WebGpuInitialization = new(1, 1);

    /// <summary>
    /// Initializes adapter metadata and prepares one portable WebGPU device for
    /// synchronous consumption by the game startup path. Required before browser startup.
    /// On desktop the native provider must already be loaded.
    /// </summary>
    public static async Task InitializeWebGPUAsync(CancellationToken cancellationToken = default)
    {
        await WebGpuInitialization.WaitAsync(cancellationToken);
        try
        {
            int version;
            lock (StaticLock)
            {
                if (isInitialized) return;
                version = initializationVersion;
            }
            var context = await WebGpuDeviceContext.CreatePortableAsync(cancellationToken,
                OperatingSystem.IsBrowser() ? WGPUBackendType.Undefined : GetRequestedNativeBackend(), deviceLostException:
                message => new GraphicsDeviceException(message, GraphicsDeviceStatus.Lost));
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (StaticLock)
                {
                    if (version != initializationVersion)
                        throw new OperationCanceledException("The graphics adapter factory was disposed during asynchronous initialization.");
                    if (isInitialized) return;
                    staticCollector = new Stride.Core.ObjectCollector();
                    defaultAdapter = new GraphicsAdapter(context, retainDevice: true);
                    staticCollector.Add(defaultAdapter);
                    adapters = [defaultAdapter];
                    context = null;
                    isInitialized = true;
                }
            }
            finally { context?.Dispose(); }
        }
        finally { WebGpuInitialization.Release(); }
    }

    private static void InitializeInternal()
    {
        if (OperatingSystem.IsBrowser())
            throw new InvalidOperationException("Await GraphicsAdapterFactory.InitializeWebGPUAsync before starting a browser game.");
        staticCollector = new Stride.Core.ObjectCollector();
        var nativePath = Environment.GetEnvironmentVariable("STRIDE_WEBGPU_NATIVE_LIBRARY") ??
            throw new InvalidOperationException("Set STRIDE_WEBGPU_NATIVE_LIBRARY to the pinned native library's absolute path.");
        using var context = WebGpuDeviceContext.Create(nativePath, GetRequestedNativeBackend());
        defaultAdapter = new GraphicsAdapter(context);
        adapters = [defaultAdapter];
    }

    private static WGPUBackendType GetRequestedNativeBackend()
    {
        var backend = WGPUBackendType.Undefined;
        var backendName = Environment.GetEnvironmentVariable("STRIDE_WEBGPU_BACKEND");
        if (!string.IsNullOrEmpty(backendName) && (!Enum.TryParse(backendName, true, out backend) || backend is not (WGPUBackendType.Vulkan or WGPUBackendType.D3D12)))
            throw new ArgumentException("STRIDE_WEBGPU_BACKEND must be Vulkan or D3D12.");
        return backend;
    }
}
#endif
