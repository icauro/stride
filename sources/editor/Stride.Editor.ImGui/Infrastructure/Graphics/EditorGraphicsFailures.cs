using System.Runtime.ExceptionServices;
using Stride.Graphics;

namespace Stride.Editor.ImGui;

internal static class EditorGraphicsFailures
{
    /// <summary>Keep device failures at the host boundary, separate from asset build errors.</summary>
    public static void ThrowIfDeviceLost(GraphicsDevice device, Exception error)
    {
        var pending = new Stack<Exception>();
        pending.Push(error);
        var seen = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        while (pending.TryPop(out var current))
        {
            if (!seen.Add(current)) continue;
            if (current is GraphicsDeviceException { Status: not GraphicsDeviceStatus.Normal } failure
                && ReferenceEquals(failure.Device, device))
            {
                if (ReferenceEquals(error, current)) ExceptionDispatchInfo.Capture(current).Throw();
                // Preserve grouped mutation/cleanup failures as the actual cause.
                throw GraphicsDeviceException.FromDeviceFailure(device, failure.Message, failure.Status, error);
            }
            if (current is AggregateException aggregate)
                foreach (var inner in aggregate.InnerExceptions) pending.Push(inner);
            else if (current.InnerException != null) pending.Push(current.InnerException);
        }
        var status = device.GraphicsDeviceStatus;
        if (status != GraphicsDeviceStatus.Normal) throw GraphicsDeviceException.FromLostDevice(device, status, error);
    }
}
