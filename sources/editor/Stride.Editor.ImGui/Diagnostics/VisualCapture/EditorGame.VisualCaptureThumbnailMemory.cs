using System.Collections;
using System.Reflection;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    // Requested endpoint metadata only. This does not scan the managed heap or
    // change cache residency, resource lifetime or compiler certificates.
    private object InspectVisualCaptureThumbnailMemory()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var snapshots = new HashSet<NativeThumbnailCompilationSnapshot>(ReferenceEqualityComparer.Instance);
        var sourceSnapshots = 0;
        foreach (var source in thumbnailSources.Values)
            if (source.Source.NativeCompilation is { } snapshot) { snapshots.Add(snapshot); sourceSnapshots++; }
        var requestCount = 0;
        var requestSnapshots = 0;
        var gpuCount = 0;
        object? workerLifecycle = null;
        if (assetThumbnails != null)
        {
            var type = assetThumbnails.GetType();
            workerLifecycle = ((NativeThumbnailResidentQueue)type.GetField("nativeQueue", flags)!.GetValue(assetThumbnails)!).InspectLifecycle();
            gpuCount = (int)type.GetField("gpuCount", flags)!.GetValue(assetThumbnails)!;
            var requests = (IDictionary)type.GetField("requests", flags)!.GetValue(assetThumbnails)!;
            requestCount = requests.Count;
            foreach (var request in requests.Values)
                if (request!.GetType().GetField("Source", flags)!.GetValue(request) is AssetThumbnailSource { NativeCompilation: { } snapshot })
                { snapshots.Add(snapshot); requestSnapshots++; }
        }
        var shaders = new HashSet<byte[]>(ReferenceEqualityComparer.Instance);
        var assemblies = new HashSet<NativeThumbnailAssemblyArtifact>(ReferenceEqualityComparer.Instance);
        var authored = new HashSet<byte[]>(ReferenceEqualityComparer.Instance);
        long shaderBytes = 0, assemblyBytes = 0, authoredBytes = 0;
        var shaderArrays = 0; var assemblyArrays = 0; var authoredArrays = 0;
        foreach (var snapshot in snapshots)
        {
            foreach (var source in snapshot.ShaderSources ?? [])
            { shaderArrays++; shaderBytes += source.Bytes.LongLength; shaders.Add(source.Bytes); }
            foreach (var source in snapshot.Assemblies)
            { assemblyArrays++; assemblyBytes += source.Artifact.Length; assemblies.Add(source.Artifact); }
            foreach (var source in snapshot.Authored.Assets)
            { authoredArrays++; authoredBytes += source.Bytes.LongLength; authored.Add(source.Bytes); }
        }
        return new
        {
            sourceCacheCount = thumbnailSources.Count, sourceCacheLimit = 2048,
            requestCount, gpuCount, gpuCacheLimit = AssetThumbnailService.MaximumGpuThumbnails,
            workerLifecycle,
            sourceSnapshotReferences = sourceSnapshots, requestSnapshotReferences = requestSnapshots,
            distinctSnapshotCount = snapshots.Count,
            pendingAssemblyArtifactCleanup = NativeThumbnailAssemblyArtifact.PendingCleanupCount,
            shaders = new { arrayReferences = shaderArrays, referencedBytes = shaderBytes,
                uniqueArrayCount = shaders.Count, uniqueArrayBytes = shaders.Sum(bytes => bytes.LongLength) },
            assemblies = new { arrayReferences = 0, referencedBytes = 0L,
                uniqueArrayCount = 0, uniqueArrayBytes = 0L,
                artifactReferences = assemblyArrays, uniqueArtifactCount = assemblies.Count,
                referencedArtifactFileBytes = assemblyBytes, uniqueArtifactFileBytes = assemblies.Sum(artifact => artifact.Length) },
            authoredAssets = new { arrayReferences = authoredArrays, referencedBytes = authoredBytes,
                uniqueArrayCount = authored.Count, uniqueArrayBytes = authored.Sum(bytes => bytes.LongLength) },
            boundary = "Actual source-cache and request-owned native thumbnail snapshots; shader/authored byte arrays and assembly artifacts deduplicated by object identity. Assembly payloads are private files, not retained managed byte arrays. Excludes certificate-only entries, workers, other caches, object overhead, native/GPU allocations and the rest of the managed heap. No cache is created or evicted."
        };
    }
}
