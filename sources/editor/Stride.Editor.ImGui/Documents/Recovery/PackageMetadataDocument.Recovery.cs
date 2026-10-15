using System.Security.Cryptography;
using Stride.Core.Assets;
using Stride.Core.Transactions;
using Stride.Core.Yaml;

namespace Stride.Editor.ImGui;

internal sealed partial class PackageMetadataDocument
{
    private byte[] recoveryDiskBaseline = [];
    private bool restoredRecovery;
    private void RememberRecoveryDiskBaseline()
    {
        recoveryDiskBaseline = package.TryGetMetadataDiskBaseline(Path, out var loadedHash)
            ? loadedHash ?? []
            : File.Exists(Path) ? SHA256.HashData(File.ReadAllBytes(Path)) : [];
        restoredRecovery = false;
    }
    internal EditorRecoveryRecord CaptureRecovery()
        => new(Path, "package", null, null, Path, null, recoveryDiskBaseline.ToArray(), CaptureRecoveryMetadata(), [], null);
    private byte[] CaptureRecoveryMetadata()
    {
        using var stream = new MemoryStream(); AssetYamlSerializer.Default.Serialize(stream, package); return stream.ToArray();
    }
    internal void ValidateRecoveryDiskBaseline()
    {
        if (!restoredRecovery) return;
        var matches = recoveryDiskBaseline.Length == 0
            ? !File.Exists(Path) && !Directory.Exists(Path)
            : File.Exists(Path) && SHA256.HashData(File.ReadAllBytes(Path)).AsSpan().SequenceEqual(recoveryDiskBaseline);
        if (!matches)
            throw new IOException("The recovered package changed on disk. Reload or resolve the package before saving.");
    }
    internal void RestoreRecovery(EditorRecoveryRecord record)
    {
        if (!CanSave || IsDirty || !string.Equals(Path, record.Path, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("The current package metadata cannot be replaced by recovery.");
        Validate(record.Payload);
        var before = CaptureRecoveryMetadata(); var beforeRevision = revision; var afterRevision = ++nextRevision;
        var baseline = recoveryDiskBaseline.ToArray(); var wasRestored = restoredRecovery; var wasDirty = package.IsDirty;
        using var transaction = historyScope.CreateTransaction("Recover package metadata");
        try
        {
            ApplyRecoveryMetadata(record.Payload); revision = afterRevision; recoveryDiskBaseline = record.BaselineHash.ToArray(); restoredRecovery = true;
            historyScope.Record(new RecoveryPackageOperation(this, before, record.Payload.ToArray(), beforeRevision, afterRevision, baseline, record.BaselineHash.ToArray(), wasRestored, wasDirty));
        }
        catch { ApplyRecoveryMetadata(before); revision = beforeRevision; recoveryDiskBaseline = baseline; restoredRecovery = wasRestored; package.IsDirty = wasDirty; throw; }
    }
    private Package Validate(byte[] payload)
    {
        using var stream = new MemoryStream(payload, false);
        var restored = AssetYamlSerializer.Default.Deserialize(stream, typeof(Package)) as Package;
        if (restored == null) throw new InvalidDataException("The recovery package metadata could not be loaded.");
        foreach (var root in restored.RootAssets)
            if (workspace.Session.FindAsset(root.Id) == null) throw new InvalidOperationException("A recovered package root requires an asset that has not been restored.");
        return restored;
    }
    private void ApplyRecoveryMetadata(byte[] payload)
    {
        var restored = Validate(payload);
        package.SerializedVersion = restored.SerializedVersion; package.Meta = restored.Meta;
        package.AssetFolders = restored.AssetFolders; package.ResourceFolders = restored.ResourceFolders; package.OutputGroupDirectories = restored.OutputGroupDirectories;
        package.ExplicitFolders.Clear(); package.ExplicitFolders.AddRange(restored.ExplicitFolders);
        package.Bundles.Clear(); foreach (var bundle in restored.Bundles) package.Bundles.Add(bundle);
        package.TemplateFolders.Clear(); package.TemplateFolders.AddRange(restored.TemplateFolders);
        package.RootAssets.Clear(); foreach (var root in restored.RootAssets) package.RootAssets.Add(root);
        package.AssetAssemblies.Clear(); package.AssetAssemblies.AddRange(restored.AssetAssemblies);
        package.AssetNamespace = restored.AssetNamespace; package.IsDirty = true;
    }
    private sealed class RecoveryPackageOperation(PackageMetadataDocument owner, byte[] before, byte[] after,
        long beforeRevision, long afterRevision, byte[] beforeHash, byte[] afterHash, bool wasRestored, bool wasDirty) : Operation
    {
        protected override void Rollback()
        {
            owner.ApplyRecoveryMetadata(before); owner.revision = beforeRevision; owner.recoveryDiskBaseline = beforeHash.ToArray(); owner.restoredRecovery = wasRestored; owner.package.IsDirty = wasDirty;
        }
        protected override void Rollforward()
        {
            owner.ApplyRecoveryMetadata(after); owner.revision = afterRevision; owner.recoveryDiskBaseline = afterHash.ToArray(); owner.restoredRecovery = true;
        }
    }
}
