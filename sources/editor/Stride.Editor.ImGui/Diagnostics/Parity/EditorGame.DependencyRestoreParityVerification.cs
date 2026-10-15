using System.Diagnostics;
using System.Xml.Linq;
using Stride.Core.Assets;
using Stride.Core;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private sealed class DependencyRestoreParityRun(ProjectWorkspace workspace, EditorSessionHistory history, SolutionProject project)
    {
        internal readonly ProjectWorkspace Workspace = workspace;
        internal readonly EditorSessionHistory History = history;
        internal readonly SolutionProject Project = project;
        internal readonly Stopwatch Clock = Stopwatch.StartNew();
        internal readonly TaskCompletionSource<object> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal PackageContainer[] Members = [];
        internal Guid[] HistoryBefore = [], HistoryAfterAdoption = [];
        internal int BaselinePosition;
        internal Guid? AdoptionTransaction, RestoreTransaction;
        internal string DependencyPath = "", AssetsPath = "";
        internal SolutionProject? Dependency;
        internal PackageDependencyRestoreRun? Restore;
        internal Dictionary<string, byte[]> Before = [], After = [];
        internal DependencyRange[] Direct = [];
        internal int Phase;
        internal bool AdoptionApplied, RestoreApplied, Draining;
        internal string? CleanupError;
    }
    private DependencyRestoreParityRun? dependencyRestoreParity;

    internal string PrepareP02DependencySolutionFixture()
    {
        if (!Environment.GetCommandLineArgs().Contains("--ui-test") || workspace != null || projectLoading != null)
            throw new InvalidOperationException("Prepare the actual P02 solution before private project loading.");
        var input = Path.GetFullPath(projectPathInput);
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Stride-ImGui-UI")) + Path.DirectorySeparatorChar;
        if (!input.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The P02 solution may reference only the private cloned project.");
        var project = Path.ChangeExtension(input, ".csproj");
        if (!File.Exists(project)) throw new FileNotFoundException("The actual private SDK project is missing.", project);
        if (NugetLibraryParityFixture.Root != null) NugetLibraryParityFixture.Prepare(project);
        var path = Path.Combine(Path.GetDirectoryName(project)!, "P02DependencyProof.sln");
        if (File.Exists(path)) throw new IOException("P02 solution preparation requires a fresh private path.");
        var id = Guid.NewGuid();
        var solution = new Stride.Core.Solutions.Solution { FullPath = path, StartupProjectGuid = id };
        solution.Projects.Add(new Stride.Core.Solutions.Project(id, Stride.Core.Solutions.KnownProjectTypeGuid.CSharp,
            Path.GetFileNameWithoutExtension(project), project, Guid.Empty, [], [], []));
        solution.SaveAs(path);
        var reloaded = Stride.Core.Solutions.Solution.FromFile(path);
        if (reloaded.Projects.Count != 1 || !string.Equals(reloaded.Projects.Single().FullPath, project, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Native solution serialization did not preserve the actual SDK project binding.");
        projectPathInput = path;
        return path;
    }

    internal Task<object> StartP02DependencyRestoreParity()
        => StartP02DependencyRestoreParity(Path.Combine(Path.GetTempPath(), "Stride-ImGui-UI", "DependencyProof_" + Guid.NewGuid().ToString("N")));

    internal Task<object> StartP02DependencyRestoreParity(string ownedDirectory)
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Stride-ImGui-UI")) + Path.DirectorySeparatorChar;
        if (dependencyRestoreParity != null || !Environment.GetCommandLineArgs().Contains("--ui-test")
            || workspace == null || SessionAuthoringHistory == null
            || !Path.GetFullPath(workspace.BuildProjectPath).StartsWith(root, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFullPath(ownedDirectory).StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("P02 request/pump proof requires a fresh owned private loaded fixture.");
        var project = workspace.Projects.Single(candidate => string.Equals(candidate.FullPath.ToOSPath(),
            Path.ChangeExtension(workspace.BuildProjectPath, ".csproj"), StringComparison.OrdinalIgnoreCase));
        var run = new DependencyRestoreParityRun(workspace, SessionAuthoringHistory, project)
        {
            Members = workspace.Session.Projects.ToArray(), HistoryBefore = VerificationHistoryTransactions.ToArray(),
            BaselinePosition = SessionAuthoringHistory.History.Position,
            Direct = project.DirectDependencies.ToArray()
        };
        run.DependencyPath = DependencyRestoreParityFixture.Create(ownedDirectory, project.FullPath.ToOSPath());
        run.AssetsPath = ProjectDependencyRestoreArtifacts.Prepare(project.FullPath.ToOSPath()).AssetsPath;
        run.Before = DependencyRestoreParityFixture.CaptureRestoreFiles(project.FullPath.ToOSPath(), run.AssetsPath);
        dependencyRestoreParity = run;
        try { StartAddSolutionProject(run.DependencyPath); }
        catch (Exception error) { run.Completion.TrySetException(error); }
        return run.Completion.Task;
    }
    // Call once per ownership-actor frame. The production host pumps the actual
    // project preparation and restore dispatch; this method only orchestrates
    // requests and inspects their real published state.
    internal void AdvanceP02DependencyRestoreParity()
    {
        var run = dependencyRestoreParity;
        if (run == null || run.Completion.Task.IsCompleted) return;
        try
        {
            if (run.Clock.Elapsed > TimeSpan.FromMinutes(4)) throw new TimeoutException("Real native project/dependency restore did not complete.");
            if (!ReferenceEquals(workspace, run.Workspace) || !ReferenceEquals(SessionAuthoringHistory, run.History))
                throw new InvalidOperationException("Dependency proof lost its actual workspace/history owner.");
            if (run.Phase == 0)
            {
                if (IsAddingSolutionProjects) return;
                run.Dependency = workspace!.Projects.SingleOrDefault(project => string.Equals(project.FullPath.ToOSPath(), run.DependencyPath, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException("The normal project request/pump did not genuinely prepare and adopt the private SDK project.");
                run.AdoptionApplied = true;
                run.HistoryAfterAdoption = VerificationHistoryTransactions.ToArray();
                run.AdoptionTransaction = RequireP02PublishedTransaction(run, adoption: true);
                if (run.HistoryAfterAdoption.Length != run.HistoryBefore.Length + 1)
                    throw new InvalidOperationException("Native project adoption must publish one real history transaction.");
                if (!PackageDependencyService().GetCandidates(run.Project).Contains(run.Dependency.Package))
                    throw new InvalidOperationException("The actual loaded private project is not an eligible dependency.");
                StartPackageDependencyRestore(run.Project, service => service.AddLoadedDependency(run.Project, run.Dependency.Package));
                run.Restore = packageDependencyRestore ?? throw new InvalidOperationException("The actual restore request owner was not created.");
                run.Phase = 1; return;
            }
            if (run.Phase == 1)
            {
                if (IsPackageDependencyUpdating) return;
                if (run.Restore?.Published != true || run.Restore.Task?.IsCompletedSuccessfully != true)
                    throw new InvalidOperationException("The real restore producer/actor did not publish its native captures.", run.Restore?.Task?.Exception);
                run.RestoreApplied = true;
                run.RestoreTransaction = RequireP02PublishedTransaction(run, adoption: false);
                if (VerificationHistoryTransactions.Length != run.HistoryAfterAdoption.Length + 1
                    || !run.Project.DirectDependencies.Any(dependency => dependency.Type == DependencyType.Project
                        && string.Equals(dependency.MSBuildProject, run.DependencyPath, StringComparison.OrdinalIgnoreCase))
                    || !run.Project.FlattenedDependencies.Any(dependency => ReferenceEquals(dependency.Package, run.Dependency!.Package)))
                    throw new InvalidOperationException("Actual restore must publish the loaded dependency and one complete native journal.");
                run.After = DependencyRestoreParityFixture.CaptureRestoreFiles(run.Project.FullPath.ToOSPath(), run.AssetsPath);
                if (!run.After.ContainsKey(Path.GetFullPath(run.AssetsPath))
                    || DependencyRestoreParityFixture.SameFiles(run.Before, run.After)
                    || !XDocument.Load(run.Project.FullPath.ToOSPath()).Descendants().Any(element => element.Name.LocalName == "ProjectReference"))
                    throw new InvalidOperationException("Real MSBuild restore and authored ProjectReference must produce actual persisted output.");
                RequireP02HistoryBoundary(run, run.RestoreTransaction, run.BaselinePosition + 2, redo: false);
                run.History.Undo(); run.RestoreApplied = false;
                if (!DependencyRestoreParityFixture.SameDependencies(run.Direct, run.Project.DirectDependencies.ToArray())
                    || !DependencyRestoreParityFixture.SameFiles(run.Before,
                        DependencyRestoreParityFixture.CaptureRestoreFiles(run.Project.FullPath.ToOSPath(), run.AssetsPath)))
                    throw new InvalidOperationException("Native restore Undo did not restore exact authored and obj restore-file bytes.");
                RequireP02HistoryBoundary(run, run.RestoreTransaction, run.BaselinePosition + 1, redo: true);
                run.History.Redo(); run.RestoreApplied = true;
                if (!DependencyRestoreParityFixture.SameFiles(run.After,
                    DependencyRestoreParityFixture.CaptureRestoreFiles(run.Project.FullPath.ToOSPath(), run.AssetsPath))
                    || !run.Project.FlattenedDependencies.Any(dependency => ReferenceEquals(dependency.Package, run.Dependency!.Package)))
                    throw new InvalidOperationException("Native restore Redo did not restore actual persisted files/resolved membership.");
                RequireP02HistoryBoundary(run, run.RestoreTransaction, run.BaselinePosition + 2, redo: false);
                run.History.Undo(); run.RestoreApplied = false;
                RequireP02HistoryBoundary(run, run.AdoptionTransaction, run.BaselinePosition + 1, redo: false);
                run.History.Undo(); run.AdoptionApplied = false;
                if (!workspace!.Session.Projects.SequenceEqual(run.Members)
                    || run.History.History.Position != run.BaselinePosition
                    || !DependencyRestoreParityFixture.SameFiles(run.Before,
                        DependencyRestoreParityFixture.CaptureRestoreFiles(run.Project.FullPath.ToOSPath(), run.AssetsPath)))
                    throw new InvalidOperationException("Final native Undo did not restore the private session/file boundary.");
                run.Completion.TrySetResult(new { realSdkProjectPreparedAndAdopted = true, realProductionRestoreRequestPump = true,
                    actualRestoreTaskSucceeded = true, nativeUndoRedoFilesAndResolvedMembership = true,
                    restoredOriginalSessionMembership = true, restoreFiles = run.After.Keys.ToArray(),
                    gaps = new[] { "Native NuGet store-library adoption, protected library migration and mixed-dirty dependency replay remain unverified." } });
            }
        }
        catch (Exception error) { run.Completion.TrySetException(error); }
    }
    private static void RequireP02HistoryBoundary(DependencyRestoreParityRun run, Guid? transaction, int position, bool redo)
    {
        var actual = redo ? run.History.Stack.NextRedoTransaction?.Id : run.History.Stack.NextUndoTransaction?.Id;
        if (transaction == null || actual != transaction || run.History.History.Position != position)
            throw new InvalidOperationException("The proof's exact native transaction boundary changed; unrelated work will not be replayed.");
    }
    private Guid RequireP02PublishedTransaction(DependencyRestoreParityRun run, bool adoption)
    {
        var offset = adoption ? 1 : 2;
        var history = run.History.History;
        var transaction = run.History.Stack.NextUndoTransaction?.Id;
        var name = adoption ? "Import project 'NativeDependencyProof'" : "Add dependency to package '" + run.Project.Package.Meta.Name + "'";
        if (history.Position != run.BaselinePosition + offset || transaction == null
            || history.Entries.SingleOrDefault(entry => entry.Id == transaction)?.Name != name
            || !VerificationHistoryTransactions.Take(run.HistoryBefore.Length).SequenceEqual(run.HistoryBefore))
            throw new InvalidOperationException("The actual production transaction does not match the owned proof's exact history boundary.");
        return transaction.Value;
    }
    internal void CancelP02DependencyRestoreParity()
    {
        var run = dependencyRestoreParity; if (run == null) return;
        run.Draining = true; run.Completion.TrySetCanceled();
        addingProjectCancellation?.Cancel();
        if (ReferenceEquals(packageDependencyRestore, run.Restore)) run.Restore?.Cancellation.Cancel();
    }
    internal bool TryDrainP02DependencyRestoreParity()
    {
        var run = dependencyRestoreParity; if (run == null) return true;
        if (!run.Completion.Task.IsCompleted) return false;
        try
        {
            if (IsAddingSolutionProjects) { addingProjectCancellation?.Cancel(); return false; }
            if (packageDependencyRestore is { } restoring)
            {
                if (restoring.RestoreFailed)
                {
                    run.CleanupError = "Actual retained restore compensation failed: " + restoring.Log.ToText();
                    return false;
                }
                restoring.Cancellation.Cancel(); return false;
            }
            if (!run.AdoptionApplied && workspace?.Projects.Any(project => string.Equals(project.FullPath.ToOSPath(), run.DependencyPath, StringComparison.OrdinalIgnoreCase)) == true)
            {
                run.AdoptionApplied = true;
            }
            if (run.Restore?.Published == true && !run.RestoreApplied
                && run.Project.DirectDependencies.Any(dependency => string.Equals(dependency.MSBuildProject, run.DependencyPath, StringComparison.OrdinalIgnoreCase)))
            {
                run.RestoreApplied = true;
                run.RestoreTransaction ??= RequireP02PublishedTransaction(run, adoption: false);
            }
            if (run.RestoreApplied)
            {
                RequireP02HistoryBoundary(run, run.RestoreTransaction, run.BaselinePosition + 2, redo: false);
                run.History.Undo(); run.RestoreApplied = false;
            }
            if (run.AdoptionApplied)
            {
                run.AdoptionTransaction ??= RequireP02PublishedTransaction(run, adoption: true);
                RequireP02HistoryBoundary(run, run.AdoptionTransaction, run.BaselinePosition + 1, redo: false);
                run.History.Undo(); run.AdoptionApplied = false;
            }
            if (workspace != null && !workspace.Session.Projects.SequenceEqual(run.Members))
                throw new InvalidOperationException("The retained dependency proof cleanup did not restore actual membership.");
            dependencyRestoreParity = null; return true;
        }
        catch (Exception error) { run.CleanupError = error.ToString(); return false; }
    }
    internal string? P02DependencyRestoreParityCleanupError => dependencyRestoreParity?.CleanupError;
}
