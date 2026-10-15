using Stride.Core.IO;

namespace Stride.Core.Assets;

public sealed partial class Package
{
    /// <summary>Validates the native loaded/saved certificate before an editor moves its owned metadata file.</summary>
    public void ValidateOwnedMetadataRelocation(string sourcePath, byte[]? hash)
    {
        var source = Path.GetFullPath(sourcePath);
        if (metadataDiskBaselinePath == null || !string.Equals(source, metadataDiskBaselinePath, StringComparison.OrdinalIgnoreCase)
            || (metadataDiskBaselineHash == null) != (hash == null)
            || hash != null && !metadataDiskBaselineHash!.AsSpan().SequenceEqual(hash)
            || PreviousPackagePath != null && !string.Equals(Path.GetFullPath(PreviousPackagePath.ToOSPath()), source, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The package metadata relocation certificate is no longer current.");
    }
    /// <summary>Retargets the native certificate after the host moved those exact owned bytes, or a certified absent file.</summary>
    public void AcceptOwnedMetadataRelocation(string sourcePath, string destinationPath, byte[]? hash)
    {
        ValidateOwnedMetadataRelocation(sourcePath, hash);
        var target = Path.GetFullPath(destinationPath);
        if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(sourcePath)), Path.GetDirectoryName(target), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Package rename must retain the actual package directory.");
        RememberMetadataDiskBaseline(target, hash);
        PreviousPackagePath = new UFile(target);
    }
    /// <summary>Accepts an exact host-owned staged metadata publication after its native file journal succeeded.</summary>
    public void AcceptPreparedMetadataPublication(string preparedPath, string destinationPath, byte[]? hash)
    {
        ValidateOwnedMetadataRelocation(preparedPath, hash);
        var target = Path.GetFullPath(destinationPath);
        if (FullPath == null || !string.Equals(Path.GetFullPath(FullPath.ToOSPath()), target, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The prepared package has another publication destination.");
        RememberMetadataDiskBaseline(target, hash); PreviousPackagePath = new UFile(target);
    }

}
