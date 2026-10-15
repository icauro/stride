using System.Security.Cryptography;
using Stride.Core.Assets;
using Stride.Core.Diagnostics;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private PackageDependencyRemovalCheck? packageDependencyRemovalCheck;
    private bool closeCheckedDependencyDeletion;
    private bool IsPackageDependencyChecking => packageDependencyRemovalCheck != null || IsProjectRemovalDependencyChecking;
    private sealed record DependencyRemovalContext(ProjectDependencyEvaluation Evaluation, PackageDependencyRemovalScope Scope,
        string[] CompilePaths, ScriptSemanticWorkspace Semantics);
    private sealed class PackageDependencyRemovalCheck(ProjectWorkspace workspace, EditorSessionHistory history, SolutionProject project,
        DependencyRange dependency, PackageDependencies service, PackageDependencyRemovalScope scope, string configuration)
    {
        public readonly ProjectWorkspace Workspace = workspace;
        public readonly EditorSessionHistory History = history;
        public readonly SolutionProject Project = project;
        public readonly DependencyRange Dependency = dependency;
        public readonly PackageDependencies Service = service;
        public readonly PackageDependencyRemovalScope Scope = scope;
        public readonly string Configuration = configuration;
        public DependencyRemovalContext[] Contexts = [];
        public readonly CancellationTokenSource Cancellation = new();
        public readonly List<IDisposable> Leases = [];
        public Task<ScriptDependencyUsageResult[]>? Task;
    }
    private void RequestCheckedPackageDependencyRemoval(SolutionProject project, DependencyRange dependency)
    {
        if (IsAuthoringPipelineUpdating || projectLoading != null || assetPreviewBuild != null || IsUserProjectBuilding
            || workspace == null || SessionAuthoringHistory == null) throw new InvalidOperationException("A package operation is already running.");
        var history = SessionAuthoringHistory;
        var service = PackageDependencyService();
        var selected = ReferenceEquals(buildWorkspace, workspace) && ReferenceEquals(buildTarget, project) && buildMetadata != null;
        var configuration = selected && buildMetadata!.Configurations.Contains(buildConfiguration) ? buildConfiguration : "Debug";
        var owner = history.Registry.GetOrCreate(project.FullPath.ToOSPath(), null, () => new ProjectBuildSettingsDocument(project, history));
        owner.Document.ValidateProjectFileBaseline();
        var evaluations = owner.Document.EvaluateDependencyContexts(configuration);
        var projectSnapshots = CaptureScriptProjectSnapshots();
        var scopes = evaluations.Select(evaluation => service.GetRemovalScope(project, dependency, projectSnapshots.Length > 0 ? null : evaluation)).ToArray();
        var scope = MergeDependencyRemovalScopes(scopes);
        service.ValidateAuthoredTypeUsage(project, scope);
        var run = new PackageDependencyRemovalCheck(workspace, history, project, dependency, service, scope, configuration);
        try
        {
            run.Leases.Add(owner.AcquireHistory());
            foreach (var state in history.Registry.PublishedStates.Where(state => state.Document is ScriptSourceDocument { IsCSharpSource: true }))
                run.Leases.Add(state.AcquireHistory());
            var snapshots = CaptureScriptSourceSnapshots();
            var membership = CaptureScriptProjectMembership();
            var contexts = new List<DependencyRemovalContext>();
            foreach (var evaluation in evaluations)
            {
                var semantics = new ScriptSemanticWorkspace(project.FullPath.ToOSPath(), configuration, evaluation.Framework);
                semantics.SetProjectMembership(membership);
                semantics.SetProjectSnapshots(projectSnapshots);
                contexts.Add(new(evaluation, scopes[contexts.Count], CaptureDependencyCompilePaths(project, snapshots, configuration, evaluation), semantics));
                run.Contexts = contexts.ToArray();
            }
            var token = run.Cancellation.Token;
            run.Task = Task.Run(async () =>
            {
                var results = new List<ScriptDependencyUsageResult>();
                foreach (var context in run.Contexts)
                {
                    var result = await context.Semantics.FindDependencyUsage(project.FullPath.ToOSPath(), snapshots, context.Scope, token,
                        context.Evaluation with { CompilePaths = context.CompilePaths },
                        graph => run.Service.GetRemovalScope(project, dependency, context.Evaluation, graph)).ConfigureAwait(false);
                    results.Add(result);
                    if (result.Usage != null) break;
                }
                return results.ToArray();
            }, token);
            packageDependencyRemovalCheck = run;
        }
        catch { foreach (var context in run.Contexts) context.Semantics.Dispose(); run.Cancellation.Dispose(); foreach (var lease in run.Leases) lease.Dispose(); throw; }
    }
    private void PumpPackageDependencyRemovalCheck()
    {
        var run = packageDependencyRemovalCheck;
        if (run?.Task?.IsCompleted != true) return;
        try
        {
            var results = run.Task.GetAwaiter().GetResult();
            run.Cancellation.Token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(workspace, run.Workspace) || !ReferenceEquals(SessionAuthoringHistory, run.History)
                || results.Any(result => run.Contexts.Single(context => context.Evaluation.Framework == result.Framework).Semantics.Epoch != result.Epoch))
                throw new InvalidOperationException("The project changed while checking dependency usage.");
            foreach (var result in results)
                if (result.Usage is { } usage)
                    throw new InvalidOperationException($"This dependency is used by C# source [{result.Framework}]: {usage.Path}({usage.Line},{usage.Column}): {usage.Symbol}");
            if (results.Length != run.Contexts.Length) throw new InvalidOperationException("Not every target framework was checked.");
            run.Service.Remove(run.Project, run.Dependency, scope =>
            {
                foreach (var result in results)
                {
                    var context = run.Contexts.Single(context => context.Evaluation.Framework == result.Framework);
                    var current = GetProjectBuildSettingsDocument(run.Project, run.History).EvaluateDependencyContexts(run.Configuration)
                        .SingleOrDefault(evaluation => evaluation.Framework == result.Framework) ?? throw new InvalidOperationException("The target frameworks changed after checking dependency usage.");
                    var dependency = run.Project.DirectDependencies.Single(candidate => candidate.Name == run.Dependency.Name && candidate.Type == run.Dependency.Type && candidate.MSBuildProject == run.Dependency.MSBuildProject);
                    var currentScope = run.Service.GetRemovalScope(run.Project, dependency, current, result.ResolvedGraph);
                    ValidateCheckedDependencyUsage(run.Project, result.ResolvedScope, currentScope, result, context.CompilePaths, run.Configuration, context.Evaluation, current);
                }
            }, () =>
            {
                var current = GetProjectBuildSettingsDocument(run.Project, run.History).EvaluateDependencyContexts(run.Configuration);
                if (!current.Select(evaluation => evaluation.Framework).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(run.Contexts.Select(context => context.Evaluation.Framework)))
                    throw new InvalidOperationException("The target frameworks changed after checking dependency usage.");
                var dependency = run.Project.DirectDependencies.Single(candidate => candidate.Name == run.Dependency.Name && candidate.Type == run.Dependency.Type && candidate.MSBuildProject == run.Dependency.MSBuildProject);
                return MergeDependencyRemovalScopes(current.Select(evaluation => run.Service.GetRemovalScope(run.Project, dependency, evaluation,
                    results.Single(result => result.Framework == evaluation.Framework).ResolvedGraph)));
            });
            removingPackageDependency = null; dependencyToRemove = null; selectedDependencyReference = null;
            closeCheckedDependencyDeletion = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            outputLog.Append("Project", LogMessageType.Error, "", error.Message, error.ToString()); statusMessage = error.Message;
        }
        finally
        {
            foreach (var context in run.Contexts) context.Semantics.Dispose(); run.Cancellation.Dispose();
            foreach (var lease in run.Leases) lease.Dispose();
            packageDependencyRemovalCheck = null;
        }
    }
    private void ValidateCheckedDependencyUsage(SolutionProject project, PackageDependencyRemovalScope expectedScope,
        PackageDependencyRemovalScope scope, ScriptDependencyUsageResult result, string[] compilePaths, string configuration,
        ProjectDependencyEvaluation expectedEvaluation, ProjectDependencyEvaluation currentEvaluation, IReadOnlySet<string>? excludedCompilePaths = null)
    {
        if (!scope.AssemblyPaths.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(expectedScope.AssemblyPaths)
            || !scope.ProjectPaths.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(expectedScope.ProjectPaths))
            throw new InvalidOperationException("The resolved dependency removal scope changed.");
        foreach (var input in result.Inputs)
        {
            using var stream = new FileStream(input.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (!SHA256.HashData(stream).AsSpan().SequenceEqual(input.Hash)) throw new InvalidOperationException("The C# project inputs changed after checking dependency usage.");
        }
        var snapshots = CaptureScriptSourceSnapshots();
        foreach (var expected in result.ProjectSnapshots)
        {
            var owner = SessionAuthoringHistory!.Registry.PublishedStates.Select(state => state.Document).OfType<ProjectBuildSettingsDocument>()
                .SingleOrDefault(document => string.Equals(document.Path, expected.Path, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("An authored project context is no longer retained.");
            var current = owner.CaptureProjectSnapshot();
            if (current.Xml != expected.Xml) throw new InvalidOperationException("The authored project XML changed after checking dependency usage.");
        }
        if (!AuthoredProjectProperties.SameReferenceAndCompilerContext(currentEvaluation, expectedEvaluation))
            throw new InvalidOperationException("The evaluated project references changed after checking dependency usage.");
        if (!compilePaths.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(CaptureDependencyCompilePaths(project, snapshots, configuration, currentEvaluation).Where(path => excludedCompilePaths?.Contains(Path.GetFullPath(path)) != true)))
            throw new InvalidOperationException("The C# compile source set changed after checking dependency usage.");
        var sources = snapshots.ToDictionary(source => source.Path, StringComparer.OrdinalIgnoreCase);
        foreach (var source in result.Sources)
            if (!result.GeneratedSources.Contains(source.Path, StringComparer.OrdinalIgnoreCase))
            if (!(sources.TryGetValue(source.Path, out var current) ? current.Text : File.ReadAllText(source.Path)).Equals(source.Text, StringComparison.Ordinal))
                throw new InvalidOperationException("The C# source changed after checking dependency usage.");
        var membership = CaptureScriptProjectMembership();
        foreach (var expected in result.Membership)
        {
            var current = membership.SingleOrDefault(value => string.Equals(value.ProjectPath, expected.ProjectPath, StringComparison.OrdinalIgnoreCase));
            if (current == null || !current.Items.SequenceEqual(expected.Items)) throw new InvalidOperationException("The source compile membership changed after checking dependency usage.");
        }
        GetProjectBuildSettingsDocument(project, SessionAuthoringHistory!).ValidateProjectFileBaseline();
    }
    private static PackageDependencyRemovalScope MergeDependencyRemovalScopes(IEnumerable<PackageDependencyRemovalScope> scopes)
    {
        var values = scopes.ToArray();
        return new(values.SelectMany(value => value.AssemblyPaths).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            values.SelectMany(value => value.ProjectPaths).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), values.SelectMany(value => value.LoadedAssemblies).Distinct().ToArray());
    }
    private string[] CaptureDependencyCompilePaths(SolutionProject project, ScriptSourceSnapshot[] snapshots, string configuration, ProjectDependencyEvaluation evaluation)
    {
        var owner = GetProjectBuildSettingsDocument(project, SessionAuthoringHistory!);
        var paths = evaluation.CompilePaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var source in snapshots.Where(source => source.Created && string.Equals(source.ProjectPath, owner.Path, StringComparison.OrdinalIgnoreCase)))
            if (owner.IsProjectItemIncluded("Compile", Path.GetRelativePath(Path.GetDirectoryName(owner.Path)!, source.Path), configuration, evaluation.Framework)) paths.Add(source.Path);
        return paths.ToArray();
    }
    private void ClearPackageDependencyRemovalCheckState()
    {
        if (IsPackageDependencyChecking) throw new InvalidOperationException("The dependency usage check has not finished.");
        closeCheckedDependencyDeletion = false;
    }
    private void FinishPackageDependencyRemovalCheckForShutdown()
    {
        // This same shutdown path also owns the root-removal dependency worker.
        // Retire its real readers/leases before any canonical session can dispose.
        ClearProjectLocationRemoval();
        var run = packageDependencyRemovalCheck;
        if (run == null) return;
        run.Cancellation.Cancel();
        while (packageDependencyRemovalCheck != null)
        {
            PumpPackageDependencyRemovalCheck();
            try { run.Task?.Wait(25); }
            catch (AggregateException) { /* The pump observes cancellation/failure and releases owner-thread leases. */ }
        }
    }
}
