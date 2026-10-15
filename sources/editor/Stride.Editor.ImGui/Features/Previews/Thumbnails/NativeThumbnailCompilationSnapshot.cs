using Stride.Editor.Thumbnails;

namespace Stride.Editor.ImGui;

internal sealed class NativeThumbnailAssemblySnapshot : IDisposable
{
    internal NativeThumbnailAssemblyInput Certificate { get; }
    internal NativeThumbnailAssemblyArtifact Artifact { get; }
    private readonly IDisposable lease;
    internal NativeThumbnailAssemblySnapshot(NativeThumbnailAssemblyInput certificate, NativeThumbnailAssemblyArtifact artifact)
    { Certificate = certificate; Artifact = artifact; lease = artifact.AcquireLease(); }
    public void Dispose() => lease.Dispose();
}
internal sealed record NativeThumbnailShaderSnapshot(string RelativePath, byte[] Bytes, string Sha256);
internal sealed record NativeThumbnailCompilationSnapshot(MediaCompilationSnapshot Authored, string CompilerType,
    NativeThumbnailAssemblySnapshot[] Assemblies, IReadOnlyDictionary<string, string> SourceHashes, string Identity,
    NativeThumbnailShaderSnapshot[]? ShaderSources = null) : IDisposable
{
    internal IDisposable AcquireArtifactLease() => new NativeThumbnailArtifactLeaseSet(Assemblies);
    public void Dispose() => NativeThumbnailArtifactCleanup.DisposeAll(Assemblies);
}
