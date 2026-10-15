using Stride.Core.Assets;
using Stride.Core.Diagnostics;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private bool IsProjectRemovalDependencyChecking => projectLocationRemoval?.DependencyTask != null;
    private sealed record ProjectRemovalDependency(SolutionProject Project, DependencyRange[] Removing,
        PackageDependencies Service, string Configuration, DependencyRemovalContext[] Contexts);
    private sealed record ProjectRemovalDependencyResult(ProjectRemovalDependency Check, ScriptDependencyUsageResult[] Results);
    private sealed partial class ProjectLocationRemoval
    {
        internal ProjectRemovalDependency[] DependencyChecks = [];
        internal (StandalonePackage Package, DependencyRange[] Removing)[] StandaloneDependencyChecks = [];
        internal Task<ProjectRemovalDependencyResult[]>? DependencyTask;
        internal CancellationTokenSource? DependencyCancellation;
        internal ProjectRemovalDependencyResult[] DependencyResults = [];
        private readonly HashSet<DependencyRemovalContext> releasedDependencyContexts = [];
        internal void ReleaseDependencyChecks()
        {
            if (DependencyTask is { IsCompleted: false }) throw new InvalidOperationException("Dependency source readers have not retired.");
            foreach (var check in DependencyChecks)
                foreach (var context in check.Contexts)
                    if (!releasedDependencyContexts.Contains(context))
                    { context.Semantics.Dispose(); releasedDependencyContexts.Add(context); }
            DependencyCancellation?.Dispose(); DependencyCancellation = null; DependencyTask = null;
        }
        internal void ReleaseAll() { ReleaseDependencyChecks(); Release(); }
    }

    private void PrepareProjectRemovalDependencyChecks(ProjectLocationRemoval run)
    {
        try { PrepareProjectRemovalDependencyChecks(run, () => CurrentProjectLocationRemoval(run), () => CommitProjectLocationRemoval(run)); }
        catch { ClearProjectLocationRemoval(); throw; }
    }
    private void PrepareProjectRemovalDependencyChecks(ProjectLocationRemoval run, Func<bool> current, Action prepared)
    {
        if (!current()) throw new InvalidOperationException("The project changed before checking dependency usage.");
        var history = SessionAuthoringHistory!; var service = PackageDependencyService();
        var checks = new List<ProjectRemovalDependency>();
        try
        {
            foreach (var project in run.Workspace.Projects.Where(project => !run.Containers.Contains(project)))
            {
                var removing = project.DirectDependencies.Where(dependency => run.Containers.Any(container =>
                    PackageDependencies.RefersTo(container.Package, dependency))).ToArray();
                if (removing.Length == 0) continue;
                var owner = GetProjectBuildSettingsDocument(project, history);
                owner.ValidateProjectFileBaseline();
                var configuration = ReferenceEquals(buildTarget, project) && buildMetadata?.Configurations.Contains(buildConfiguration) == true ? buildConfiguration : "Debug";
                var evaluations = owner.EvaluateDependencyContexts(configuration);
                var projectSnapshots = CaptureScriptProjectSnapshots();
                var snapshots = CaptureScriptSourceSnapshots(); var membership = CaptureScriptProjectMembership();
                var contexts = new List<DependencyRemovalContext>();
                var check = new ProjectRemovalDependency(project, removing, service, configuration, []);
                checks.Add(check); run.DependencyChecks = checks.ToArray();
                foreach (var evaluation in evaluations)
                {
                    var scope = service.GetRemovalScope(project, removing, projectSnapshots.Length > 0 ? null : evaluation);
                    service.ValidateAuthoredTypeUsage(project, scope, run.MixedAssetIds.ToHashSet());
                    var semantics = new ScriptSemanticWorkspace(project.FullPath.ToOSPath(), configuration, evaluation.Framework);
                    contexts.Add(new(evaluation, scope, CaptureDependencyCompilePaths(project, snapshots, configuration, evaluation).Where(path => !run.ExcludedCompilePaths.Contains(Path.GetFullPath(path))).ToArray(), semantics));
                    check = check with { Contexts = contexts.ToArray() }; checks[^1] = check; run.DependencyChecks = checks.ToArray();
                    semantics.SetProjectMembership(membership); semantics.SetProjectSnapshots(projectSnapshots);
                }
            }
            var standaloneChecks = new List<(StandalonePackage, DependencyRange[])>();
            foreach (var package in run.Workspace.Session.Projects.OfType<StandalonePackage>().Where(package => !run.Containers.Contains(package)))
            {
                var removing = package.DirectDependencies.Where(dependency => run.Containers.Any(container =>
                    PackageDependencies.RefersTo(container.Package, dependency))).ToArray();
                if (removing.Length == 0) continue;
                service.ValidateStandaloneRemoval(package, removing, run.MixedAssetIds.ToHashSet());
                var state = history.Registry.GetOrCreate(package.Package.FullPath.ToOSPath(), null,
                    () => new PackageMetadataDocument(run.Workspace, package.Package, history));
                if (!run.Leases.ContainsKey(state))
                { run.Leases.Add(state, state.AcquireHistory()); run.Revisions.Add(state, ProjectRemovalDocumentRevision(state.Document)); }
                standaloneChecks.Add((package, removing));
            }
            run.StandaloneDependencyChecks = standaloneChecks.ToArray();
            if (checks.Count == 0) { prepared(); return; }
            // Retain all actual source/project contexts used by immutable Roslyn certificates.
            foreach (var state in history.Registry.PublishedStates.Where(state => state.Document is ScriptSourceDocument or ProjectBuildSettingsDocument).ToArray())
                if (!run.Leases.ContainsKey(state))
                { run.Leases.Add(state, state.AcquireHistory()); run.Revisions.Add(state, ProjectRemovalDocumentRevision(state.Document)); }
            var sources = CaptureScriptSourceSnapshots();
            run.DependencyCancellation = new(); var token = run.DependencyCancellation.Token;
            run.DependencyTask = Task.Run(async () =>
            {
                var output = new List<ProjectRemovalDependencyResult>();
                foreach (var check in run.DependencyChecks)
                {
                    var results = new List<ScriptDependencyUsageResult>();
                    foreach (var context in check.Contexts)
                    {
                        token.ThrowIfCancellationRequested();
                        var result = await context.Semantics.FindDependencyUsage(check.Project.FullPath.ToOSPath(), sources, context.Scope, token,
                            context.Evaluation with { CompilePaths = context.CompilePaths },
                            graph => check.Service.GetRemovalScope(check.Project, check.Removing, context.Evaluation, graph), run.ExcludedCompilePaths).ConfigureAwait(false);
                        results.Add(result);
                        if (result.Usage != null) break;
                    }
                    output.Add(new(check, results.ToArray()));
                    if (results.Any(result => result.Usage != null)) break;
                }
                return output.ToArray();
            }, token);
        }
        catch { throw; }
    }

    private void PumpProjectRemovalDependencyChecks(ProjectLocationRemoval run)
    {
        if (run.DependencyTask?.IsCompleted != true) return;
        try
        {
            CompleteProjectRemovalDependencyChecks(run, () => CurrentProjectLocationRemoval(run));
            Execute(() => CommitProjectLocationRemoval(run));
        }
        catch (OperationCanceledException) { ClearProjectLocationRemoval(); }
        catch (Exception error)
        {
            outputLog.Append("Project", LogMessageType.Error, "", error.Message, error.ToString()); statusMessage = error.Message;
            ClearProjectLocationRemoval();
        }
    }

    private void CompleteProjectRemovalDependencyChecks(ProjectLocationRemoval run, Func<bool> current)
    {
            var results = run.DependencyTask.GetAwaiter().GetResult();
            run.DependencyCancellation!.Token.ThrowIfCancellationRequested();
            if (!current()) throw new InvalidOperationException("The project changed while checking dependency usage.");
            foreach (var prepared in results)
                foreach (var result in prepared.Results)
                {
                    if (result.Usage is { } usage)
                        throw new InvalidOperationException($"This dependency is used by C# source [{result.Framework}]: {usage.Path}({usage.Line},{usage.Column}): {usage.Symbol}");
                    if (prepared.Check.Contexts.Single(context => context.Evaluation.Framework == result.Framework).Semantics.Epoch != result.Epoch)
                        throw new InvalidOperationException("The project changed while checking dependency usage.");
                }
            if (results.Length != run.DependencyChecks.Length || results.Any(prepared => prepared.Results.Length != prepared.Check.Contexts.Length))
                throw new InvalidOperationException("Not every project target framework was checked.");
            run.DependencyResults = results;
            run.ReleaseDependencyChecks();
    }

    private DependencyRange[] CurrentProjectRemovalDependencies(ProjectRemovalDependency check)
        => check.Removing.Select(expected => check.Project.DirectDependencies.Single(dependency => dependency.Name == expected.Name
            && dependency.Type == expected.Type && dependency.MSBuildProject == expected.MSBuildProject
            && string.Equals(dependency.VersionRange?.ToString(), expected.VersionRange?.ToString(), StringComparison.Ordinal))).ToArray();

    private void ApplyProjectRemovalDependencies(ProjectLocationRemoval run)
    {
        foreach (var check in run.StandaloneDependencyChecks)
            PackageDependencyService().RemoveStandaloneDependencies(check.Package, check.Removing, run.MixedAssetIds.ToHashSet());
        foreach (var prepared in run.DependencyResults)
        {
            var check = prepared.Check;
            check.Service.RemoveMany(check.Project, CurrentProjectRemovalDependencies(check), scope =>
            {
                foreach (var result in prepared.Results)
                {
                    var context = check.Contexts.Single(context => context.Evaluation.Framework == result.Framework);
                    var current = GetProjectBuildSettingsDocument(check.Project, SessionAuthoringHistory!).EvaluateDependencyContexts(check.Configuration)
                        .SingleOrDefault(evaluation => evaluation.Framework == result.Framework)
                        ?? throw new InvalidOperationException("The target frameworks changed after checking dependency usage.");
                    var currentScope = check.Service.GetRemovalScope(check.Project, CurrentProjectRemovalDependencies(check), current, result.ResolvedGraph);
                    ValidateCheckedDependencyUsage(check.Project, result.ResolvedScope, currentScope, result, context.CompilePaths,
                        check.Configuration, context.Evaluation, current, run.ExcludedCompilePaths);
                }
            }, () =>
            {
                var evaluations = GetProjectBuildSettingsDocument(check.Project, SessionAuthoringHistory!).EvaluateDependencyContexts(check.Configuration);
                if (!evaluations.Select(evaluation => evaluation.Framework).ToHashSet(StringComparer.OrdinalIgnoreCase)
                    .SetEquals(check.Contexts.Select(context => context.Evaluation.Framework)))
                    throw new InvalidOperationException("The target frameworks changed after checking dependency usage.");
                return MergeDependencyRemovalScopes(evaluations.Select(evaluation => check.Service.GetRemovalScope(check.Project,
                    CurrentProjectRemovalDependencies(check), evaluation, prepared.Results.Single(result => result.Framework == evaluation.Framework).ResolvedGraph)));
            }, run.MixedAssetIds.ToHashSet());
        }
        foreach (var prepared in run.DependencyResults)
        {
            // Reuse the exact project-save byte/boundary journal that dependency
            // restore uses. It joins the same capture as declarations and membership.
            using var persisted = GetProjectBuildSettingsDocument(prepared.Check.Project, SessionAuthoringHistory!).BeginDependencyRestorePersistence();
            persisted.Save();
        }
    }
}
