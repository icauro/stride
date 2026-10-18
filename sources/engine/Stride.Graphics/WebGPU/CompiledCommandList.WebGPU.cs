// Copyright (c) .NET Foundation and Contributors.
// Distributed under the MIT license. See LICENSE.md in the project root.
#if STRIDE_GRAPHICS_API_WEBGPU
using System;
using System.Threading;
using Stride.Graphics.WebGPU;
using Api = Stride.Graphics.WebGPU.WebGPU;
namespace Stride.Graphics;

public partial struct CompiledCommandList
{
    // The shared lease prevents copies of this public value type from submitting
    // or releasing a command buffer twice.
    internal WebGpuCommandBufferLease Lease;
}

internal sealed class WebGpuCommandBufferLease(GraphicsDevice device, WGPUCommandBuffer buffer, WGPUCommandBuffer readback, Action<bool>[] queryCompletions)
{
    private nint handle = buffer.Handle;
    private nint readbackHandle = readback.Handle;
    internal GraphicsDevice Device { get; } = device;
    private Action<bool>[] completions = queryCompletions;
    internal void NotifySubmission(bool submitted)
    {
        foreach (var completion in Interlocked.Exchange(ref completions, Array.Empty<Action<bool>>())) completion(submitted);
    }

    internal WGPUCommandBuffer Take(GraphicsDevice device, out WGPUCommandBuffer readback)
    {
        if (!ReferenceEquals(Device, device)) throw new InvalidOperationException("The WebGPU command list belongs to another device.");
        var value = Interlocked.Exchange(ref handle, 0);
        if (value == 0) throw new InvalidOperationException("The WebGPU command list has already been submitted.");
        readback = new WGPUCommandBuffer(Interlocked.Exchange(ref readbackHandle, 0));
        GC.SuppressFinalize(this);
        return new WGPUCommandBuffer(value);
    }

    ~WebGpuCommandBufferLease()
    {
        var value = Interlocked.Exchange(ref handle, 0);
        if (value != 0) Api.wgpuCommandBufferRelease(new WGPUCommandBuffer(value));
        value = Interlocked.Exchange(ref readbackHandle, 0);
        if (value != 0) Api.wgpuCommandBufferRelease(new WGPUCommandBuffer(value));
        NotifySubmission(false);
    }
}
#endif
