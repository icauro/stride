using System;

namespace Stride.Graphics;

/// <summary>The actual state of a submitted GPU queue completion marker.</summary>
public enum GraphicsGpuCompletionStatus
{
    Pending,
    Completed,
    DeviceUnavailable,
    Failed,
}

/// <summary>
/// A nonblocking certificate for work submitted before its creation, on one exact
/// graphics device. It does not cover commands submitted after the marker.
/// Create, poll and dispose on the device owner thread, after recording work has
/// been flushed. A failed marker does not authorize resource retirement.
/// </summary>
public abstract class GraphicsGpuCompletion : IDisposable
{
    public GraphicsDevice Device { get; }
    public string? Failure { get; protected set; }
    protected GraphicsGpuCompletion(GraphicsDevice device) => Device = device;
    public abstract GraphicsGpuCompletionStatus Poll();
    public abstract void Dispose();
}

public partial class GraphicsDevice
{
    /// <summary>Actual native loss/release evidence, independent of simulated reset status.</summary>
    public bool IsPhysicallyUnavailable
    {
        get { var unavailable = false; QueryPhysicalDeviceUnavailable(ref unavailable); return unavailable; }
    }
    partial void QueryPhysicalDeviceUnavailable(ref bool unavailable);
    private sealed class UnavailableGpuCompletion(GraphicsDevice device) : GraphicsGpuCompletion(device)
    {
        public override GraphicsGpuCompletionStatus Poll() => GraphicsGpuCompletionStatus.DeviceUnavailable;
        public override void Dispose() { }
    }
    /// <summary>
    /// Submits a completion marker behind already submitted graphics/copy work.
    /// Returns null on an unsupported backend. This method never waits for the GPU.
    /// The caller must stop submitting work that borrows retired resources first.
    /// </summary>
    public GraphicsGpuCompletion? CreateGpuCompletion()
    {
        GraphicsGpuCompletion? completion = null;
        CreateGpuCompletionPlatform(ref completion);
        return completion;
    }

    partial void CreateGpuCompletionPlatform(ref GraphicsGpuCompletion? completion);
}
