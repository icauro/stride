namespace Stride.Editor.ImGui;

/// <summary>Native source hashes with read ownership held until actor publication finishes.</summary>
internal sealed class FactorySourceBaseline(AssetSourceImportResult hashes, List<FileStream> sources) : IDisposable
{
    private readonly SourceReadLeases readLeases = new(sources);
    internal AssetSourceImportResult Hashes { get; } = hashes;
    private readonly Dictionary<string, WindowsSourceFileIdentity.Identity> identities = OperatingSystem.IsWindows()
        ? sources.ToDictionary(source => source.Name, WindowsSourceFileIdentity.Capture, StringComparer.OrdinalIgnoreCase) : [];
    internal void Validate()
    {
        if (OperatingSystem.IsWindows()) foreach (var source in identities) WindowsSourceFileIdentity.Validate(source.Key, source.Value);
        else AssetSourceImport.ValidateSources(Hashes.SourceHashes);
    }
    internal static FactorySourceBaseline Prepare(string[] files, CancellationToken cancellation, Action<int, string>? progress = null)
    {
        var held = new List<FileStream>();
        try
        {
            foreach (var file in files)
            { cancellation.ThrowIfCancellationRequested(); held.Add(new(file, FileMode.Open, FileAccess.Read, FileShare.Read)); }
            var sha = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var native = new Dictionary<string, Stride.Core.Storage.ObjectId>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < files.Length; index++)
            {
                cancellation.ThrowIfCancellationRequested(); progress?.Invoke(index, files[index]);
                var captured = AssetSourceImport.Fingerprint(held[index], cancellation);
                sha.Add(files[index], captured.Sha); native.Add(files[index], captured.Object);
            }
            cancellation.ThrowIfCancellationRequested();
            return new(new([], sha, new Stride.Core.Diagnostics.LoggerResult("Asset import"), native), held);
        }
        catch (Exception failure)
        {
            var cleanup = new SourceReadLeases(held);
            try { cleanup.Dispose(); }
            catch (Exception disposal) { throw new FactorySourcePreparationFailure(failure, disposal, cleanup); }
            throw;
        }
    }
    public void Dispose() => readLeases.Dispose();
}


internal sealed class FactorySourcePreparationFailure(Exception preparation, Exception cleanup, SourceReadLeases sources)
    : AggregateException("Factory source preparation and read-handle cleanup failed.", preparation, cleanup)
{
    internal SourceReadLeases Sources { get; } = sources;
}
