using Stride.Graphics;
using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorIcons
{
    private sealed class ProviderImage(EditorComponentImageLease lease)
    {
        internal readonly EditorComponentImageLease Lease = lease;
        internal readonly string Key = "provider-component/" + Guid.NewGuid().ToString("N");
        internal Texture? Texture;
        internal ImTextureRef Reference;
        internal bool Registered, TextureDisposed, LeaseDisposed;
        internal int UsedFrame;
        internal GraphicsGpuCompletion? Marker;
        internal readonly List<GraphicsGpuCompletion> Markers = [];
        internal long RetryAt;
        internal bool Certified;
    }
    private readonly Dictionary<object, ProviderImage> providerImages = [];
    private readonly Dictionary<string, ProviderImage> providerImageKeys = new(StringComparer.Ordinal);
    private readonly List<ProviderImage> failedProviderImages = [];
    private readonly List<ProviderImage> retiredProviderImages = [];
    private bool componentImageDraining;
    private bool componentImageRetirementBlocked;
    internal string? ComponentImageRetirementFailure { get; private set; }
    internal void SetComponentImageRetirementBlocked(bool blocked)
    { CheckComponentImageThread(); componentImageRetirementBlocked = blocked; }
    private TaskCompletionSource? componentImageDrain;
    private readonly int iconOwnerThread = Environment.CurrentManagedThreadId;
    private GraphicsDevice iconDevice = null!;
    private EditorExtensionSession? componentImageProviders;
    private long componentImageProviderVersion = -1;

    internal void BindComponentImages(EditorExtensionSession? providers)
    {
        CheckComponentImageThread();
        if (componentImageDraining) return;
        if (ReferenceEquals(componentImageProviders, providers) && componentImageProviderVersion == (providers?.Version ?? -1)) return;
        ClearCustomComponentImages();
        componentImageProviders = providers; componentImageProviderVersion = providers?.Version ?? -1;
    }

    private string? ProviderComponentIcon(Type componentType)
    {
        CheckComponentImageThread();
        if (providerImages.TryGetValue(componentType, out var cached))
        { cached.UsedFrame = Gui.GetFrameCount(); return cached.Key; }
        var lease = componentImageProviders?.AcquireComponentImage(componentType);
        if (lease == null) return null;
        return RegisterProviderImage(componentType, lease);
    }

    private string RegisterProviderImage(object identity, EditorComponentImageLease lease)
    {
        var entry = new ProviderImage(lease) { UsedFrame = Gui.GetFrameCount() };
        try
        {
            using var stream = new MemoryStream(lease.Bytes.ToArray(), writable: false);
            entry.Texture = Texture.Load(iconDevice, stream, loadAsSrgb: true);
            entry.Reference = renderer.RegisterTexture(entry.Texture); entry.Registered = true;
            icons.Add(entry.Key, (entry.Texture, entry.Reference));
            providerImageKeys.Add(entry.Key, entry);
            providerImages.Add(identity, entry);
            return entry.Key;
        }
        catch
        {
            failedProviderImages.Add(entry);
            throw;
        }
    }

    // Call only after main and detached renderer work has actually been submitted.
    internal void RetireUnusedComponentImages()
    {
        CheckComponentImageThread();
        foreach (var pair in providerImages.Where(pair => pair.Value.UsedFrame != Gui.GetFrameCount()).ToArray())
        { RetireProviderImage(pair.Value); providerImages.Remove(pair.Key); }
        if (componentImageRetirementBlocked) return;
        foreach (var entry in retiredProviderImages.ToArray())
        { if (CertifyProviderImage(entry)) { ReleaseProviderImage(entry); retiredProviderImages.Remove(entry); } }
        foreach (var entry in failedProviderImages.ToArray())
        { if (CertifyProviderImage(entry)) { ReleaseProviderImage(entry); failedProviderImages.Remove(entry); } }
        CompleteComponentImageDrain();
    }

    internal void ClearCustomComponentImages()
    {
        CheckComponentImageThread();
        foreach (var pair in providerImages.ToArray())
        { RetireProviderImage(pair.Value); providerImages.Remove(pair.Key); }
        componentImageProviders = null; componentImageProviderVersion = -1;
    }

    internal Task BeginComponentImageDrain(bool graphicsQuiesced = false)
    {
        CheckComponentImageThread();
        componentImageDraining = true;
        componentImageDrain ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
        ClearCustomComponentImages();
        if (graphicsQuiesced && !componentImageRetirementBlocked)
        {
            foreach (var entry in retiredProviderImages.ToArray())
            { ReleaseProviderImage(entry); retiredProviderImages.Remove(entry); }
            foreach (var entry in failedProviderImages.ToArray())
            { ReleaseProviderImage(entry); failedProviderImages.Remove(entry); }
        }
        CompleteComponentImageDrain();
        return componentImageDrain.Task;
    }

    internal void ResumeComponentImages()
    {
        CheckComponentImageThread();
        if (componentImageDrain is { Task.IsCompleted: false })
            throw new InvalidOperationException("Component image retirement is still draining.");
        componentImageDraining = false; componentImageDrain = null;
    }

    private void RetireProviderImage(ProviderImage entry)
    {
        retiredProviderImages.Add(entry);
    }

    private bool CertifyProviderImage(ProviderImage entry)
    {
        if (entry.Certified) return true;
        if (entry.Marker is { } marker)
        {
            if (!ReferenceEquals(marker.Device, iconDevice))
                throw new InvalidOperationException("The provider image marker belongs to another graphics device.");
            var status = marker.Poll();
            if (status == GraphicsGpuCompletionStatus.Completed
                || status == GraphicsGpuCompletionStatus.DeviceUnavailable && iconDevice.IsPhysicallyUnavailable)
            { entry.Certified = true; return true; }
            if (status == GraphicsGpuCompletionStatus.Pending) return false;
            ComponentImageRetirementFailure = marker.Failure ?? "Provider image GPU completion could not be proved.";
        }
        var now = Environment.TickCount64;
        if (now < entry.RetryAt) return false;
        entry.RetryAt = now + 250;
        entry.Marker = iconDevice.CreateGpuCompletion()
            ?? throw new NotSupportedException("The graphics backend cannot certify provider image retirement.");
        entry.Markers.Add(entry.Marker);
        return false;
    }

    private void CompleteComponentImageDrain()
    {
        if (retiredProviderImages.Count == 0 && failedProviderImages.Count == 0) ComponentImageRetirementFailure = null;
        if (componentImageDraining && providerImages.Count == 0 && retiredProviderImages.Count == 0 && failedProviderImages.Count == 0)
            componentImageDrain?.TrySetResult();
    }

    private void EnsureComponentImagesDrained()
    {
        CheckComponentImageThread();
        if (providerImages.Count != 0 || retiredProviderImages.Count != 0 || failedProviderImages.Count != 0)
            throw new InvalidOperationException("Drain component image rendering and provider leases before disposing editor icons.");
    }

    private void ReleaseProviderImage(ProviderImage entry)
    {
        entry.Certified = true;
        if (entry.Registered) { renderer.UnregisterTexture(entry.Reference); entry.Registered = false; }
        if (!entry.TextureDisposed) { entry.Texture?.Dispose(); entry.TextureDisposed = true; }
        foreach (var marker in entry.Markers.ToArray()) { marker.Dispose(); entry.Markers.Remove(marker); }
        entry.Marker = null;
        icons.Remove(entry.Key);
        providerImageKeys.Remove(entry.Key);
        if (!entry.LeaseDisposed) { entry.Lease.Dispose(); entry.LeaseDisposed = true; }
    }

    private void CheckComponentImageThread()
    {
        if (Environment.CurrentManagedThreadId != iconOwnerThread)
            throw new InvalidOperationException("Component images require their owning editor/render thread.");
    }

    private void MarkProviderImageUsed(string key)
    {
        if (!providerImageKeys.TryGetValue(key, out var entry)) return;
        CheckComponentImageThread(); entry.UsedFrame = Gui.GetFrameCount();
        // A cached key can still be drawn while its provider lease is retiring.
        // A marker made before this use cannot certify the new submission. Keep
        // its ownership in Markers until release; create a fresh completion proof
        // after main and detached work have actually been submitted.
        entry.Certified = false;
        entry.Marker = null;
        entry.RetryAt = 0;
    }
}
