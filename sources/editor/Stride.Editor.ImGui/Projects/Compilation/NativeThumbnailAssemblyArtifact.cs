using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Collections.Concurrent;
using Stride.Editor.Thumbnails;

namespace Stride.Editor.ImGui;

/// <summary>A private certified module image retained only by explicit snapshot/job leases.</summary>
internal sealed class NativeThumbnailAssemblyArtifact
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Stride-Thumbnail-Modules",
        Environment.ProcessId + "-" + Guid.NewGuid().ToString("N")));
    private const int MaximumPendingCleanup = 64;
    private static readonly ConcurrentDictionary<NativeThumbnailAssemblyArtifact, Lease> pendingCleanup = new();
    internal static int PendingCleanupCount => pendingCleanup.Count;
    internal static void EnsureCaptureCapacity()
    {
        PumpPendingCleanup(requireComplete: false);
        if (pendingCleanup.Count >= MaximumPendingCleanup)
            throw new IOException("Private thumbnail module cleanup must complete before capturing more modules.");
    }
    internal static void PumpPendingCleanup(bool requireComplete)
    {
        var failures = new List<Exception>();
        foreach (var lease in pendingCleanup.Values.ToArray())
            try { lease.Dispose(); } catch (Exception error) { failures.Add(error); }
        if (requireComplete && pendingCleanup.Count != 0)
            throw new AggregateException("Private thumbnail module cleanup remains pending.", failures);
    }
    private readonly object sync = new();
    private readonly string directory, path;
    private FileStream? locked;
    private int leases;
    private bool closing, deleted;
    internal long Length { get; }
    internal string InspectionPath => path;
    private NativeThumbnailAssemblyArtifact(string directory, string path)
    {
        this.directory = directory; this.path = path;
        locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Length = locked.Length;
    }
    internal static (NativeThumbnailAssemblyArtifact Artifact, NativeThumbnailAssemblyInput Certificate, IDisposable CaptureLease) Capture(
        string sourcePath, Assembly assembly, string? requiredSourceHash)
    {
        EnsureCaptureCapacity();
        var directory = Path.Combine(Root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "module.dll");
        NativeThumbnailAssemblyArtifact? artifact = null;
        IDisposable? captureLease = null;
        try
        {
            using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                source.CopyTo(output, 65536);
            artifact = new NativeThumbnailAssemblyArtifact(directory, path); captureLease = artifact.AcquireLease();
            using var image = File.OpenRead(path);
            var sha = Convert.ToHexString(SHA256.HashData(image)); image.Position = 0;
            using var pe = new PEReader(image);
            var metadata = pe.GetMetadataReader();
            var mvid = metadata.GetGuid(metadata.GetModuleDefinition().Mvid);
            if (mvid != assembly.ManifestModule.ModuleVersionId || AssemblyName.GetAssemblyName(path).FullName != assembly.FullName
                || requiredSourceHash != null && sha != requiredSourceHash)
                throw new InvalidDataException("The captured thumbnail module differs from its loaded canonical generation: " + assembly.FullName);
            return (artifact, new NativeThumbnailAssemblyInput { Path = sourcePath, Identity = assembly.FullName!,
                ModuleVersionId = mvid.ToString("D"), Sha256 = sha }, captureLease);
        }
        catch
        {
            if (captureLease != null) captureLease.Dispose();
            else if (File.Exists(path))
            {
                artifact = new NativeThumbnailAssemblyArtifact(directory, path);
                artifact.AcquireLease().Dispose();
            }
            else DeleteDirectory(directory);
            throw;
        }
    }
    internal bool IsAvailable { get { lock (sync) return !closing && !deleted; } }
    internal IDisposable AcquireLease()
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(closing || deleted, this);
            leases++; return new Lease(this);
        }
    }
    internal void CopyTo(string destination, NativeThumbnailAssemblyInput certificate)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(closing || deleted || leases == 0, this);
            Validate(certificate);
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
            input.CopyTo(output, 65536);
        }
    }
    internal void Validate(NativeThumbnailAssemblyInput certificate)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(closing || deleted || leases == 0, this);
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (Convert.ToHexString(SHA256.HashData(input)) != certificate.Sha256)
                throw new InvalidDataException("The private thumbnail module hash differs from its certificate.");
            input.Position = 0;
            using var image = new PEReader(input);
            var metadata = image.GetMetadataReader();
            if (metadata.GetGuid(metadata.GetModuleDefinition().Mvid).ToString("D") != certificate.ModuleVersionId
                || AssemblyName.GetAssemblyName(path).FullName != certificate.Identity)
                throw new InvalidDataException("The private thumbnail module identity differs from its certificate.");
        }
    }
    private void Release(Lease lease)
    {
        lock (sync)
        {
            if (lease.Owner == null) return;
            if (leases <= 0) throw new InvalidOperationException("The module artifact lease has already been released.");
            if (leases == 1)
            {
                closing = true;
                locked?.Dispose(); locked = null;
                // Failed deletion retains this exact final lease for retry.
                try { DeleteDirectory(directory); deleted = true; pendingCleanup.TryRemove(this, out _); }
                catch { pendingCleanup.TryAdd(this, lease); throw; }
            }
            leases--; lease.Owner = null;
        }
    }
    private static void DeleteDirectory(string directory)
    {
        var absolute = Path.GetFullPath(directory);
        if (!string.Equals(Path.GetDirectoryName(absolute), Root, StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParseExact(Path.GetFileName(absolute), "N", out _))
            throw new InvalidOperationException("The thumbnail module directory is not privately owned.");
        if (Directory.Exists(absolute)) Directory.Delete(absolute, true);
    }
    private sealed class Lease(NativeThumbnailAssemblyArtifact owner) : IDisposable
    {
        internal NativeThumbnailAssemblyArtifact? Owner = owner;
        public void Dispose() => Volatile.Read(ref Owner)?.Release(this);
    }
}

internal sealed class NativeThumbnailArtifactLeaseSet : IDisposable
{
    private readonly List<IDisposable> leases;
    internal NativeThumbnailArtifactLeaseSet(IEnumerable<NativeThumbnailAssemblySnapshot> modules)
    {
        leases = [];
        try { foreach (var module in modules) leases.Add(module.Artifact.AcquireLease()); }
        catch { Dispose(); throw; }
    }
    public void Dispose()
    {
        var failures = new List<Exception>();
        for (var index = leases.Count - 1; index >= 0; index--)
        {
            try { leases[index].Dispose(); leases.RemoveAt(index); }
            catch (Exception error) { failures.Add(error); }
        }
        if (failures.Count != 0) throw new AggregateException("Thumbnail module lease cleanup failed.", failures);
    }
}

internal static class NativeThumbnailArtifactCleanup
{
    internal static void DisposeAll(IEnumerable<IDisposable> owners)
    {
        var failures = new List<Exception>();
        foreach (var owner in owners)
            try { owner.Dispose(); } catch (Exception error) { failures.Add(error); }
        if (failures.Count != 0) throw new AggregateException("Thumbnail ownership cleanup failed.", failures);
    }
}
