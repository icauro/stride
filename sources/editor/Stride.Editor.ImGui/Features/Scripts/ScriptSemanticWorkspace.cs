using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Classification;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;
using Stride.Core.Assets;

namespace Stride.Editor.ImGui;

internal sealed record ScriptSourceSnapshot(string Path, long Version, string Text, string? ProjectPath = null, string? Name = null, string[]? Folders = null, bool Created = false, bool CanRelocate = false);
internal sealed record ScriptProjectMembershipSnapshot(string ProjectPath, ProjectCompileItemOverride[] Items, long Revision);
internal sealed record ScriptProjectFileBaseline(string Path, byte[] Hash);
internal sealed record ScriptSemanticColor(TextSpan Span, uint Color);
internal sealed record ScriptSemanticIssue(TextSpan Span, DiagnosticSeverity Severity, string Message);
internal sealed record ScriptSemanticResult(ScriptSourceSnapshot Source, CSharpParseOptions ParseOptions, ScriptSemanticColor[] Colors, ScriptSemanticIssue[] Issues, ScriptDiagnosticReport[] Diagnostics);

/// <summary>Actual MSBuild project context, with immutable overlays for every retained source buffer.</summary>
internal sealed partial class ScriptSemanticWorkspace : IDisposable
{
    private readonly string projectPath, configuration, framework;
    private readonly SemaphoreSlim gate = new(1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly List<FileSystemWatcher> watchers = [];
    private MSBuildWorkspace? workspace;
    internal object? DesignTimeLoadEvidence { get; private set; }
    internal object? NativeCompositionEvidence => codeActionProviders?.NativeCompositionEvidence;
    private Solution? baseline;
    private long epoch, loadedNativeVersion = -1;
    private bool disposed;
    private ScriptProjectMembershipSnapshot[] authoredMembership = [];
    private ScriptProjectFileBaseline[] loadedProjectFiles = [];
    public long Epoch { get { ScheduleWatchRecovery(); return Interlocked.Read(ref epoch); } }
    public void SetProjectMembership(ScriptProjectMembershipSnapshot[] next)
    {
        var previous = Volatile.Read(ref authoredMembership);
        if (previous.Length == next.Length && previous.Zip(next).All(pair => pair.First.ProjectPath == pair.Second.ProjectPath && pair.First.Revision == pair.Second.Revision && pair.First.Items.SequenceEqual(pair.Second.Items))) return;
        Volatile.Write(ref authoredMembership, next);
        Interlocked.Increment(ref epoch);
    }

    public ScriptSemanticWorkspace(string projectPath, string configuration, string framework)
    {
        this.projectPath = Path.GetFullPath(projectPath);
        this.configuration = configuration; this.framework = framework;
        ObserveProjectOrigin(this.projectPath);
    }

    public async Task<ScriptSemanticResult> Analyze(ScriptSourceSnapshot[] snapshots, string targetPath, CancellationToken cancellation)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, lifetime.Token);
        var token = linked.Token;
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested();
            await WaitForQuiet(token).ConfigureAwait(false);
            var expectedEpoch = Epoch;
            var (target, selected) = await PrepareDocument(snapshots, targetPath, expectedEpoch, token).ConfigureAwait(false);
            using var analyzers = CreateAnalyzerRun(target.Project, token, liveAnalysis: true);
            var tree = await target.GetSyntaxTreeAsync(token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The C# project returned no source syntax tree.");
            var model = await target.GetSemanticModelAsync(token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The C# project returned no semantic model.");
            var colors = new List<ScriptSemanticColor>();
            var classified = await Classifier.GetClassifiedSpansAsync(target, new TextSpan(0, selected.Text.Length), token).ConfigureAwait(false);
            foreach (var span in classified)
            {
                token.ThrowIfCancellationRequested();
                var color = ClassificationColor(span.ClassificationType);
                if (color != 0) colors.Add(new ScriptSemanticColor(span.TextSpan, color));
            }
            var compilerDiagnostics = model.GetDiagnostics(cancellationToken: token);
            var analyzerDiagnostics = await analyzers.Run(model.Compilation, token).ConfigureAwait(false);
            var visible = compilerDiagnostics.Concat(analyzerDiagnostics).Where(diagnostic => !diagnostic.IsSuppressed && diagnostic.Severity != DiagnosticSeverity.Hidden).ToArray();
            var issues = visible
                .Where(diagnostic => !diagnostic.IsSuppressed && diagnostic.Severity != DiagnosticSeverity.Hidden
                    && diagnostic.Location.SourceTree == tree)
                .Select(diagnostic => new ScriptSemanticIssue(diagnostic.Location.SourceSpan, diagnostic.Severity, diagnostic.GetMessage())).Distinct().ToArray();
            var reports = visible.Select(diagnostic => DescribeAuthoredDiagnostic(diagnostic, target.Project.Solution)).Concat(analyzers.Failures).Distinct().ToArray();
            token.ThrowIfCancellationRequested();
            if (Epoch != expectedEpoch) throw new OperationCanceledException("The project source context changed.", token);
            return new(selected, (CSharpParseOptions)tree.Options, colors.ToArray(), issues, reports);
        }
        finally { gate.Release(); }
    }

    private async Task<Solution> PrepareSolution(ScriptSourceSnapshot[] snapshots, long expectedEpoch, CancellationToken cancellation)
    {
        if (baseline == null || loadedNativeVersion != NativeContextVersion) await Load(expectedEpoch, cancellation).ConfigureAwait(false);
        var solution = ApplyReferenceContexts(WithEditorAnalyzers(baseline!));
        foreach (var membership in Volatile.Read(ref authoredMembership))
            foreach (var owner in solution.Projects.Where(project => project.FilePath != null && string.Equals(Path.GetFullPath(project.FilePath), membership.ProjectPath, StringComparison.OrdinalIgnoreCase)).ToArray())
                foreach (var item in membership.Items.Where(item => !item.Included))
                {
                    var path = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(membership.ProjectPath)!, item.Include));
                    foreach (var document in owner.Documents.Where(document => AuthoredSourcePath(document) is { } source && string.Equals(Path.GetFullPath(source), path, StringComparison.OrdinalIgnoreCase)))
                        solution = solution.RemoveDocument(document.Id);
                }
        var documents = solution.Projects.SelectMany(project => project.Documents).Where(document => AuthoredSourcePath(document) != null)
            .GroupBy(document => Path.GetFullPath(AuthoredSourcePath(document)!), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Select(document => document.Id).ToArray(), StringComparer.OrdinalIgnoreCase);
        foreach (var snapshot in snapshots)
        {
            if (snapshot.ProjectPath != null && Volatile.Read(ref authoredMembership).Any(membership => string.Equals(membership.ProjectPath, snapshot.ProjectPath, StringComparison.OrdinalIgnoreCase)
                && membership.Items.Any(item => !item.Included && string.Equals(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(membership.ProjectPath)!, item.Include)), snapshot.Path, StringComparison.OrdinalIgnoreCase)))) continue;
            if (!documents.ContainsKey(snapshot.Path) && snapshot.Created && snapshot.ProjectPath != null)
            {
                var owners = solution.Projects.Where(project => project.FilePath != null && string.Equals(Path.GetFullPath(project.FilePath), snapshot.ProjectPath, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (owners.Length == 0) throw new InvalidOperationException("The authored source's actual project is not loaded.");
                var newIds = new List<DocumentId>();
                foreach (var owner in owners)
                {
                    var id = DocumentId.CreateNewId(owner.Id, snapshot.Path);
                    solution = solution.AddDocument(id, snapshot.Name ?? Path.GetFileName(snapshot.Path), SourceText.From(snapshot.Text, Encoding.UTF8), folders: snapshot.Folders, filePath: snapshot.Path);
                    newIds.Add(id);
                }
                documents.Add(snapshot.Path, newIds.ToArray());
            }
            if (documents.TryGetValue(snapshot.Path, out var ids))
                foreach (var id in ids) solution = solution.WithDocumentText(id, SourceText.From(snapshot.Text, Encoding.UTF8));
        }
        return solution;
    }

    private async Task<(Document Document, ScriptSourceSnapshot Snapshot)> PrepareDocument(ScriptSourceSnapshot[] snapshots, string targetPath, long expectedEpoch, CancellationToken cancellation)
    {
        if (metadataDocuments.TryGetValue(Path.GetFullPath(targetPath), out var metadata))
        {
            var snapshot = snapshots.Single(snapshot => string.Equals(snapshot.Path, targetPath, StringComparison.OrdinalIgnoreCase));
            return (metadata.Document.WithText(SourceText.From(snapshot.Text)), snapshot);
        }
        var solution = await PrepareSolution(snapshots, expectedEpoch, cancellation).ConfigureAwait(false);
        var targets = solution.Projects.SelectMany(project => project.Documents)
            .Where(document => AuthoredSourcePath(document) is { } source && string.Equals(Path.GetFullPath(source), targetPath, StringComparison.OrdinalIgnoreCase)).Select(document => document.Id).ToArray();
        if (targets.Length == 0) throw new InvalidOperationException("The source file is not included in the loaded C# project.");
        var distances = ProjectDistances(solution);
        var selectedTarget = targets.OrderBy(id => distances.GetValueOrDefault(id.ProjectId, int.MaxValue)).First();
        return (solution.GetDocument(selectedTarget)!, snapshots.Single(snapshot => string.Equals(snapshot.Path, targetPath, StringComparison.OrdinalIgnoreCase)));
    }

    private async Task Load(long expectedEpoch, CancellationToken cancellation)
    {
        RecoverWatchers();
        var loadingNativeVersion = NativeContextVersion;
        if (Volatile.Read(ref authoredProjects).Length > 0)
        {
            await LoadAuthored(expectedEpoch, loadingNativeVersion, cancellation).ConfigureAwait(false);
            return;
        }
        ObserveLiteralImports(projectPath);
        ObserveEvaluatedImportPatterns(projectPath, framework);
        var initialInputs = new List<ScriptProjectFileBaseline>();
        try
        {
            foreach (var path in await ScriptProjectWatchPaths.Read(projectPath, configuration, framework, cancellation).ConfigureAwait(false))
            {
                if (path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)) ObserveLiteralImports(path); else ObserveFile(path);
                if (File.Exists(path)) initialInputs.Add(new(path, System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(path, cancellation).ConfigureAwait(false))));
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is IOException or TimeoutException or System.Text.Json.JsonException)
        { Console.Error.WriteLine("Script evaluated-import observation failed: " + error.Message); }
        var properties = new Dictionary<string, string>
        {
            ["Configuration"] = configuration,
            ["StrideSkipAutoPack"] = "true",
        };
        if (framework.Length > 0) properties["TargetFramework"] = framework;
        codeActionProviders ??= new ScriptCodeActionProviders();
        var next = MSBuildWorkspace.Create(properties, codeActionProviders.Host);
        next.LoadMetadataForReferencedProjects = false;
        try
        {
            await next.OpenProjectAsync(projectPath, cancellationToken: cancellation).ConfigureAwait(false);
            InstallWatchers(next.CurrentSolution);
            var failures = next.Diagnostics.Where(diagnostic => diagnostic.Kind == WorkspaceDiagnosticKind.Failure).ToArray();
            if (failures.Length > 0) CertifyDesignTimeWarnings(next, failures, cancellation);
            var inputPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var policies = new Dictionary<ProjectId, ScriptAnalyzerPolicy>();
            foreach (var path in next.CurrentSolution.Projects.Where(project => project.FilePath != null).Select(project => Path.GetFullPath(project.FilePath!)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                ObserveEvaluatedImportPatterns(path, string.Equals(path, projectPath, StringComparison.OrdinalIgnoreCase) ? framework : "");
                var evaluated = await ScriptProjectWatchPaths.Evaluate(path, configuration,
                    string.Equals(path, projectPath, StringComparison.OrdinalIgnoreCase) ? framework : "", cancellation).ConfigureAwait(false);
                inputPaths.UnionWith(evaluated.Paths);
                foreach (var project in next.CurrentSolution.Projects.Where(project => project.FilePath != null
                    && string.Equals(Path.GetFullPath(project.FilePath), path, StringComparison.OrdinalIgnoreCase)))
                {
                    var targets = (evaluated.Targets ?? []).Where(target => project.OutputFilePath != null
                        && string.Equals(Path.GetFullPath(project.OutputFilePath), target.OutputPath, StringComparison.OrdinalIgnoreCase)).ToArray();
                    policies.Add(project.Id, targets.Length == 1 ? new(targets[0].RunAnalyzers, targets[0].RunAnalyzersDuringLiveAnalysis)
                        : new(evaluated.RunAnalyzers, evaluated.RunAnalyzersDuringLiveAnalysis));
                }
            }
            foreach (var path in inputPaths) ObserveFile(path);
            var projectFiles = new List<ScriptProjectFileBaseline>();
            foreach (var path in inputPaths.Where(File.Exists))
                projectFiles.Add(new(path, System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(path, cancellation).ConfigureAwait(false))));
            foreach (var input in initialInputs)
            {
                using var file = new FileStream(input.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (!System.Security.Cryptography.SHA256.HashData(file).AsSpan().SequenceEqual(input.Hash))
                { Invalidate(); throw new OperationCanceledException("A project input changed during context loading.", cancellation); }
            }
            cancellation.ThrowIfCancellationRequested();
            ReleaseAuthoredWorkspace(); workspace = next; baseline = next.CurrentSolution; loadedNativeVersion = loadingNativeVersion; loadedProjectFiles = projectFiles.ToArray();
            nativeContextGeneration = new(() => DisposeNativeContext(next.Dispose));
            RefreshReferenceContextLeases();
            analyzerPolicies = policies;
        }
        catch { try { InstallWatchers(next.CurrentSolution); } finally { next.Dispose(); } throw; }
    }

    private Dictionary<ProjectId, int> ProjectDistances(Solution solution)
    {
        var distances = new Dictionary<ProjectId, int>(); var pending = new Queue<ProjectId>();
        foreach (var project in solution.Projects.Where(project => string.Equals(project.FilePath, projectPath, StringComparison.OrdinalIgnoreCase)))
        { distances[project.Id] = 0; pending.Enqueue(project.Id); }
        while (pending.TryDequeue(out var id))
        {
            foreach (var reference in solution.GetProject(id)!.ProjectReferences)
                if (!distances.ContainsKey(reference.ProjectId)) { distances[reference.ProjectId] = distances[id] + 1; pending.Enqueue(reference.ProjectId); }
        }
        return distances;
    }

    // Roslyn's MSBuild DiagnosticReporter publishes both native warnings and errors as
    // WorkspaceDiagnosticKind.Failure. Recheck their original severity without changing
    // the project's warning, audit, restore or compiler policy.
    private void CertifyDesignTimeWarnings(MSBuildWorkspace next, WorkspaceDiagnostic[] failures, CancellationToken cancellation)
    {
        var logger = new DesignTimeSeverityLogger();
        var certificates = new List<object>();
        var ready = true;
        using var collection = new Microsoft.Build.Evaluation.ProjectCollection();
        using var manager = new Microsoft.Build.Execution.BuildManager();
        manager.BeginBuild(new Microsoft.Build.Execution.BuildParameters(collection)
        { Loggers = [logger], DisableInProcNode = true, EnableNodeReuse = false });
        using var registration = cancellation.Register(manager.CancelAllSubmissions);
        try
        {
            foreach (var project in next.CurrentSolution.Projects)
            {
                cancellation.ThrowIfCancellationRequested();
                if (project.FilePath is not { } path) { ready = false; continue; }
                var properties = new Dictionary<string, string>
                {
                    ["Configuration"] = configuration, ["StrideSkipAutoPack"] = "true",
                    ["DesignTimeBuild"] = "true", ["BuildingInsideVisualStudio"] = "true",
                    ["BuildProjectReferences"] = "false", ["BuildingProject"] = "false",
                    ["ProvideCommandLineArgs"] = "true", ["SkipCompilerExecution"] = "true",
                    ["ContinueOnError"] = "ErrorAndContinue", ["NonExistentFile"] = "__NonExistentSubDir__\\__NonExistentFile__",
                };
                if (framework.Length > 0) properties["TargetFramework"] = framework;
                var instance = new Microsoft.Build.Execution.ProjectInstance(path, properties, null, collection);
                var result = manager.BuildRequest(new Microsoft.Build.Execution.BuildRequestData(instance,
                    ["Compile", "CoreCompile"], null, Microsoft.Build.Execution.BuildRequestDataFlags.ProvideProjectStateAfterBuild));
                var state = result.ProjectStateAfterBuild;
                var arguments = state?.GetItems("CscCommandLineArgs").Select(item => item.EvaluatedInclude).ToArray() ?? [];
                var references = project.MetadataReferences.OfType<PortableExecutableReference>().Select(reference => reference.FilePath).ToArray();
                var projectReferencesReady = project.ProjectReferences.All(reference => next.CurrentSolution.GetProject(reference.ProjectId) != null);
                var projectReady = result.OverallResult == Microsoft.Build.Execution.BuildResultCode.Success
                    && arguments.Length > 0 && references.Length > 0 && references.All(path => path != null && File.Exists(path))
                    && projectReferencesReady && project.CompilationOptions != null && project.ParseOptions != null;
                ready &= projectReady;
                certificates.Add(new { projectPath = path, result = result.OverallResult.ToString(),
                    compilerArgumentCount = arguments.Length, metadataReferences = references, projectReferencesReady, ready = projectReady });
            }
        }
        finally { manager.EndBuild(); }
        cancellation.ThrowIfCancellationRequested();
        var events = logger.Events.ToArray();
        var unmatched = failures.Where(diagnostic => !events.Any(native => native.Severity == "Warning"
            && diagnostic.Message.Contains("'" + native.ProjectPath + "'", StringComparison.OrdinalIgnoreCase)
            && diagnostic.Message.EndsWith("with message: " + native.Message, StringComparison.Ordinal))).ToArray();
        var passed = ready && events.All(native => native.Severity != "Error") && unmatched.Length == 0;
        DesignTimeLoadEvidence = new { passed, certificates, nativeEvents = events,
            workspaceDiagnostics = failures.Select(diagnostic => new { kind = diagnostic.Kind.ToString(), diagnostic.Message }).ToArray(),
            unmatched = unmatched.Select(diagnostic => diagnostic.Message).ToArray() };
        foreach (var native in events) Console.Error.WriteLine($"Script design-time {native.Severity} {native.Code}: {native.Message}");
        if (!passed) throw new InvalidOperationException(string.Join(Environment.NewLine, failures.Select(diagnostic => diagnostic.Message)));
    }

    private sealed record DesignTimeSeverityEvent(string Severity, string Code, string ProjectPath, string Message);
    private sealed class DesignTimeSeverityLogger : Microsoft.Build.Framework.ILogger
    {
        public readonly System.Collections.Concurrent.ConcurrentQueue<DesignTimeSeverityEvent> Events = new();
        public Microsoft.Build.Framework.LoggerVerbosity Verbosity { get; set; } = Microsoft.Build.Framework.LoggerVerbosity.Normal;
        public string? Parameters { get; set; }
        private Microsoft.Build.Framework.IEventSource? source;
        public void Initialize(Microsoft.Build.Framework.IEventSource eventSource)
        { source = eventSource; source.WarningRaised += Warning; source.ErrorRaised += Error; }
        private void Warning(object sender, Microsoft.Build.Framework.BuildWarningEventArgs item)
            => Events.Enqueue(new("Warning", item.Code ?? "", item.ProjectFile ?? "", item.Message ?? ""));
        private void Error(object sender, Microsoft.Build.Framework.BuildErrorEventArgs item)
            => Events.Enqueue(new("Error", item.Code ?? "", item.ProjectFile ?? "", item.Message ?? ""));
        public void Shutdown()
        { if (source != null) { source.WarningRaised -= Warning; source.ErrorRaised -= Error; source = null; } }
    }

    private static uint ClassificationColor(string classification)
    {
        if (classification.StartsWith("xml doc comment", StringComparison.Ordinal)) return Rgb(87, 166, 74);
        return classification switch
        {
            ClassificationTypeNames.Keyword or ClassificationTypeNames.ControlKeyword => Rgb(86, 156, 214),
            ClassificationTypeNames.StringLiteral or ClassificationTypeNames.VerbatimStringLiteral or ClassificationTypeNames.StringEscapeCharacter => Rgb(214, 157, 133),
            ClassificationTypeNames.NumericLiteral => Rgb(181, 206, 168),
            ClassificationTypeNames.Comment => Rgb(87, 166, 74),
            ClassificationTypeNames.PreprocessorKeyword => Rgb(155, 155, 155),
            ClassificationTypeNames.ExcludedCode => Rgb(120, 120, 120),
            ClassificationTypeNames.ClassName or ClassificationTypeNames.RecordClassName or ClassificationTypeNames.StructName
                or ClassificationTypeNames.RecordStructName or ClassificationTypeNames.InterfaceName or ClassificationTypeNames.EnumName
                or ClassificationTypeNames.DelegateName or ClassificationTypeNames.TypeParameterName or ClassificationTypeNames.MethodName
                or ClassificationTypeNames.ExtensionMethodName => Rgb(78, 201, 176),
            _ => 0,
        };
    }
    private static uint Rgb(byte red, byte green, byte blue) => 0xff000000u | (uint)blue << 16 | (uint)green << 8 | red;

    public void Dispose()
    {
        lock (watchGate) { if (disposed) return; disposed = true; }
        lifetime.Cancel();
        // Workspace disposal is serialized after outstanding analysis has released its immutable snapshots.
        _ = DisposeAfterWork();
    }
    private async Task DisposeAfterWork()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (watchGate) { foreach (var watcher in watchers) watcher.Dispose(); watchers.Clear(); watchKeys.Clear(); projectWatchRoots.Clear(); }
            ReleaseMetadataDocuments();
            ReleaseReferenceContexts(); ReleaseAuthoredWorkspace(); baseline = null; codeActionProviders?.Dispose(); codeActionProviders = null;
            editorAnalyzerReferences = null; editorAnalyzerLoader?.Dispose(); editorAnalyzerLoader = null;
        }
        catch (Exception error) { Console.Error.WriteLine("Script workspace disposal failed: " + error); }
        finally { gate.Release(); lifetime.Dispose(); }
    }
}
