// Copyright (c) .NET Foundation and Contributors.
// Distributed under the MIT license. See LICENSE.md in the project root.
#if STRIDE_GRAPHICS_API_WEBGPU
using System;
using System.Threading;
using System.Threading.Tasks;
using Stride.Graphics.WebGPU;
using Api = Stride.Graphics.WebGPU.WebGPU;
namespace Stride.Graphics;
public partial class GraphicsDevice
{
    private const GraphicsPlatform GraphicPlatform = GraphicsPlatform.WebGPU;
    internal WebGpuDeviceContext WebGpuContext { get; private set; }
    internal GraphicsProfile WebGpuRequestedProfile { get; private set; }
    internal GraphicsProfile WebGpuCurrentProfile { get; private set; }
    private WebGpuBufferClear bufferClear;
    internal WebGpuBufferClear BufferClear => bufferClear ??= new WebGpuBufferClear(WebGpuContext);
    public long TimestampFrequency => !OperatingSystem.IsBrowser() && WebGpuContext.HasEncoderTimestamps
        ? checked((long)Math.Round(1_000_000_000.0 / Api.wgpuQueueGetTimestampPeriod(WebGpuContext.Queue))) : 0;
    internal int ConstantBufferDataPlacementAlignment = 256;
    private unsafe partial void InitializePlatformDevice(GraphicsProfile[] graphicsProfiles, DeviceCreationFlags deviceCreationFlags, object windowHandle)
    {
        WebGpuRequestedProfile = graphicsProfiles[0];
        WebGpuCurrentProfile = Array.Find(graphicsProfiles, profile => Adapter.IsProfileSupported(profile));
        if (WebGpuCurrentProfile == 0) throw new NotSupportedException("No requested graphics profile is supported by the WebGPU backend.");
        WebGpuContext = Adapter.TakePreparedDevice();
        if (WebGpuContext is null)
        {
            if (OperatingSystem.IsBrowser()) throw new InvalidOperationException("No prepared browser device remains. Dispose the adapter factory and await InitializeWebGPUAsync before creating another device.");
            var nativePath = Environment.GetEnvironmentVariable("STRIDE_WEBGPU_NATIVE_LIBRARY") ?? throw new InvalidOperationException("STRIDE_WEBGPU_NATIVE_LIBRARY is required.");
            WebGpuContext = WebGpuDeviceContext.Create(nativePath, Adapter.BackendType,
                message => new GraphicsDeviceException(message, GraphicsDeviceStatus.Lost), debug: (deviceCreationFlags & DeviceCreationFlags.Debug) != 0);
        }
        ConstantBufferDataPlacementAlignment = checked((int)WebGpuContext.Limits.minUniformBufferOffsetAlignment);
    }
    /// <summary>Asynchronously waits for WebGPU work; browser callers must yield rather than block the event loop.</summary>
    public Task WaitForWebGPUIdleAsync(CancellationToken cancellationToken = default) => WebGpuContext.WaitForIdleAsync(cancellationToken);
    private bool simulateReset;
    public GraphicsDeviceStatus GraphicsDeviceStatus => simulateReset ? GraphicsDeviceStatus.Reset : WebGpuContext.IsLost ? GraphicsDeviceStatus.Lost : GraphicsDeviceStatus.Normal;
    /// <summary>Simulates device loss for the shared graphics regression suite.</summary>
    public void SimulateReset() => simulateReset = true;
    public void Begin() => WebGpuContext.ProcessEvents();
    public void End() => WebGpuContext.ProcessEvents();
    public unsafe void ExecuteCommandList(CompiledCommandList commandList)
    {
        WebGpuContext.ThrowIfFailed();
        var buffer = (commandList.Lease ?? throw new ArgumentException("The command list is empty.", nameof(commandList))).Take(this, out var readback);
        var buffers = stackalloc WGPUCommandBuffer[2] { buffer, readback };
        var submitted = false;
        try { Api.wgpuQueueSubmit(WebGpuContext.Queue, readback.Handle == 0 ? 1u : 2u, buffers); WebGpuContext.ThrowIfFailed(); submitted = true; }
        finally
        {
            commandList.Lease.NotifySubmission(submitted);
            Api.wgpuCommandBufferRelease(buffer);
            if (readback.Handle != 0) Api.wgpuCommandBufferRelease(readback);
        }
    }
    public void ExecuteCommandLists(int count, CompiledCommandList[] lists)
    {
        ArgumentNullException.ThrowIfNull(lists);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, lists.Length);
        for (var i = 0; i < count; i++) ExecuteCommandList(lists[i]);
    }
    private partial void AdjustDefaultPipelineStateDescription(ref PipelineStateDescription pipelineStateDescription) { }
    private unsafe partial void InitializePostFeatures() { }
    private partial string GetRendererName() => WebGpuContext.AdapterName;
    internal partial string GetDeviceLostDetails() => WebGpuContext.DeviceLostDetails;
    protected partial void DestroyPlatformDevice()
    {
        bufferClear?.Dispose(); bufferClear = null;
        WebGpuContext?.Dispose();
    }
    partial void WaitForGPUIdle() => WebGpuContext?.WaitForIdle();
    internal partial void TagResourceAsNotAlive(GraphicsResourceLink resourceLink) { }
}
#endif
