using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Stride.Editor.ImGui;

internal sealed record ScriptDiagnosticReport(string Code, DiagnosticSeverity Severity, string Message, string? Path, int Line, int Column);
internal sealed record ScriptAnalyzerPolicy(bool? RunAnalyzers, bool? RunAnalyzersDuringLiveAnalysis);

/// <summary>Runs only analyzer instances and options supplied by the actual loaded project/solution.</summary>
internal sealed class ScriptAnalyzerRun : IDisposable
{
    private readonly Project project;
    private readonly AnalyzerFileReference[] files;
    private readonly ConcurrentQueue<ScriptDiagnosticReport> failures = new();
    private readonly ImmutableArray<DiagnosticAnalyzer> analyzers;
    public IReadOnlyCollection<ScriptDiagnosticReport> Failures => failures.ToArray();

    public ScriptAnalyzerRun(Project project, CancellationToken cancellation, bool liveAnalysis = false, ScriptAnalyzerPolicy? evaluated = null)
    {
        this.project = project;
        var global = project.AnalyzerOptions.AnalyzerConfigOptionsProvider.GlobalOptions;
        bool Disabled(string key) => global.TryGetValue(key, out var value) && bool.TryParse(value, out var enabled) && !enabled;
        if (evaluated?.RunAnalyzers == false || Disabled("build_property.RunAnalyzers")
            || liveAnalysis && (evaluated?.RunAnalyzersDuringLiveAnalysis == false || Disabled("build_property.RunAnalyzersDuringLiveAnalysis")))
        { files = []; analyzers = []; return; }
        var references = project.AnalyzerReferences.Concat(project.Solution.AnalyzerReferences).Distinct().ToArray();
        files = references.OfType<AnalyzerFileReference>().ToArray();
        foreach (var file in files) file.AnalyzerLoadFailed += Failed;
        var loaded = new List<DiagnosticAnalyzer>();
        try
        {
            foreach (var reference in references)
            {
                cancellation.ThrowIfCancellationRequested();
                try { loaded.AddRange(reference.GetAnalyzers(project.Language)); }
                catch (OperationCanceledException) { throw; }
                catch (Exception error) { failures.Enqueue(new("", DiagnosticSeverity.Warning, reference.Display + ": " + error.Message, null, 0, 0)); }
            }
            analyzers = loaded.GroupBy(analyzer => analyzer.GetType()).Select(group => group.First()).ToImmutableArray();
        }
        catch { Dispose(); throw; }
    }
    private void Failed(object? sender, AnalyzerLoadFailureEventArgs error) => failures.Enqueue(new("", DiagnosticSeverity.Warning, error.Message, null, 0, 0));
    public async Task<ImmutableArray<Diagnostic>> Run(Compilation compilation, CancellationToken cancellation)
    {
        if (analyzers.IsDefaultOrEmpty) return [];
        var options = new CompilationWithAnalyzersOptions(project.AnalyzerOptions, onAnalyzerException: null,
            concurrentAnalysis: true, logAnalyzerExecutionTime: false, reportSuppressedDiagnostics: false);
        try { return await compilation.WithAnalyzers(analyzers, options).GetAnalyzerDiagnosticsAsync(cancellation).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            failures.Enqueue(new("", DiagnosticSeverity.Warning, error.Message, null, 0, 0));
            return [];
        }
    }
    public void Dispose() { foreach (var file in files) file.AnalyzerLoadFailed -= Failed; }

    public static ScriptDiagnosticReport Describe(Diagnostic diagnostic)
    {
        if (!diagnostic.Location.IsInSource) return new(diagnostic.Id, diagnostic.Severity, diagnostic.GetMessage(), null, 0, 0);
        var span = diagnostic.Location.GetLineSpan();
        return new(diagnostic.Id, diagnostic.Severity, diagnostic.GetMessage(), span.Path.Length == 0 ? null : span.Path,
            span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1);
    }
}
