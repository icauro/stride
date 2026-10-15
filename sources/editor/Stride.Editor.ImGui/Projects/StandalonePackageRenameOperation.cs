using Stride.Core.Assets;
using Stride.Core.IO;
using Stride.Core.Transactions;

namespace Stride.Editor.ImGui;

internal sealed class StandalonePackageRenameOperation : Operation
{
    private readonly ProjectWorkspace workspace;
    private readonly Package package;
    private readonly EditorSessionHistory history;
    private readonly EditorAuthoringState<PackageMetadataDocument> metadata;
    private readonly EditorAuthoringState<PackageSettingsDocument> settings;
    private readonly string before, after;
    private readonly long beforeRevision, afterRevision;
    private readonly Action<Package, string, string> refresh;
    private readonly Action<Action> retainRecovery;
    private bool? recoveryGoal;
    private readonly Action retryRecovery;
    private string metadataPath, userPath, baselinePath;
    private WindowsOwnedAssetFileRename? metadataFile, userFile;

    internal StandalonePackageRenameOperation(ProjectWorkspace workspace, Package package, EditorSessionHistory history,
        EditorAuthoringState<PackageMetadataDocument> metadata, EditorAuthoringState<PackageSettingsDocument> settings,
        string target, Action<Package, string, string> refresh, Action<Action> retainRecovery)
    {
        this.workspace = workspace; this.package = package; this.history = history;
        this.metadata = metadata; this.settings = settings; this.refresh = refresh; this.retainRecovery = retainRecovery;
        retryRecovery = RetryRecovery;
        before = System.IO.Path.GetFullPath(package.FullPath.ToOSPath()); after = System.IO.Path.GetFullPath(target);
        metadataPath = baselinePath = before; userPath = before + ".user";
        beforeRevision = metadata.Document.Revision; afterRevision = metadata.Document.AllocateRevision();
    }
    internal void ApplyInitial() => Change(true);
    protected override void Rollback() => Change(false);
    protected override void Rollforward() => Change(true);

    private void Change(bool forward)
    {
        try { Apply(forward); }
        catch (Exception failure)
        {
            if (history.IsCompensating) throw;
            try { Apply(!forward); }
            catch (Exception recovery)
            {
                if (!history.IsCapturingOperations)
                { recoveryGoal = !forward; retainRecovery(retryRecovery); }
                throw new AggregateException("Package rename and restoration failed.", failure, recovery);
            }
            throw;
        }
    }
    private void RetryRecovery()
    {
        if (recoveryGoal is not { } goal) return;
        Apply(goal); recoveryGoal = null;
    }
    private void Apply(bool forward)
    {
        var target = forward ? after : before;
        var targetUser = target + ".user";
        if (package.IsReadOnly || package.Container is SolutionProject || !workspace.Session.Packages.Contains(package)
            || !ReferenceEquals(package.Session, workspace.Session) || package.State < PackageState.AssetsReady
            || metadata.IsDisposed || settings.IsDisposed || !metadata.IsPublished || !settings.IsPublished
            || !ReferenceEquals(history.Registry.Find(metadata.CanonicalPath), metadata)
            || !ReferenceEquals(history.Registry.Find(settings.CanonicalPath), settings))
            throw new InvalidOperationException("The standalone package rename owner is no longer available.");
        var current = System.IO.Path.GetFullPath(package.FullPath.ToOSPath());
        if (current != before && current != after) throw new InvalidOperationException("The package path changed outside this rename.");
        ValidateTarget(target, metadata); ValidateTarget(targetUser, settings);
        var metadataHash = metadata.Document.PackageRenameDiskHash;
        var userHash = settings.Document.PackageRenameDiskHash;
        package.ValidateOwnedMetadataRelocation(baselinePath, metadataHash);
        using var saving = PackageUserSettings.SettingsContainer.BeginGuardedProfileSave(package.UserSettings.Profile);
        MoveFile(ref metadataFile, ref metadataPath, target, metadataHash);
        MoveFile(ref userFile, ref userPath, targetUser, userHash);
        if (baselinePath != target)
        {
            package.AcceptOwnedMetadataRelocation(baselinePath, target, metadataHash);
            baselinePath = target;
        }
        // Native copyAssets=false retains the same-directory source roots and actual assets.
        // Package/owner revision records the rename without fabricating asset changes.
        package.SetPackagePath(new UFile(target), copyAssets: false);
        history.Registry.Reindex(metadata); history.Registry.Reindex(settings);
        settings.Document.RetargetOwnedPackageSettings();
        metadata.Document.RetargetOwnedPackageMetadata(forward ? afterRevision : beforeRevision);
        ReleaseHandles();
        refresh(package, current, target);
    }
    private void ValidateTarget(string target, EditorAuthoringState owner)
    {
        if (history.Registry.Find(target) is { } occupied && !ReferenceEquals(occupied, owner))
            throw new IOException("The package rename destination has another canonical owner.");
    }
    private static void MoveFile(ref WindowsOwnedAssetFileRename? lease, ref string current, string target, byte[]? hash)
    {
        if (current == target) return;
        var caseOnly = string.Equals(current, target, StringComparison.OrdinalIgnoreCase);
        if (!caseOnly && (File.Exists(target) || Directory.Exists(target))) throw new IOException("A file already exists at the package rename destination.");
        if (hash == null)
        {
            if (lease != null || File.Exists(current) || Directory.Exists(current) || File.Exists(target) || Directory.Exists(target))
                throw new IOException("The unsaved package rename path is occupied.");
            current = target; return;
        }
        lease ??= new WindowsOwnedAssetFileRename(current, hash);
        try { lease.Move(target); }
        finally { current = lease.CurrentPath; }
    }
    private void ReleaseHandles()
    {
        if (metadataFile != null) { metadataFile.Dispose(); metadataFile = null; }
        if (userFile != null) { userFile.Dispose(); userFile = null; }
    }
    protected override void FreezeContent()
    {
        if (recoveryGoal != null || metadataFile != null || userFile != null) throw new InvalidOperationException("Finish the actual package rename compensation before retiring its file handles.");
    }
}
