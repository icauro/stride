using Stride.Core.Assets;
using Stride.Core.Transactions;

namespace Stride.Editor.ImGui;

internal sealed partial class PackageSettingsDocument
{
    private long savedFileGeneration;
    internal EditorRecoveryRecord CaptureRecovery()
    {
        if (!IsDirty || !CanSave) throw new InvalidOperationException("Package user settings have no editable changes to recover.");
        return new(Path, "package-settings", null, null, package.FullPath.ToOSPath(), null,
            savedDiskHash?.ToArray() ?? [], PackageUserSettings.SettingsContainer.SerializeSettingsProfile(package.UserSettings.Profile), [], null);
    }
    internal void RestoreRecovery(EditorRecoveryRecord record)
    {
        if (!CanSave || IsDirty || record.Kind != "package-settings" || record.AssetId != null
            || !SameRecoveryPath(Path, record.Path) || record.PackagePath == null
            || !SameRecoveryPath(package.FullPath.ToOSPath(), record.PackagePath))
            throw new InvalidOperationException("Recovery belongs to another package user-settings owner or a dirty/read-only profile.");
        var container = PackageUserSettings.SettingsContainer;
        container.ValidateSettingsProfileSnapshot(record.Payload);
        var before = container.SerializeSettingsProfile(package.UserSettings.Profile);
        var after = record.Payload.ToArray();
        var beforeRevision = revision; var afterRevision = ++nextRevision;
        var beforeHash = savedDiskHash?.ToArray(); var afterHash = record.BaselineHash.Length == 0 ? null : record.BaselineHash.ToArray();
        var saveGeneration = savedFileGeneration;
        using var transaction = history.CreateTransaction("Recover package user settings");
        try
        {
            ApplyRecoverySettings(after, afterRevision, afterHash, saveGeneration);
            history.Record(new RecoverySettingsOperation(this, before, after, beforeRevision, afterRevision,
                beforeHash, afterHash, saveGeneration));
        }
        catch { ApplyRecoverySettings(before, beforeRevision, beforeHash, saveGeneration); throw; }
    }
    private static bool SameRecoveryPath(string first, string second)
        => string.Equals(System.IO.Path.GetFullPath(first), System.IO.Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase);
    private void ApplyRecoverySettings(byte[] bytes, long nextRevisionValue, byte[]? baseline, long saveGeneration)
    {
        PackageUserSettings.SettingsContainer.RestoreSettingsProfileSnapshot(package.UserSettings.Profile, bytes);
        revision = nextRevisionValue; authoringStamp++;
        // A subsequent actual Save supersedes the replay's old disk baseline.
        if (savedFileGeneration == saveGeneration) savedDiskHash = baseline?.ToArray();
    }
    private sealed class RecoverySettingsOperation(PackageSettingsDocument owner, byte[] before, byte[] after,
        long beforeRevision, long afterRevision, byte[]? beforeHash, byte[]? afterHash, long saveGeneration) : Operation, ISettingsHistoryOwnership
    {
        public bool Owns(Package target, Stride.Core.Settings.SettingsKey key) => ReferenceEquals(owner.package, target);
        protected override void Rollback() => owner.ApplyRecoverySettings(before, beforeRevision, beforeHash, saveGeneration);
        protected override void Rollforward() => owner.ApplyRecoverySettings(after, afterRevision, afterHash, saveGeneration);
    }
}
