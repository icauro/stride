namespace Stride.Graphics;

public partial class CommandList
{
    /// <summary>
    /// Submits an actually recording list at an owner-thread retirement boundary,
    /// without waiting or resetting it. A healthy native list left by a failed
    /// frame must precede the completion marker. Already closed lists and
    /// physically unavailable devices are untouched. This ends recording; the
    /// caller must not continue a frame on the same list without its usual Reset.
    /// </summary>
    public void SubmitRecordedWorkForRetirement()
    {
        if (GraphicsDevice.IsPhysicallyUnavailable) return;
        SubmitRecordedWorkForRetirementPlatform();
    }
    partial void SubmitRecordedWorkForRetirementPlatform();
}
