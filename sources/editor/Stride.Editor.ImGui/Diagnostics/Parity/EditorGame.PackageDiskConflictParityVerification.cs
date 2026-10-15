using System.Security.Cryptography;
using Stride.Core.Assets;
using Stride.Core.Solutions;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private PackageDiskConflictParityRun? packageDiskConflictParityOwner;
    internal sealed class PackageDiskConflictParityRun
    {
        internal required ProjectWorkspace Workspace;
        internal required EditorSessionHistory History;
        internal required Package Package;
        internal required PackageMetadataDocument Metadata;
        internal required IDisposable Lease;
        internal required byte[] OriginalPackage;
        internal required Dictionary<string, byte[]> OtherFiles;
        internal required string HistoryReceipt, SolutionPath, EditedDescription;
        internal string? OriginalDescription;
        internal required Package OriginalPersistedPackage;
        internal object? ReloadReceipt;
        internal long Revision;
        internal byte[]? ConflictBytes;
        internal string? PreparedSha256;
        internal string? RejectedFailure;
        internal bool NativePackageReloadVerified, NativeSolutionReloadVerified;
        internal Exception? Failure;
        internal bool PreparedObserved, Continued, ArmConflict = true, CleanupStarted;
        internal int Stage;
        internal object? Evidence;
        internal object? PreparationReceipt;
    }

    internal PackageDiskConflictParityRun BeginPackageDiskConflictParityVerification()
    {
        if (!args.Contains("--ui-test") || workspace?.Package is not { IsReadOnly: false } package || sessionHistory == null
            || IsAuthoringPipelineUpdating || documents.HasDirtyDocuments || workspace.HasPackageChanges || packageDiskConflictParityOwner != null)
            throw new InvalidOperationException("Package conflict requires settled clean private native owners.");
        var path = package.FullPath.ToOSPath();
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Stride-ImGui-UI")) + Path.DirectorySeparatorChar;
        var solutionPath = workspace.Session.SolutionPath?.ToOSPath();
        if (!Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("An actual private .sdpkg is mandatory.");
        var state = sessionHistory.Registry.GetOrCreate(path, null, () => new PackageMetadataDocument(workspace, package, sessionHistory));
        var directory = Path.GetDirectoryName(path)!;
        var files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Where(file => !SamePath(file, path) && (file.EndsWith(".sdpkg", StringComparison.OrdinalIgnoreCase)
                || file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
                || file.StartsWith(Path.Combine(directory, "Assets") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            .ToDictionary(file => file, File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);
        var run = new PackageDiskConflictParityRun { Workspace = workspace, History = sessionHistory, Package = package,
            Metadata = state.Document, Lease = state.AcquireView(), OriginalPackage = File.ReadAllBytes(path), OtherFiles = files,
            HistoryReceipt = CaptureRecoveryAcceptanceHistory(), SolutionPath = solutionPath ?? "", Revision = state.Document.Revision,
            OriginalPersistedPackage = AssetFileSerializer.Load<Package>(path).Asset,
            OriginalDescription = package.Meta.Description, EditedDescription = "Native package conflict " + Guid.NewGuid().ToString("N") };
        packageDiskConflictParityOwner = run;
        // Genuine native metadata fixture arrangement, not a UI authoring gesture.
        package.Meta.Description = run.EditedDescription; package.IsDirty = true;
        RequestPackageMetadataSave(() => run.Continued = true, error => run.Failure = error);
        return run;
    }

    private void ObservePackageDiskConflictPreparation(PreparedPackageMetadataSave prepared)
    {
        var run = packageDiskConflictParityOwner;
        if (run == null || !run.ArmConflict) return;
        var actualBytes = File.ReadAllBytes(run.Metadata.Path);
        run.PreparationReceipt = new
        {
            writes = prepared.Writes.Select(write => new { write.PackagePath, length = write.Bytes.Length,
                sha256 = Convert.ToHexString(SHA256.HashData(write.Bytes.Span)) }).ToArray(),
            removals = prepared.Removals.Select(removal => new { removal.PackagePath, removal.FilePath, removal.Kind }).ToArray(),
            projectMembership = prepared.ProjectMembership.Select(member => new { member.ProjectPath, member.AssetPath,
                assetId = member.AssetId.ToString(), member.Adding }).ToArray(),
            originalPackageSha256 = Convert.ToHexString(SHA256.HashData(run.OriginalPackage)),
            actualPackageSha256 = Convert.ToHexString(SHA256.HashData(actualBytes)),
            revision = run.Metadata.Revision, expectedRevision = run.Revision,
            canonicalOwnerSame = ReferenceEquals(run.History.Registry.Find(run.Metadata.Path)?.Document, run.Metadata),
        };
        var preparationJson = System.Text.Json.JsonSerializer.Serialize(run.PreparationReceipt);
        var artifacts = Environment.GetEnvironmentVariable("STRIDE_EDITOR_TEST_ARTIFACTS")
            ?? throw new InvalidOperationException("Native preparation evidence requires the owned artifact directory.");
        File.WriteAllText(Path.Combine(artifacts, "package-conflict-preparation.json"), preparationJson);
        if (packageMetadataSave == null || !ReferenceEquals(packageMetadataSave.Workspace, run.Workspace)
            || !ReferenceEquals(packageMetadataSave.History, run.History)) throw new InvalidOperationException("The prepared save has another native owner. " + preparationJson);
        if (prepared.Writes.Count != 1 || prepared.Removals.Count != 0
            || !SamePath(prepared.Writes[0].PackagePath, run.Metadata.Path) || !actualBytes.SequenceEqual(run.OriginalPackage))
            throw new InvalidOperationException("Conflict fixture requires one real prepared native package write and unchanged baseline. " + preparationJson);
        // Native save preparation always requests membership for IProjectAsset,
        // including ordinary SDK sources already included in the project. Only
        // these genuine idempotent requests are permitted in this fixture.
        foreach (var membership in prepared.ProjectMembership)
        {
            var asset = run.Workspace.Session.FindAsset(membership.AssetId);
            var projectState = run.History.Registry.Find(membership.ProjectPath);
            if (!membership.Adding || asset?.Asset is not SourceCodeAsset || asset.Asset is IProjectFileGeneratorAsset
                || asset.Package.Container is not SolutionProject project || !SamePath(project.FullPath.ToOSPath(), membership.ProjectPath)
                || !SamePath(asset.FullPath.ToOSPath(), membership.AssetPath)
                || projectState is not { IsPublished: true, IsDisposed: false, Document: ProjectBuildSettingsDocument projectOwner }
                || projectOwner.IsDirty)
                throw new InvalidOperationException("Only actual already-authored ordinary native SDK source membership is valid. " + preparationJson);
            projectOwner.ValidateProjectFileBaseline();
            projectOwner.ValidateProjectAssetMembership(asset, true);
        }
        var write = prepared.Writes[0];
        run.ArmConflict = false;
        run.PreparedObserved = true;
        run.PreparedSha256 = Convert.ToHexString(SHA256.HashData(write.Bytes.Span));
        run.ConflictBytes = run.OriginalPackage.Concat(System.Text.Encoding.UTF8.GetBytes("\n# controlled post-preparation package conflict\n")).ToArray();
        File.WriteAllBytes(run.Metadata.Path, run.ConflictBytes);
    }

    internal bool StepPackageDiskConflictParityVerification(PackageDiskConflictParityRun run)
    {
        if (IsPackageMetadataSaving) return false;
        AssertPackageDiskConflictOwners(run);
        if (run.Stage == 0)
        {
            if (!run.PreparedObserved || run.Failure is not IOException || run.Continued || !run.Package.IsDirty || !run.Metadata.IsDirty
                || run.Metadata.Revision != run.Revision || !File.ReadAllBytes(run.Metadata.Path).SequenceEqual(run.ConflictBytes!))
                throw new InvalidOperationException("Actual native package save must refuse post-preparation disk conflict without publication, acknowledgment or dirty-owner loss.", run.Failure);
            var rejected = run.Failure.ToString();
            run.RejectedFailure = rejected;
            RestoreOnlyControlledPackageConflict(run);
            run.Evidence = new { actualPreparedNativeWriteSha256 = run.PreparedSha256, actualDiskConflict = rejected,
                rejectedContinuation = true, canonicalRevisionHistoryAndOtherFilesPreserved = true,
                scope = "Actual detached native package preparation then actor-owned write refusal; native metadata fixture arrangement, no UI edit or whole-save atomicity claim." };
            run.Failure = null; run.Stage = 1;
            RequestPackageMetadataSave(() => run.Continued = true, error => run.Failure = error);
            return false;
        }
        if (run.Failure != null || !run.Continued || run.Package.IsDirty || run.Metadata.IsDirty)
            throw new InvalidOperationException("Actual package retry/cleanup failed to publish and acknowledge the canonical owner.", run.Failure);
        if (run.Stage == 1)
        {
            var loaded = AssetFileSerializer.Load<Package>(run.Metadata.Path).Asset;
            var actualSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(run.Metadata.Path)));
            run.ReloadReceipt = new
            {
                expected = new { run.OriginalPersistedPackage.Meta.Name,
                    version = run.OriginalPersistedPackage.Meta.Version?.ToString(), description = run.EditedDescription },
                reloaded = new { loaded.Meta.Name, version = loaded.Meta.Version?.ToString(), loaded.Meta.Description },
                runtimeSessionName = run.Package.Meta.Name,
                preparedSha256 = run.PreparedSha256, actualSha256,
            };
            var reloadJson = System.Text.Json.JsonSerializer.Serialize(run.ReloadReceipt);
            File.WriteAllText(Path.Combine(Environment.GetEnvironmentVariable("STRIDE_EDITOR_TEST_ARTIFACTS")
                ?? throw new InvalidOperationException("Native reload evidence requires owned artifacts."), "package-conflict-reload.json"), reloadJson);
            // The native session uses a csproj-derived name; the serializer keeps
            // the independently captured authored package identity on disk.
            if (loaded.Meta.Name != run.OriginalPersistedPackage.Meta.Name
                || !Equals(loaded.Meta.Version, run.OriginalPersistedPackage.Meta.Version) || actualSha256 != run.PreparedSha256
                || loaded.Meta.Description != run.EditedDescription)
                throw new InvalidDataException("Independent native package reload did not retain actual successful publication. " + reloadJson);
            run.NativePackageReloadVerified = true;
            if (!string.IsNullOrEmpty(run.SolutionPath) && File.Exists(run.SolutionPath))
            {
                var solution = Solution.FromFile(run.SolutionPath);
                var nativeProjects = run.Workspace.Session.Projects.OfType<SolutionProject>().Select(project => project.VSProject.Guid).ToArray();
                if (nativeProjects.Length == 0 || nativeProjects.Any(id => solution.Projects.FindByGuid(id) == null))
                    throw new InvalidDataException("Independent native solution reload did not retain project identity.");
                run.NativeSolutionReloadVerified = true;
            }
            run.Package.Meta.Description = run.OriginalDescription; run.Package.IsDirty = true;
            run.Stage = 2; run.Continued = false;
            RequestPackageMetadataSave(() => run.Continued = true, error => run.Failure = error);
            return false;
        }
        if (!File.ReadAllBytes(run.Metadata.Path).SequenceEqual(run.OriginalPackage))
            throw new InvalidOperationException("Normal native metadata cleanup did not restore exact original package bytes.");
        run.Evidence = new { actualPreparedNativeWriteSha256 = run.PreparedSha256, actualDiskConflict = run.RejectedFailure,
            preparation = run.PreparationReceipt,
            reload = run.ReloadReceipt,
            rejectedContinuation = true, canonicalRevisionHistoryAndOtherFilesPreserved = true,
            actualNormalRetryAcknowledged = true, independentNativePackageReload = run.NativePackageReloadVerified,
            independentNativeSolutionReload = run.NativeSolutionReloadVerified,
            originalPackageBytesRestoredByNativeSave = true,
            scope = "Actual detached native package preparation and guarded write conflict/refusal/retry on a loaded private canonical owner; metadata mutation is fixture arrangement, not UI input/history. Solution reload is certified only when an actual .sln exists. No whole-save atomicity claim." };
        run.Lease.Dispose(); packageDiskConflictParityOwner = null;
        return true;
    }

    private void AssertPackageDiskConflictOwners(PackageDiskConflictParityRun run)
    {
        if (!ReferenceEquals(workspace, run.Workspace) || !ReferenceEquals(sessionHistory, run.History)
            || !ReferenceEquals(run.History.Registry.Find(run.Metadata.Path)?.Document, run.Metadata)
            || CaptureRecoveryAcceptanceHistory() != run.HistoryReceipt
            || run.OtherFiles.Any(file => !File.ReadAllBytes(file.Key).SequenceEqual(file.Value)))
            throw new InvalidOperationException("Package conflict changed native owner/history or unrelated project/solution/asset bytes.");
    }
    private void RestoreOnlyControlledPackageConflict(PackageDiskConflictParityRun run)
    {
        if (run.ConflictBytes == null) return;
        if (!File.ReadAllBytes(run.Metadata.Path).SequenceEqual(run.ConflictBytes))
            throw new IOException("Refuse to overwrite package bytes changed after the controlled conflict.");
        File.WriteAllBytes(run.Metadata.Path, run.OriginalPackage); run.ConflictBytes = null;
    }

    internal bool TryDrainPackageDiskConflictParityVerification()
    {
        var run = packageDiskConflictParityOwner;
        if (run == null) return true;
        if (packageMetadataSave is { } saving)
        {
            if (!ReferenceEquals(saving.Workspace, run.Workspace) || !ReferenceEquals(saving.History, run.History))
                throw new InvalidOperationException("Another native producer owns the package cleanup barrier.");
            if (!run.CleanupStarted && !saving.Published) saving.CancelPublication = true;
            return false;
        }
        AssertPackageDiskConflictOwners(run);
        RestoreOnlyControlledPackageConflict(run);
        if (!run.CleanupStarted)
        {
            run.ArmConflict = false; run.CleanupStarted = true; run.Continued = false; run.Failure = null;
            run.Package.Meta.Description = run.OriginalDescription; run.Package.IsDirty = true;
            RequestPackageMetadataSave(() => run.Continued = true, error => run.Failure = error);
            return false;
        }
        if (run.Failure != null) throw new InvalidOperationException("Actual package cleanup failed; its exact owner remains retained.", run.Failure);
        if (!run.Continued) return false;
        if (run.Package.IsDirty || run.Metadata.IsDirty || !File.ReadAllBytes(run.Metadata.Path).SequenceEqual(run.OriginalPackage))
            throw new InvalidOperationException("Package cleanup did not recover native original bytes/clean ownership.");
        run.Lease.Dispose(); packageDiskConflictParityOwner = null;
        return true;
    }
}
