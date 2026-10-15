using System.Numerics;
using Hexa.NET.ImGui;
using Stride.Graphics;

namespace Stride.Editor.ImGui;

/// <summary>One renderer/device generation of images from native shaped glyph coverage.</summary>
internal sealed class WindowsGlyphImages(GraphicsDevice device, ImGuiRenderer renderer) : IDisposable
{
    private sealed class Image(Texture texture)
    {
        internal readonly Texture Texture = texture;
        internal ImTextureRef Reference;
        internal bool Registered;
        internal long UsedFrame;
        internal long ByteSize => (long)Texture.Width * Texture.Height * 4;
    }
    private sealed class Retirement(Image[] images)
    {
        internal readonly List<Image> Images = [.. images];
        internal readonly List<GraphicsGpuCompletion> Markers = [];
        internal GraphicsGpuCompletion? Marker;
        internal long NextCertificateAttempt;
    }
    private readonly int ownerThread = Environment.CurrentManagedThreadId;
    // The native coverage array is immutable and owned by its CPU layout cache.
    private readonly Dictionary<byte[], Image> images = [];
    private readonly List<Image> pending = [];
    private readonly List<Retirement> retirements = [];
    private readonly TaskCompletionSource drain = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long frame;
    private long residentBytes;
    private const long ResidentByteBudget = 32L * 1024 * 1024;
    private const long IdleFrameBudget = 120;
    private bool draining, disposed;
    internal string? RetirementFailure { get; private set; }
    internal bool IsDraining => draining;

    internal void Draw(ImDrawListPtr draw, CommandList commands, WindowsTextLayout.GlyphRasterizer.Bitmap bitmap, Vector2 origin, uint color)
    {
        CheckOwner();
        if (draining) throw new InvalidOperationException("This native glyph image generation is retiring.");
        if (!ReferenceEquals(commands.GraphicsDevice, device)) throw new InvalidOperationException("Glyph upload commands belong to a different graphics device.");
        if (bitmap.Width == 0 || bitmap.Height == 0) return;
        if (bitmap.Coverage.Length != checked(bitmap.Width * bitmap.Height)) throw new InvalidDataException("The native glyph coverage bounds are inconsistent.");
        if (!images.TryGetValue(bitmap.Coverage, out var image))
        {
            image = new(Texture.New2D(device, bitmap.Width, bitmap.Height, PixelFormat.R8G8B8A8_UNorm));
            try
            {
                var pixels = new byte[checked(bitmap.Coverage.Length * 4)];
                for (var index = 0; index < bitmap.Coverage.Length; index++)
                {
                    var offset = index * 4; pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = 255;
                    pixels[offset + 3] = bitmap.Coverage[index];
                }
                image.Reference = renderer.RegisterTexture(image.Texture); image.Registered = true;
                image.Texture.SetData(commands, (ReadOnlySpan<byte>)pixels);
                images.Add(bitmap.Coverage, image);
                residentBytes += image.ByteSize;
            }
            catch
            {
                // Upload failure can still leave recorded commands borrowing this resource.
                pending.Add(image); throw;
            }
        }
        image.UsedFrame = frame;
        var minimum = origin + new Vector2(bitmap.Left, bitmap.Top);
        draw.AddImage(image.Reference, minimum, minimum + new Vector2(bitmap.Width, bitmap.Height), Vector2.Zero, Vector2.One, color);
    }

    /// <summary>Only after ALL main/detached renderer work has been submitted.</summary>
    internal void EndSubmittedFrame()
    {
        CheckOwner();
        // Cache age selects eviction candidates only. Actual resource release
        // still requires the submitted GPU completion marker below.
        if (residentBytes > ResidentByteBudget || images.Any(pair => frame - pair.Value.UsedFrame >= IdleFrameBudget))
        foreach (var pair in images.Where(pair => pair.Value.UsedFrame != frame)
                     .OrderBy(pair => pair.Value.UsedFrame).ToArray())
        {
            if (residentBytes <= ResidentByteBudget && frame - pair.Value.UsedFrame < IdleFrameBudget) break;
            pending.Add(pair.Value); images.Remove(pair.Key); residentBytes -= pair.Value.ByteSize;
        }
        frame++;
        SealSubmittedRetirements(); PumpRetirements();
    }
    internal Task BeginDrain()
    {
        CheckOwner(); draining = true;
        pending.AddRange(images.Values); images.Clear(); residentBytes = 0; CompleteDrain(); return drain.Task;
    }
    /// <summary>Requires a genuine submitted boundary or an inactive boundary with no recorded work.</summary>
    internal void SealSubmittedRetirements()
    {
        CheckOwner();
        if (pending.Count != 0) { retirements.Add(new(pending.ToArray())); pending.Clear(); }
        foreach (var retirement in retirements)
        {
            if (retirement.Marker is { } previous && previous.Poll() is GraphicsGpuCompletionStatus.Pending or GraphicsGpuCompletionStatus.Completed) continue;
            var now = Environment.TickCount64;
            if (now < retirement.NextCertificateAttempt) continue;
            retirement.NextCertificateAttempt = now + 250;
            // A later real certificate may prove old work after a failed query; retain
            // every prior marker until the batch is actually safe to release.
            var marker = device.CreateGpuCompletion();
            if (marker == null) { RetirementFailure = "The selected graphics backend cannot certify glyph image retirement."; continue; }
            retirement.Markers.Add(marker); retirement.Marker = marker;
        }
    }
    internal void PumpRetirements()
    {
        CheckOwner();
        foreach (var retirement in retirements.ToArray())
        {
            if (retirement.Marker is not { } marker) continue;
            if (!ReferenceEquals(marker.Device, device)) throw new InvalidOperationException("The glyph image retirement marker belongs to another device.");
            var status = marker.Poll();
            if (status == GraphicsGpuCompletionStatus.Pending) continue;
            if (status != GraphicsGpuCompletionStatus.Completed
                && !(status == GraphicsGpuCompletionStatus.DeviceUnavailable && device.IsPhysicallyUnavailable))
            { RetirementFailure = marker.Failure ?? "Native GPU completion for glyph images is unavailable."; continue; }
            foreach (var image in retirement.Images.ToArray())
            {
                if (image.Registered) { renderer.UnregisterTexture(image.Reference); image.Registered = false; }
                image.Texture.Dispose(); retirement.Images.Remove(image);
            }
            foreach (var previous in retirement.Markers.ToArray()) { previous.Dispose(); retirement.Markers.Remove(previous); }
            retirements.Remove(retirement);
        }
        if (retirements.Count == 0 && pending.Count == 0) RetirementFailure = null;
        CompleteDrain();
    }
    private void CompleteDrain() { if (draining && images.Count == 0 && pending.Count == 0 && retirements.Count == 0) drain.TrySetResult(); }
    private void CheckOwner()
    {
        if (Environment.CurrentManagedThreadId != ownerThread) throw new InvalidOperationException("Native glyph images belong to their graphics owner thread.");
        ObjectDisposedException.ThrowIf(disposed, this);
    }
    public void Dispose()
    {
        if (disposed) return; CheckOwner();
        if (!draining || !drain.Task.IsCompletedSuccessfully) throw new InvalidOperationException("Native glyph images require certified asynchronous retirement before disposal.");
        disposed = true;
    }
}
