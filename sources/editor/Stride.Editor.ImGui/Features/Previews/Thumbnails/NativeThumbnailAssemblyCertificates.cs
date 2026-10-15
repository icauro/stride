using System.Reflection;
using System.Runtime.CompilerServices;
using Stride.Editor.Thumbnails;

namespace Stride.Editor.ImGui;

/// <summary>Share certified private artifacts by actual loaded module, without rooting collectible assemblies.</summary>
internal static class NativeThumbnailAssemblyCertificates
{
    private sealed class Entry
    {
        internal long Length, Modified;
        internal WeakReference<NativeThumbnailAssemblyArtifact>? Artifact;
        internal NativeThumbnailAssemblyInput? Certificate;
    }
    private static readonly ConditionalWeakTable<Assembly, Entry> Entries = new();

    internal static NativeThumbnailAssemblySnapshot Capture(Assembly assembly)
    {
        NativeThumbnailAssemblyArtifact.EnsureCaptureCapacity();
        var source = Stride.Core.Reflection.AssemblyContainer.FindLoadedSource(assembly);
        var path = string.IsNullOrEmpty(assembly.Location) ? source?.Path : assembly.Location;
        if (assembly.IsDynamic || string.IsNullOrEmpty(path)
            || string.IsNullOrEmpty(assembly.Location) && string.IsNullOrEmpty(source?.SourceSha256))
            throw new InvalidDataException("The native thumbnail factory requires a physical certified assembly: " + assembly.FullName);
        using var sourceLease = source?.AcquireArtifactLifetime();
        var entry = Entries.GetValue(assembly, _ => new());
        lock (entry)
        {
            var info = new FileInfo(path);
            if (!info.Exists) throw new FileNotFoundException("The canonical thumbnail module is missing.", path);
            if (entry.Certificate != null && entry.Artifact?.TryGetTarget(out var existing) == true
                && existing.IsAvailable && entry.Length == info.Length && entry.Modified == info.LastWriteTimeUtc.Ticks)
            {
                try { return new(entry.Certificate, existing); }
                catch (ObjectDisposedException) { } // Last job lease ended after weak lookup; recapture actual source.
            }
            var length = info.Length; var modified = info.LastWriteTimeUtc.Ticks;
            var captured = NativeThumbnailAssemblyArtifact.Capture(path, assembly,
                string.IsNullOrEmpty(assembly.Location) ? source!.SourceSha256 : null);
            using var captureLease = captured.CaptureLease;
            var snapshot = new NativeThumbnailAssemblySnapshot(captured.Certificate, captured.Artifact);
            try
            {
                info.Refresh();
                if (!info.Exists || length != info.Length || modified != info.LastWriteTimeUtc.Ticks)
                    throw new InvalidDataException("The thumbnail source file changed during canonical capture: " + assembly.FullName);
                entry.Certificate = captured.Certificate; entry.Artifact = new(captured.Artifact);
                entry.Length = length; entry.Modified = modified;
                return snapshot;
            }
            catch { snapshot.Dispose(); throw; }
        }
    }
}
