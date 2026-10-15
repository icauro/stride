namespace Stride.Editor.ImGui;

internal sealed partial class PackageMetadataDocument
{
    internal byte[]? PackageRenameDiskHash => recoveryDiskBaseline.Length == 0 ? null : recoveryDiskBaseline.ToArray();
    internal void RetargetOwnedPackageMetadata(long next)
    {
        revision = next;
        RememberRecoveryDiskBaseline();
        package.IsDirty = IsDirty;
    }
}
