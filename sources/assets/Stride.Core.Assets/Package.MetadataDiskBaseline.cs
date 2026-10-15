using System.Security.Cryptography;

namespace Stride.Core.Assets;

public sealed partial class Package
{
    private string? metadataDiskBaselinePath;
    private byte[]? metadataDiskBaselineHash;
    private readonly Dictionary<string, byte[]> obsoleteMetadataDiskBaselines = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Returns the disk certificate belonging to the bytes loaded or successfully saved by this package.</summary>
    public bool TryGetMetadataDiskBaseline(string path, out byte[]? hash)
    {
        hash = null;
        if (metadataDiskBaselinePath == null) return false;
        // Changing the destination does not establish ownership of a file which
        // happens to occupy it. A known package expects a new path to be absent.
        if (!string.Equals(Path.GetFullPath(path), metadataDiskBaselinePath, StringComparison.OrdinalIgnoreCase)) return true;
        hash = metadataDiskBaselineHash?.ToArray();
        return true;
    }

    internal void RememberMetadataDiskBaseline(string path, byte[]? hash)
    {
        metadataDiskBaselinePath = Path.GetFullPath(path);
        metadataDiskBaselineHash = hash?.ToArray();
    }

    internal void RememberSavedMetadata(string path, byte[] bytes)
    {
        var fullPath = Path.GetFullPath(path);
        obsoleteMetadataDiskBaselines.Remove(fullPath);
        if (metadataDiskBaselinePath != null && metadataDiskBaselineHash != null
            && !string.Equals(fullPath, metadataDiskBaselinePath, StringComparison.OrdinalIgnoreCase))
            obsoleteMetadataDiskBaselines[metadataDiskBaselinePath] = metadataDiskBaselineHash.ToArray();
        RememberMetadataDiskBaseline(fullPath, SHA256.HashData(bytes));
    }

    /// <summary>Returns the retained certificate of a successfully moved package's old file.</summary>
    public bool TryGetObsoleteMetadataDiskBaseline(string path, out byte[]? hash)
    {
        hash = null;
        if (!obsoleteMetadataDiskBaselines.TryGetValue(Path.GetFullPath(path), out var retained)) return false;
        hash = retained.ToArray();
        return true;
    }

    /// <summary>Releases an old-file certificate after its guarded removal succeeds.</summary>
    public void AcknowledgeObsoleteMetadataRemoval(string path)
        => obsoleteMetadataDiskBaselines.Remove(Path.GetFullPath(path));
}
