using System.Diagnostics;
using System.Security.Cryptography;
using System.Xml.Linq;
using Stride.Core;
using Stride.Core.Assets;
using Stride.Core.Packages;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private sealed class NugetLibraryParityRun
    {
        internal required ProjectWorkspace Workspace;
        internal required EditorSessionHistory History;
        internal required SolutionProject Project;
        internal required PackageName Name;
        internal required string SourcePath;
        internal required string AssetsPath;
        internal required PackageContainer[] Members;
        internal required DependencyRange[] Direct;
        internal required Dictionary<string, byte[]> Before;
        internal required Dictionary<string, string> SourceHashes;
        internal required Guid[] Transactions;
        internal int Position;
        internal Guid? Transaction;
        internal PackageDependencyRestoreRun? Restore;
        internal Package? Library;
        internal bool Applied;
        internal bool Acquired;
        internal string? CleanupError;
        internal int CompensationRetryCount;
        internal long NextCompensationRetry;
        internal readonly Stopwatch Clock = Stopwatch.StartNew();
        internal readonly TaskCompletionSource<object> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private NugetLibraryParityRun? nugetLibraryParity;

    internal Task<object> StartP02NugetLibraryParity()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Stride-ImGui-UI")) + Path.DirectorySeparatorChar;
        if (nugetLibraryParity != null || !Environment.GetCommandLineArgs().Contains("--ui-test")
            || workspace == null || SessionAuthoringHistory == null
            || !Path.GetFullPath(workspace.BuildProjectPath).StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Native library proof requires an owned private loaded fixture.");
        var project = workspace.Projects.Single(candidate => string.Equals(candidate.FullPath.ToOSPath(),
            Path.ChangeExtension(workspace.BuildProjectPath, ".csproj"), StringComparison.OrdinalIgnoreCase));
        var name = NugetLibraryParityFixture.Name ?? throw new InvalidOperationException("Private real NuGet package was not configured before bootstrap.");
        if (workspace.Session.Packages.Any(package => string.Equals(package.Meta.Name, name.Id, StringComparison.OrdinalIgnoreCase))
            || project.DirectDependencies.Any(dependency => string.Equals(dependency.Name, name.Id, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Library adoption prerequisite failed: the genuine library is already present.");
        var fixtureRoot = NugetLibraryParityFixture.Root ?? throw new InvalidOperationException("Private native feed is missing.");
        // The package is deliberately not installed yet. Only the actual
        // AddSuggested/RestoreAsync path may author the reference and acquire it.
        var source = Path.Combine(fixtureRoot, "packages", name.Id.ToLowerInvariant(),
            name.Version.ToString().ToLowerInvariant(), "stride", name.Id + ".sdpkg");
        if (Directory.Exists(Path.GetDirectoryName(source)!))
            throw new InvalidOperationException("The unique native package was already installed before the acquisition proof.");
        var assets = ProjectDependencyRestoreArtifacts.Prepare(project.FullPath.ToOSPath()).AssetsPath;
        var run = new NugetLibraryParityRun
        {
            Workspace = workspace, History = SessionAuthoringHistory, Project = project, Name = name,
            SourcePath = Path.GetFullPath(source), AssetsPath = assets, Members = workspace.Session.Projects.ToArray(),
            Direct = project.DirectDependencies.ToArray(), Position = SessionAuthoringHistory.History.Position,
            Transactions = VerificationHistoryTransactions.ToArray(),
            Before = DependencyRestoreParityFixture.CaptureRestoreFiles(project.FullPath.ToOSPath(), assets),
            SourceHashes = HashP02Library(Path.Combine(fixtureRoot, "payload", "stride", name.Id + ".sdpkg"))
        };
        nugetLibraryParity = run;
        try
        {
            StartPackageDependencyRestore(project, service => service.AddSuggested(project, name));
            run.Restore = packageDependencyRestore ?? throw new InvalidOperationException("Actual restore owner missing.");
        }
        catch (Exception error) { run.Completion.TrySetException(error); }
        return run.Completion.Task;
    }

    internal void AdvanceP02NugetLibraryParity()
    {
        var run = nugetLibraryParity; if (run == null || run.Completion.Task.IsCompleted) return;
        try
        {
            if (run.Clock.Elapsed > TimeSpan.FromMinutes(4)) throw new TimeoutException("Actual native library restore timed out.");
            if (!ReferenceEquals(workspace, run.Workspace) || !ReferenceEquals(SessionAuthoringHistory, run.History))
                throw new InvalidOperationException("Actual library workspace/history changed.");
            if (IsPackageDependencyUpdating) return;
            if (run.Restore?.Published != true || run.Restore.Task?.IsCompletedSuccessfully != true)
                throw new InvalidOperationException("Actual NuGet restore/adoption failed.", run.Restore?.Task?.Exception);
            run.Applied = true; CertifyP02LibraryTransaction(run);
            var installedPath = PackageStore.Instance.GetPackageFileName(run.Name.Id,
                new PackageVersionRange(run.Name.Version, true))?.ToOSPath();
            if (!string.Equals(Path.GetFullPath(installedPath ?? ""), run.SourcePath, StringComparison.OrdinalIgnoreCase)
                || !File.Exists(run.SourcePath))
                throw new InvalidOperationException("Actual restore did not acquire the unique native package into its exact private cache.");
            run.Acquired = true;
            var library = run.Workspace.Session.Packages.Single(package => package.Meta.Name == run.Name.Id);
            run.Library = library;
            if (!library.IsReadOnly || library.State < PackageState.AssetsReady || library.Container is SolutionProject
                || !Equals(library.Meta.Version, run.Name.Version)
                || !library.Templates.Any(template => template.Id == NugetLibraryParityFixture.TemplateId
                    && template is Stride.Core.Assets.Templates.TemplateAssetFactoryDescription)
                || !library.Assets.Any(item => item.Id == NugetLibraryParityFixture.MaterialId
                    && item.Asset is Stride.Assets.Materials.MaterialAsset)
                || !run.Project.DirectDependencies.Any(dependency => dependency.Type == DependencyType.Package
                    && dependency.Name == run.Name.Id && dependency.VersionRange.Contains(run.Name.Version))
                || !run.Project.FlattenedDependencies.Any(dependency => ReferenceEquals(dependency.Package, library)))
                throw new InvalidOperationException("Real prepared library/templates/resolved native dependency were not adopted: "
                    + System.Text.Json.JsonSerializer.Serialize(new { readOnly = library.IsReadOnly, state = library.State.ToString(),
                        solutionProject = library.Container is SolutionProject, actualVersion = library.Meta.Version?.ToString(),
                        expectedVersion = run.Name.Version.ToString(), packagePath = library.FullPath.ToOSPath(),
                        templates = library.Templates.Select(template => new { template.Id, type = template.GetType().Name, path = template.FullPath?.ToOSPath() }),
                        assets = library.Assets.Select(item => new { item.Id, type = item.Asset.GetType().Name }),
                        expectedTemplate = NugetLibraryParityFixture.TemplateId, expectedMaterial = NugetLibraryParityFixture.MaterialId,
                        direct = run.Project.DirectDependencies.Select(dependency => new { dependency.Name, type = dependency.Type.ToString(), range = dependency.VersionRange?.ToString() }),
                        resolvedLibrary = run.Project.FlattenedDependencies.Any(dependency => ReferenceEquals(dependency.Package, library)) }));
            var after = DependencyRestoreParityFixture.CaptureRestoreFiles(run.Project.FullPath.ToOSPath(), run.AssetsPath);
            if (!after.ContainsKey(Path.GetFullPath(run.AssetsPath)) || DependencyRestoreParityFixture.SameFiles(run.Before, after)
                || !XDocument.Load(run.Project.FullPath.ToOSPath()).Descendants().Any(element => element.Name.LocalName == "PackageReference"
                    && (string?)element.Attribute("Include") == run.Name.Id))
                throw new InvalidOperationException("Actual package reference and NuGet output were not persisted.");
            var members = run.Workspace.Session.Projects.ToArray();
            RequireP02LibraryBoundary(run, false); run.History.Undo(); run.Applied = false;
            VerifyP02LibraryBaseline(run);
            RequireP02LibraryBoundary(run, true); run.History.Redo(); run.Applied = true;
            if (!run.Workspace.Session.Projects.SequenceEqual(members)
                || !run.Project.FlattenedDependencies.Any(dependency => ReferenceEquals(dependency.Package, library))
                || !DependencyRestoreParityFixture.SameFiles(after, DependencyRestoreParityFixture.CaptureRestoreFiles(run.Project.FullPath.ToOSPath(), run.AssetsPath)))
                throw new InvalidOperationException("Native Redo failed exact library identity/membership/restore files.");
            RequireP02LibraryBoundary(run, false); run.History.Undo(); run.Applied = false;
            VerifyP02LibraryBaseline(run);
            run.Completion.TrySetResult(new { realInstalledNativeLibrary = run.Name.Id, version = run.Name.Version.ToString(),
                actualNugetRestoreAndProductionAdoption = true, initiallyUninstalledLocalFeedAcquisition = true, templates = library.Templates.Count,
                nativeUndoRedoFilesAndLibraryIdentity = true, sourcePackageUnchanged = true,
                gaps = new[] { "Library version migration and mixed-dirty dependency replay are not covered." } });
        }
        catch (Exception error) { run.Completion.TrySetException(error); }
    }

    private void CertifyP02LibraryTransaction(NugetLibraryParityRun run)
    {
        var transaction = run.History.Stack.NextUndoTransaction?.Id;
        if (run.History.History.Position != run.Position + 1 || transaction == null
            || VerificationHistoryTransactions.Length != run.Transactions.Length + 1
            || !VerificationHistoryTransactions.Take(run.Transactions.Length).SequenceEqual(run.Transactions)
            || run.History.History.Entries.SingleOrDefault(entry => entry.Id == transaction)?.Name != "Add dependency to package '" + run.Project.Package.Meta.Name + "'")
            throw new InvalidOperationException("Actual native library transaction boundary changed.");
        run.Transaction = transaction;
    }
    private static void RequireP02LibraryBoundary(NugetLibraryParityRun run, bool redo)
    {
        if (run.Transaction == null || (redo ? run.History.Stack.NextRedoTransaction?.Id : run.History.Stack.NextUndoTransaction?.Id) != run.Transaction
            || run.History.History.Position != run.Position + (redo ? 0 : 1))
            throw new InvalidOperationException("Unrelated native history will not be replayed.");
    }
    private static Dictionary<string, string> HashP02Library(string source)
        => Directory.EnumerateFiles(Path.GetDirectoryName(source)!, "*", SearchOption.AllDirectories)
            .ToDictionary(file => Path.GetRelativePath(Path.GetDirectoryName(source)!, file),
                file => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))), StringComparer.OrdinalIgnoreCase);
    private static void VerifyP02LibraryBaseline(NugetLibraryParityRun run)
    {
        // Before acquisition fails there is no installed tree to preserve.
        // After real acquisition it must exactly match the immutable payload.
        var hashes = File.Exists(run.SourcePath) ? HashP02Library(run.SourcePath) : run.SourceHashes;
        var payloadHashes = HashP02Library(Path.Combine(NugetLibraryParityFixture.Root!, "payload", "stride", run.Name.Id + ".sdpkg"));
        if (!run.Workspace.Session.Projects.SequenceEqual(run.Members)
            || (run.Acquired && !File.Exists(run.SourcePath))
            || !DependencyRestoreParityFixture.SameDependencies(run.Direct, run.Project.DirectDependencies.ToArray())
            || !DependencyRestoreParityFixture.SameFiles(run.Before, DependencyRestoreParityFixture.CaptureRestoreFiles(run.Project.FullPath.ToOSPath(), run.AssetsPath))
            || hashes.Count != run.SourceHashes.Count || payloadHashes.Count != run.SourceHashes.Count
            || run.SourceHashes.Any(pair => !hashes.TryGetValue(pair.Key, out var hash) || hash != pair.Value
                || !payloadHashes.TryGetValue(pair.Key, out var payloadHash) || payloadHash != pair.Value))
            throw new InvalidOperationException("Library Undo/cleanup failed original native membership/files/source hashes.");
    }
    internal void CancelP02NugetLibraryParity()
    {
        var run = nugetLibraryParity; if (run == null) return;
        run.Completion.TrySetCanceled();
        if (ReferenceEquals(packageDependencyRestore, run.Restore)) run.Restore?.Cancellation.Cancel();
    }
    internal bool TryDrainP02NugetLibraryParity()
    {
        var run = nugetLibraryParity; if (run == null) return true;
        if (!run.Completion.Task.IsCompleted) return false;
        try
        {
            if (ReferenceEquals(packageDependencyRestore, run.Restore) && run.Restore != null)
            {
                run.Restore.Cancellation.Cancel();
                if (run.Restore.RestoreFailed)
                {
                    // This flag denotes a failed real compensation/lease release,
                    // not the already-certified scenario task failure. Re-enter
                    // the same recovery route as the existing Retry button; only
                    // the normal ownership-actor pump may release this run.
                    run.CleanupError = "Owned native compensation failed; recovery attempts="
                        + run.CompensationRetryCount + ": " + run.Restore.Log.ToText();
                    if (run.Restore.Task?.IsCompleted == true && run.CompensationRetryCount < 3
                        && run.Clock.ElapsedMilliseconds >= run.NextCompensationRetry)
                    {
                        run.CompensationRetryCount++;
                        run.NextCompensationRetry = run.Clock.ElapsedMilliseconds + 250;
                        run.Restore.RestoreFailed = false;
                    }
                }
                return false;
            }
            if (run.Restore?.Published == true && run.History.History.Position == run.Position + 1)
            { run.Applied = true; CertifyP02LibraryTransaction(run); }
            if (run.Applied) { RequireP02LibraryBoundary(run, false); run.History.Undo(); run.Applied = false; }
            VerifyP02LibraryBaseline(run);
            if (run.History.History.Position != run.Position
                || !VerificationHistoryTransactions.Take(run.Transactions.Length).SequenceEqual(run.Transactions))
                throw new InvalidOperationException("Owned native library cleanup did not preserve the original history boundary.");
            nugetLibraryParity = null; return true;
        }
        catch (Exception error) { run.CleanupError = error.ToString(); return false; }
    }
    internal string? P02NugetLibraryParityCleanupError => nugetLibraryParity?.CleanupError;
    internal int P02NugetLibraryParityCompensationRetryCount => nugetLibraryParity?.CompensationRetryCount ?? 0;
}
