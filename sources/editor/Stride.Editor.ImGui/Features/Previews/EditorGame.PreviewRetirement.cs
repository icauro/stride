using Stride.Graphics;

namespace Stride.Editor.ImGui;

/// <summary>An owned CPU worker is still draining after its GPU resources were certified.</summary>
internal sealed class PreviewResourceDrainPendingException : Exception { }

internal interface IEditorResourceRetirementDrain
{
    bool IsRetirementDrainComplete { get; }
    bool PumpRetirementDrain();
}

internal sealed partial class EditorGame
{
    private sealed class RetiredEditorPreview(IDisposable preview, GraphicsDevice device)
    {
        internal readonly IDisposable Preview = preview;
        internal readonly GraphicsDevice Device = device;
        internal readonly List<GraphicsGpuCompletion> Markers = [];
        internal GraphicsGpuCompletion? Marker;
        internal bool Certified, PreviewDisposed;
        internal long RetryAt;
        internal string? Failure;
    }
    private readonly List<RetiredEditorPreview> retiredEditorPreviews = [];
    private readonly HashSet<IDisposable> retiredEditorPreviewOwners = new(ReferenceEqualityComparer.Instance);
    private void RetireEditorPreview(IDisposable preview)
        => RetireEditorPreview(preview, GraphicsDevice);
    private void RetireEditorPreview(IDisposable preview, GraphicsDevice device)
    {
        ArgumentNullException.ThrowIfNull(preview);
        if (retiredEditorPreviewOwners.Contains(preview)) return;
        var retirement = new RetiredEditorPreview(preview, device);
        retiredEditorPreviews.Add(retirement);
        retiredEditorPreviewOwners.Add(preview);
        retiredEditorPreviewRevision++;
    }
    private void PumpRetiredEditorPreviews(bool submittedBoundary = false)
    {
        if (IsEditorGpuRetirementPending) return;
        foreach (var retired in retiredEditorPreviews.ToArray())
        {
            if (Environment.TickCount64 < retired.RetryAt) continue;
            try
            {
                if (retired.Preview is IEditorResourceRetirementDrain drain && !drain.IsRetirementDrainComplete
                    && !drain.PumpRetirementDrain()) continue;
                if (!retired.Certified)
                {
                    if (!ReferenceEquals(retired.Device, GraphicsDevice))
                        throw new InvalidOperationException("A preview device changed before retirement completed.");
                    if (retired.Marker is { } marker)
                    {
                        var status = marker.Poll();
                        if (status == GraphicsGpuCompletionStatus.Pending) continue;
                        if (status == GraphicsGpuCompletionStatus.Completed
                            || status == GraphicsGpuCompletionStatus.DeviceUnavailable && retired.Device.IsPhysicallyUnavailable)
                            retired.Certified = true;
                        else
                        {
                            retired.Marker = null;
                            throw new InvalidOperationException(marker.Failure ?? "Preview GPU completion could not be proved.");
                        }
                    }
                    else
                    {
                        if (!submittedBoundary) continue;
                        if (!retired.Device.IsPhysicallyUnavailable) GraphicsContext.CommandList.SubmitRecordedWorkForRetirement();
                        retired.Marker = retired.Device.CreateGpuCompletion()
                            ?? throw new NotSupportedException("The graphics backend cannot certify preview retirement.");
                        retired.Markers.Add(retired.Marker);
                        continue;
                    }
                }
                ReleaseRetiredEditorPreview(retired);
                retiredEditorPreviews.Remove(retired);
                retiredEditorPreviewOwners.Remove(retired.Preview);
            }
            catch (PreviewResourceDrainPendingException) { }
            catch (Exception error)
            {
                retired.RetryAt = Environment.TickCount64 + 250;
                if (retired.Failure != error.ToString())
                { retired.Failure = error.ToString(); RecordAssetPreviewRenderFailure("Preview retirement", error); }
            }
        }
    }
    private static void ReleaseRetiredEditorPreview(RetiredEditorPreview retired)
    {
        if (!retired.PreviewDisposed) { retired.Preview.Dispose(); retired.PreviewDisposed = true; }
        foreach (var marker in retired.Markers.ToArray()) { marker.Dispose(); retired.Markers.Remove(marker); }
        retired.Marker = null;
    }
    private void ClearRetiredEditorPreviews()
    {
        var certifiedDevice = protectedEditorResourceDisposalDevice
            ?? (editorGpuRetirementCertified && IsEditorGpuRetirementFinalizing ? editorGpuRetirementCertifiedDevice : null);
        if (retiredEditorPreviews.Count != 0 && certifiedDevice == null)
            throw new InvalidOperationException("Forced preview cleanup requires the protected host GPU retirement certificate.");
        // Protected callers supply the host's real global GPU certificate.
        foreach (var retired in retiredEditorPreviews.ToArray())
        {
            if (retired.Preview is IEditorResourceRetirementDrain drain && !drain.IsRetirementDrainComplete) continue;
            if (!ReferenceEquals(retired.Device, certifiedDevice))
                throw new InvalidOperationException("The protected GPU certificate does not cover this preview's allocating device.");
            retired.Certified = true;
            try { ReleaseRetiredEditorPreview(retired); }
            catch (PreviewResourceDrainPendingException) { continue; }
            retiredEditorPreviews.Remove(retired);
            retiredEditorPreviewOwners.Remove(retired.Preview);
        }
    }
}
